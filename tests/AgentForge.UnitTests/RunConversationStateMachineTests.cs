using System.Globalization;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;

namespace AgentForge.UnitTests;

public sealed class RunConversationStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Conversation_appends_hash_chained_turns_and_returns_ready_after_completion()
    {
        var created = Create();
        Assert.True(created.IsSuccess, created.Failure?.Message);

        var started = RunConversationStateMachine.StartTurn(
            created.Value, created.Value.Turns[0].Id, Now.AddSeconds(1));
        Assert.True(started.IsSuccess, started.Failure?.Message);
        var completed = RunConversationStateMachine.CompleteTurn(
            started.Value,
            started.Value.Turns[0].Id,
            Artifact("b", 24),
            Hash('b'),
            new ModelUsage(12, 7, 0, null, null),
            ModelFinishReason.Stop,
            0,
            8,
            Hash('c'),
            Now.AddSeconds(2));
        Assert.True(completed.IsSuccess, completed.Failure?.Message);
        Assert.Equal(RunConversationState.Ready, completed.Value.State);
        Assert.True(RunConversationStateMachine.IsConsistent(completed.Value));

        var second = Turn(2, Now.AddSeconds(3));
        var added = RunConversationStateMachine.AddTurn(completed.Value, second, Now.AddSeconds(3));
        Assert.True(added.IsSuccess, added.Failure?.Message);
        Assert.Equal(2, added.Value.Turns.Count);
        Assert.Equal(completed.Value.SnapshotHash, added.Value.PreviousSnapshotHash);
    }

    [Fact]
    public void Retryable_failure_requires_resume_while_terminal_failure_blocks_new_turns()
    {
        var started = RunConversationStateMachine.StartTurn(
            Create().Value, TurnId(1), Now.AddSeconds(1)).Value;
        var interrupted = RunConversationStateMachine.FailTurn(
            started, TurnId(1), FailureCode.RecoverableExternalFailure, true, Hash('d'), Now.AddSeconds(2));
        Assert.True(interrupted.IsSuccess);
        Assert.Equal(RunConversationState.NeedsResume, interrupted.Value.State);

        var resumed = RunConversationStateMachine.StartTurn(
            interrupted.Value, TurnId(1), Now.AddSeconds(3));
        Assert.True(resumed.IsSuccess);
        var failed = RunConversationStateMachine.FailTurn(
            resumed.Value, TurnId(1), FailureCode.PolicyDenied, false, Hash('e'), Now.AddSeconds(4));
        Assert.True(failed.IsSuccess);
        Assert.Equal(RunConversationState.Failed, failed.Value.State);
        Assert.False(RunConversationStateMachine.AddTurn(
            failed.Value, Turn(2, Now.AddSeconds(5)), Now.AddSeconds(5)).IsSuccess);
    }

    [Fact]
    public void Conversation_rejects_duplicate_turn_authority_and_detects_snapshot_tampering()
    {
        var created = Create().Value;
        var started = RunConversationStateMachine.StartTurn(created, TurnId(1), Now.AddSeconds(1)).Value;
        var completed = RunConversationStateMachine.CompleteTurn(
            started, TurnId(1), Artifact("f", 10), Hash('f'), null, ModelFinishReason.Stop,
            0, 4, Hash('1'), Now.AddSeconds(2)).Value;
        var duplicate = Turn(2, Now.AddSeconds(3)) with { TaskId = completed.Turns[0].TaskId };
        Assert.False(RunConversationStateMachine.AddTurn(completed, duplicate, Now.AddSeconds(3)).IsSuccess);
        Assert.False(RunConversationStateMachine.IsConsistent(completed with { Name = "tampered" }));
    }

    private static DomainResult<RunConversationSnapshot> Create()
    {
        var first = Turn(1, Now);
        return RunConversationStateMachine.Create(
            new RunConversationId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new InstallationId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            new AgentIdentityId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            3,
            new ProviderProfileId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
            2,
            "qwen3.8",
            "Durable conversation",
            Artifact("a", 128),
            ["skill:csharp.review"],
            Hash('2'),
            Hash('3'),
            Hash('4'),
            first,
            new ActorId("operator"),
            "conversation-key",
            new CorrelationId("conversation-correlation"),
            null,
            Now);
    }

    private static RunConversationTurn Turn(int sequence, DateTimeOffset occurredAt) => new(
        TurnId(sequence),
        sequence,
        new OrchestrationTaskId(Guid.Parse($"{sequence:x8}-1111-2222-3333-444444444444")),
        RunConversationTurnState.Pending,
        Artifact(sequence.ToString(CultureInfo.InvariantCulture), 32),
        null,
        Hash((char)('5' + sequence)),
        null,
        "balanced",
        2_048,
        120,
        null,
        null,
        0,
        0,
        null,
        null,
        false,
        $"turn-key-{sequence}",
        occurredAt,
        occurredAt);

    private static RunConversationTurnId TurnId(int sequence) => new(
        Guid.Parse($"{sequence:x8}-aaaa-bbbb-cccc-dddddddddddd"));

    private static ArtifactReference Artifact(string seed, long length) => new(
        $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed)))}",
        length,
        "text/plain; charset=utf-8",
        Now);

    private static string Hash(char character) => $"sha256:{new string(character, 64)}";
}
