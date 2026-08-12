using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Devices;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Devices;

internal sealed class DeterministicDecoderEvaluator(IDeclarativeDecoder decoder) : IDecoderEvaluator
{
    public DomainResult<DecoderEvaluationEvidence> Evaluate(
        DeclarativeDecoderDefinition definition,
        DecoderEvaluationSuite suite)
    {
        if (definition is null || !definition.IsValid() || !Valid(suite))
            return DomainResult.Fail<DecoderEvaluationEvidence>(new(FailureCode.ValidationFailure, "Decoder evaluation suite is invalid."));
        var target = CasesPass(definition, suite.TargetCases, suite.MaximumOperationsPerByte, out var targetUnknown);
        var holdout = CasesPass(definition, suite.HoldoutCases, suite.MaximumOperationsPerByte, out var holdoutUnknown);
        var sample = suite.TargetCases[0].Input;
        var partialInput = sample[..Math.Min(sample.Length, Math.Max(1, definition.FrameLength - 1))];
        var partial = decoder.Decode(definition, partialInput.AsMemory());
        var partialPassed = partial.IsSuccess && partial.Value.Frames.Length == 0 &&
            partial.Value.UnframedSegments.Sum(segment => segment.Bytes.Length) == partialInput.Length;
        var concatenatedInput = sample.Concat(sample).ToImmutableArray();
        var concatenated = decoder.Decode(definition, concatenatedInput.AsMemory());
        var expectedSingle = decoder.Decode(definition, sample.AsMemory());
        var concatenatedPassed = concatenated.IsSuccess && expectedSingle.IsSuccess &&
            concatenated.Value.Frames.Length == expectedSingle.Value.Frames.Length * 2;
        var resyncInput = new byte[] { 0x13, 0x37, 0x42 }.Concat(sample).ToImmutableArray();
        var resync = decoder.Decode(definition, resyncInput.AsMemory());
        var resyncPassed = resync.IsSuccess && expectedSingle.IsSuccess &&
            resync.Value.Frames.Length == expectedSingle.Value.Frames.Length &&
            resync.Value.UnframedSegments.Sum(segment => segment.Bytes.Length) >= 3;
        var malformed = decoder.Decode(definition, definition.SyncPrefix[..Math.Min(definition.SyncPrefix.Length, definition.FrameLength - 1)].AsMemory());
        var malformedPassed = malformed.IsSuccess && malformed.Value.Frames.Length == 0;
        var fuzzPassed = FuzzPasses(definition, suite);
        var performancePassed = target && holdout && fuzzPassed;
        var unknown = targetUnknown && holdoutUnknown;
        var passed = target && holdout && partialPassed && concatenatedPassed && resyncPassed &&
            malformedPassed && fuzzPassed && performancePassed && unknown;
        var seed = string.Join('\n', definition.DefinitionHash, suite.SuiteHash, suite.TargetCases.Length,
            suite.HoldoutCases.Length, suite.FuzzCases, target, holdout, malformedPassed, partialPassed,
            concatenatedPassed, resyncPassed, unknown, performancePassed, passed);
        var evidenceHash = Hash(Encoding.UTF8.GetBytes(seed));
        return DomainResult.Success(new DecoderEvaluationEvidence(definition.DefinitionHash, suite.SuiteHash,
            suite.TargetCases.Length, suite.HoldoutCases.Length, suite.FuzzCases, target, holdout,
            malformedPassed, partialPassed, concatenatedPassed, resyncPassed, unknown, performancePassed,
            passed, evidenceHash));
    }

    private bool CasesPass(
        DeclarativeDecoderDefinition definition,
        IEnumerable<DecoderEvaluationCase> cases,
        int maximumOperationsPerByte,
        out bool unknownPreserved)
    {
        unknownPreserved = true;
        foreach (var item in cases)
        {
            var result = decoder.Decode(definition, item.Input.AsMemory());
            if (!result.IsSuccess || result.Value.Frames.Length != item.ExpectedFrameCount ||
                result.Value.OperationCount > Math.Max(1, item.Input.Length) * maximumOperationsPerByte) return false;
            var unknown = result.Value.Frames.SelectMany(frame => frame.UnknownSegments).Sum(segment => segment.Bytes.Length) +
                result.Value.UnframedSegments.Sum(segment => segment.Bytes.Length);
            if (unknown < item.MinimumUnknownBytes) unknownPreserved = false;
        }
        return true;
    }

    private bool FuzzPasses(DeclarativeDecoderDefinition definition, DecoderEvaluationSuite suite)
    {
        ulong state = BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(definition.DefinitionHash)), 0);
        for (var index = 0; index < suite.FuzzCases; index++)
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            var length = (int)(state % 4096);
            var bytes = new byte[length];
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                state ^= state << 13; state ^= state >> 7; state ^= state << 17;
                bytes[byteIndex] = (byte)state;
            }
            var result = decoder.Decode(definition, bytes);
            if (!result.IsSuccess || result.Value.OperationCount > Math.Max(1, length) * suite.MaximumOperationsPerByte)
                return false;
        }
        return true;
    }

    private static bool Valid(DecoderEvaluationSuite suite) => suite is not null &&
        suite.TargetCases.Length is >= 1 and <= 128 && suite.HoldoutCases.Length is >= 1 and <= 128 &&
        suite.FuzzCases is >= 32 and <= 4096 && suite.MaximumOperationsPerByte is >= 2 and <= 128 &&
        SerialDeviceRecordValidator.IsSha256(suite.SuiteHash) &&
        string.Equals(suite.SuiteHash, DecoderEvaluationSuiteHasher.Calculate(suite), StringComparison.Ordinal) &&
        suite.TargetCases.Concat(suite.HoldoutCases).Any(item => item.MinimumUnknownBytes > 0) &&
        suite.TargetCases.Concat(suite.HoldoutCases).All(item =>
            SerialDeviceRecordValidator.Text(item.Name, 128) && item.Input.Length <= 1_048_576 &&
            item.ExpectedFrameCount is >= 0 and <= 4096 && item.MinimumUnknownBytes >= 0 &&
            item.MinimumUnknownBytes <= item.Input.Length);

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
}
