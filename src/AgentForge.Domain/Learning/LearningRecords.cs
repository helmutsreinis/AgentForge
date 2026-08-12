using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Domain.Learning;

public readonly record struct LearningSignalId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum LearningSignalKind
{
    Correction,
    SuccessfulProcedure,
    Recovery,
    MissingCapability,
    RepeatedSkillChain,
}

public enum LearningAction
{
    NoDurableAction,
    Memory,
    NewSkill,
    SkillRevision,
    Bundle,
}

public enum LearningRole
{
    Worker,
    Proposer,
    Verifier,
    Critic,
    Governor,
}

public sealed record LearningRoleAssignments(
    ActorId Worker,
    ActorId Proposer,
    ActorId Verifier,
    ActorId Critic,
    ActorId Governor)
{
    public bool IsSeparated() =>
        new[] { Worker, Proposer, Verifier, Critic, Governor }.Distinct().Count() == 5 &&
        new[] { Worker, Proposer, Verifier, Critic, Governor }
            .All(actor => LearningValidation.IsBounded(actor.Value, 256));

    public ActorId ActorFor(LearningRole role) => role switch
    {
        LearningRole.Worker => Worker,
        LearningRole.Proposer => Proposer,
        LearningRole.Verifier => Verifier,
        LearningRole.Critic => Critic,
        LearningRole.Governor => Governor,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

public sealed record SkillUsageReceipt(
    string RunId,
    SkillId SkillId,
    SkillVersion Version,
    string PackageHash,
    bool Succeeded,
    DateTimeOffset UsedAt,
    string ReceiptHash);

public sealed record SkillChainStep(
    int Position,
    SkillId SkillId,
    SkillVersion Version,
    string PackageHash,
    string InputContractHash,
    string OutputContractHash);

public sealed record LearningSignal(
    LearningSignalId Id,
    InstallationId InstallationId,
    LearningSignalKind Kind,
    string RedactedSummary,
    string SourceEvidenceHash,
    IReadOnlyList<SkillUsageReceipt> UsageReceipts,
    IReadOnlyList<SkillChainStep> SuccessfulChain,
    int OccurrenceCount,
    ActorId CapturedBy,
    DateTimeOffset CapturedAt,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string SignalHash);

public sealed record LearningClassification(
    LearningSignalId SignalId,
    LearningAction Action,
    string ReasonCode,
    string SignalHash,
    string ClassificationHash);

public static class LearningSignalClassifier
{
    public static DomainResult<LearningSignal> Create(
        LearningSignalId id,
        InstallationId installationId,
        LearningSignalKind kind,
        string redactedSummary,
        string sourceEvidenceHash,
        IReadOnlyList<SkillUsageReceipt> usageReceipts,
        IReadOnlyList<SkillChainStep> successfulChain,
        int occurrenceCount,
        ActorId capturedBy,
        DateTimeOffset capturedAt,
        CorrelationId correlationId,
        CorrelationId? causationId)
    {
        usageReceipts ??= [];
        successfulChain ??= [];
        var signal = new LearningSignal(
            id,
            installationId,
            kind,
            redactedSummary,
            sourceEvidenceHash,
            usageReceipts.OrderBy(receipt => receipt.ReceiptHash, StringComparer.Ordinal).ToArray(),
            successfulChain.OrderBy(step => step.Position).ToArray(),
            occurrenceCount,
            capturedBy,
            capturedAt,
            correlationId,
            causationId,
            LearningValidation.EmptyHash);
        if (!IsStructurallyValid(signal, checkHash: false))
        {
            return Failure<LearningSignal>("Learning evidence must be redacted, bounded, hashed, and structurally valid.");
        }

        return DomainResult.Success(signal with { SignalHash = ComputeSignalHash(signal) });
    }

    public static DomainResult<LearningClassification> Classify(LearningSignal signal)
    {
        if (!IsConsistent(signal))
        {
            return Failure<LearningClassification>("Only consistent immutable evidence can be classified.");
        }

        var (action, reason) = signal.Kind switch
        {
            LearningSignalKind.RepeatedSkillChain when signal.OccurrenceCount >= 3 && signal.SuccessfulChain.Count >= 2 =>
                (LearningAction.Bundle, "repeated-successful-chain"),
            LearningSignalKind.Correction when signal.UsageReceipts.Any(receipt => receipt.Succeeded) =>
                (LearningAction.SkillRevision, "correction-with-usage-receipt"),
            LearningSignalKind.Correction => (LearningAction.Memory, "correction-without-revision-authority"),
            LearningSignalKind.MissingCapability => (LearningAction.NewSkill, "missing-capability"),
            LearningSignalKind.SuccessfulProcedure when signal.OccurrenceCount >= 3 =>
                (LearningAction.NewSkill, "repeated-successful-procedure"),
            LearningSignalKind.Recovery when signal.OccurrenceCount >= 3 =>
                (LearningAction.NewSkill, "repeated-recovery"),
            LearningSignalKind.SuccessfulProcedure or LearningSignalKind.Recovery or LearningSignalKind.RepeatedSkillChain =>
                (LearningAction.Memory, "insufficient-repetition"),
            _ => (LearningAction.NoDurableAction, "no-durable-signal"),
        };
        var classification = new LearningClassification(
            signal.Id,
            action,
            reason,
            signal.SignalHash,
            LearningValidation.EmptyHash);
        return DomainResult.Success(classification with
        {
            ClassificationHash = LearningValidation.Hash(
                $"{signal.Id}|{action}|{reason}|{signal.SignalHash}"),
        });
    }

    public static bool IsConsistent(LearningSignal? signal) => signal is not null &&
        IsStructurallyValid(signal, checkHash: true) &&
        string.Equals(signal.SignalHash, ComputeSignalHash(signal), StringComparison.Ordinal);

    private static bool IsStructurallyValid(LearningSignal signal, bool checkHash) =>
        signal.Id.Value != Guid.Empty && signal.InstallationId.Value != Guid.Empty && Enum.IsDefined(signal.Kind) &&
        LearningValidation.IsRedactedSummary(signal.RedactedSummary) &&
        LearningValidation.IsHash(signal.SourceEvidenceHash) &&
        signal.UsageReceipts.Count <= 128 && signal.UsageReceipts.All(LearningValidation.IsValid) &&
        signal.UsageReceipts.Select(receipt => receipt.ReceiptHash).Distinct(StringComparer.Ordinal).Count() ==
            signal.UsageReceipts.Count &&
        signal.UsageReceipts.SequenceEqual(
            signal.UsageReceipts.OrderBy(receipt => receipt.ReceiptHash, StringComparer.Ordinal)) &&
        signal.SuccessfulChain.Count <= 128 && signal.SuccessfulChain.All(LearningValidation.IsValid) &&
        signal.SuccessfulChain.Select(step => step.Position).SequenceEqual(Enumerable.Range(0, signal.SuccessfulChain.Count)) &&
        signal.OccurrenceCount is >= 1 and <= 1_000_000 &&
        LearningValidation.IsBounded(signal.CapturedBy.Value, 256) &&
        LearningValidation.IsBounded(signal.CorrelationId.Value, 128) &&
        (signal.CausationId is null || LearningValidation.IsBounded(signal.CausationId.Value.Value, 128)) &&
        (!checkHash || LearningValidation.IsHash(signal.SignalHash));

    private static string ComputeSignalHash(LearningSignal signal)
    {
        var builder = new StringBuilder(4096);
        LearningValidation.Append(builder, signal.Id);
        LearningValidation.Append(builder, signal.InstallationId);
        LearningValidation.Append(builder, signal.Kind);
        LearningValidation.Append(builder, signal.RedactedSummary);
        LearningValidation.Append(builder, signal.SourceEvidenceHash);
        foreach (var receipt in signal.UsageReceipts)
        {
            LearningValidation.Append(builder, receipt.RunId);
            LearningValidation.Append(builder, receipt.SkillId);
            LearningValidation.Append(builder, receipt.Version);
            LearningValidation.Append(builder, receipt.PackageHash);
            LearningValidation.Append(builder, receipt.Succeeded);
            LearningValidation.Append(builder, receipt.UsedAt.UtcTicks);
            LearningValidation.Append(builder, receipt.ReceiptHash);
        }

        foreach (var step in signal.SuccessfulChain)
        {
            LearningValidation.Append(builder, step.Position);
            LearningValidation.Append(builder, step.SkillId);
            LearningValidation.Append(builder, step.Version);
            LearningValidation.Append(builder, step.PackageHash);
            LearningValidation.Append(builder, step.InputContractHash);
            LearningValidation.Append(builder, step.OutputContractHash);
        }

        LearningValidation.Append(builder, signal.OccurrenceCount);
        LearningValidation.Append(builder, signal.CapturedBy);
        LearningValidation.Append(builder, signal.CapturedAt.UtcTicks);
        LearningValidation.Append(builder, signal.CorrelationId);
        LearningValidation.Append(builder, signal.CausationId?.Value ?? string.Empty);
        return LearningValidation.Hash(builder.ToString());
    }

    private static DomainResult<T> Failure<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ValidationFailure, message));
}

internal static class LearningValidation
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    internal const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    internal static bool IsValid(SkillUsageReceipt receipt) =>
        IsBounded(receipt.RunId, 256) && IsSkillId(receipt.SkillId) &&
        SkillVersion.TryParse(receipt.Version.Value, out _) && IsHash(receipt.PackageHash) &&
        IsHash(receipt.ReceiptHash);

    internal static bool IsValid(SkillChainStep step) => step.Position is >= 0 and < 128 &&
        IsSkillId(step.SkillId) && SkillVersion.TryParse(step.Version.Value, out _) &&
        IsHash(step.PackageHash) && IsHash(step.InputContractHash) && IsHash(step.OutputContractHash);

    internal static bool IsSkillId(SkillId id) => IsBounded(id.Value, 256) &&
        id.Value.StartsWith("skill:", StringComparison.Ordinal);

    internal static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    internal static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    internal static bool IsRedactedSummary(string? value) => IsBounded(value, 4096) &&
        !value!.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("authorization:", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("api_key", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    internal static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    internal static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }
}
