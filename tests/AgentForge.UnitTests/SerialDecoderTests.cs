using System.Collections.Immutable;
using AgentForge.Abstractions.Devices;
using AgentForge.Devices;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class SerialDecoderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Declarative_decoder_parses_fields_and_preserves_unknown_and_noise_bytes()
    {
        using var provider = Services();
        var decoder = provider.GetRequiredService<IDeclarativeDecoder>();
        var input = new byte[] { 0x99, 0xaa, 0x55, 0x34, 0x12, 0x07, 0xde, 0xad, 0xbe };

        var result = decoder.Decode(Definition(), input);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        var frame = Assert.Single(result.Value.Frames);
        Assert.Equal("4660", frame.Fields["temperature"]);
        Assert.Equal("7", frame.Fields["status"]);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe }, Assert.Single(frame.UnknownSegments).Bytes.ToArray());
        Assert.Equal(new byte[] { 0x99 }, Assert.Single(result.Value.UnframedSegments).Bytes.ToArray());
        Assert.Equal(9, result.Value.InputLength);
    }

    [Fact]
    public void Evaluation_covers_target_holdout_partial_concat_resync_fuzz_and_performance()
    {
        using var provider = Services();
        var evaluator = provider.GetRequiredService<IDecoderEvaluator>();

        var evidence = evaluator.Evaluate(Definition(), Suite());

        Assert.True(evidence.IsSuccess, evidence.Failure?.Message);
        Assert.True(evidence.Value.Passed);
        Assert.True(evidence.Value.TargetPassed);
        Assert.True(evidence.Value.HoldoutPassed);
        Assert.True(evidence.Value.PartialPassed);
        Assert.True(evidence.Value.ConcatenatedPassed);
        Assert.True(evidence.Value.ResynchronizationPassed);
        Assert.True(evidence.Value.UnknownFieldsPreserved);
        Assert.True(evidence.Value.PerformancePassed);
        Assert.Equal(256, evidence.Value.FuzzCases);
    }

    [Fact]
    public void Failed_holdout_is_deterministic_promotion_veto()
    {
        using var provider = Services();
        var evaluator = provider.GetRequiredService<IDecoderEvaluator>();
        var suite = Suite();
        var badHoldout = suite with
        {
            HoldoutCases = [suite.HoldoutCases[0] with { ExpectedFrameCount = 99 }],
            SuiteHash = string.Empty,
        };
        badHoldout = badHoldout with { SuiteHash = DecoderEvaluationSuiteHasher.Calculate(badHoldout) };

        var evidence = evaluator.Evaluate(Definition(), badHoldout);
        var proposed = DecoderProposalStateMachine.Propose(
            new DecoderProposalId(Guid.NewGuid()), new InstallationId(Guid.NewGuid()), Definition(), null,
            new ActorId("proposer"), Now);
        var rejected = DecoderProposalStateMachine.Evaluate(proposed, evidence.Value, Now.AddSeconds(1));

        Assert.False(evidence.Value.Passed);
        Assert.False(evidence.Value.HoldoutPassed);
        Assert.Equal(DecoderProposalState.Rejected, rejected.State);
        Assert.Throws<InvalidOperationException>(() => DecoderProposalStateMachine.Approve(
            rejected, new ActorId("governor"), Now.AddSeconds(2)));
    }

    [Fact]
    public void Proposal_enforces_actor_separation_canary_and_hash_chain()
    {
        using var provider = Services();
        var evidence = provider.GetRequiredService<IDecoderEvaluator>().Evaluate(Definition(), Suite()).Value;
        var proposed = DecoderProposalStateMachine.Propose(
            new DecoderProposalId(Guid.NewGuid()), new InstallationId(Guid.NewGuid()), Definition(), null,
            new ActorId("proposer"), Now);
        var evaluated = DecoderProposalStateMachine.Evaluate(proposed, evidence, Now.AddSeconds(1));
        Assert.Throws<InvalidOperationException>(() => DecoderProposalStateMachine.Approve(
            evaluated, new ActorId("proposer"), Now.AddSeconds(2)));
        var approved = DecoderProposalStateMachine.Approve(evaluated, new ActorId("governor"), Now.AddSeconds(2));
        var failingCanary = new DecoderCanaryEvidence("device-group-a", 10, 1, true, Hash('c'));
        Assert.Throws<InvalidOperationException>(() => DecoderProposalStateMachine.Promote(
            approved, failingCanary, Now.AddSeconds(3)));
        var quarantined = DecoderProposalStateMachine.RecordCanary(approved, failingCanary, Now.AddSeconds(3));
        var canary = new DecoderCanaryEvidence("device-group-a", 100, 0, false, Hash('d'));
        var active = DecoderProposalStateMachine.Promote(approved, canary, Now.AddSeconds(3));
        var rollback = DecoderProposalStateMachine.Rollback(active, Now.AddSeconds(4));

        Assert.Equal(DecoderProposalState.Active, active.State);
        Assert.Equal(DecoderProposalState.Quarantined, quarantined.State);
        Assert.Equal(DecoderProposalState.RolledBack, rollback.State);
        Assert.Equal(Enumerable.Range(0, 6).Take(5).Select(value => (long)value),
            new[] { proposed, evaluated, approved, active, rollback }.Select(item => item.Version));
        Assert.Equal(new[] { proposed, evaluated, approved, active }.Select(item => item.SnapshotHash),
            new[] { evaluated, approved, active, rollback }.Select(item => item.PreviousSnapshotHash));
        Assert.All(new[] { proposed, evaluated, approved, active, rollback }, item => Assert.True(item.IsConsistent()));
    }

    [Fact]
    public void Decoder_candidate_cannot_acquire_device_write_or_other_authority()
    {
        var candidate = Definition() with
        {
            Permissions = new[] { DecoderAuthority.ProtocolDecode, DecoderAuthority.DeviceWrite }.ToImmutableSortedSet(),
            DefinitionHash = string.Empty,
        };
        candidate = candidate with { DefinitionHash = DeclarativeDecoderDefinition.CalculateHash(candidate) };

        Assert.False(candidate.IsValid());
        Assert.Throws<InvalidOperationException>(() => DecoderProposalStateMachine.Propose(
            new DecoderProposalId(Guid.NewGuid()), new InstallationId(Guid.NewGuid()), candidate, null,
            new ActorId("proposer"), Now));
    }

    internal static DeclarativeDecoderDefinition Definition(string version = "1.0.0")
    {
        var definition = new DeclarativeDecoderDefinition("fixture.decoder", version, 8, [0xaa, 0x55],
            [new("temperature", 2, 2, DecoderFieldEncoding.UInt16LittleEndian),
             new("status", 4, 1, DecoderFieldEncoding.ByteUnsigned)],
            new[] { DecoderAuthority.ProtocolDecode }.ToImmutableSortedSet(), string.Empty);
        return definition with { DefinitionHash = DeclarativeDecoderDefinition.CalculateHash(definition) };
    }

    internal static DecoderEvaluationSuite Suite()
    {
        var frame = new byte[] { 0xaa, 0x55, 0x34, 0x12, 0x07, 0xde, 0xad, 0xbe }.ToImmutableArray();
        var suite = new DecoderEvaluationSuite(
            [new("target-basic", frame, 1, 3)],
            [new("holdout-noise-concat", new byte[] { 1, 2, 3 }.Concat(frame).Concat(frame).ToImmutableArray(), 2, 9)],
            256, 16, string.Empty);
        return suite with { SuiteHash = DecoderEvaluationSuiteHasher.Calculate(suite) };
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AgentForge.Abstractions.Time.IClock>(new FixedClock());
        services.AddAgentForgeDevices();
        return services.BuildServiceProvider();
    }

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";
    private sealed class FixedClock : AgentForge.Abstractions.Time.IClock { public DateTimeOffset UtcNow => Now; }
}
