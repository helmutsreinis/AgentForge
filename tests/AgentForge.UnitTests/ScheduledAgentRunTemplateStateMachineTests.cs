using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Scheduling;

namespace AgentForge.UnitTests;

public sealed class ScheduledAgentRunTemplateStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Create_pins_authority_and_produces_a_self_consistent_immutable_hash()
    {
        var definition = Definition();
        var result = ScheduledAgentRunTemplateStateMachine.Create(
            definition,
            new ProviderProfileId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            4,
            "qwen3.8",
            "Daily verification",
            Artifact(HashA, 900),
            Artifact(HashB, 128),
            ["skill:z", "skill:a"],
            HashA,
            8_192,
            120,
            Now,
            new ActorId("operator:local"),
            new CorrelationId("scheduled-template-test"));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(["skill:a", "skill:z"], result.Value.SkillIds);
        Assert.Equal(definition.PolicySnapshotHash, result.Value.PolicySnapshotHash);
        Assert.True(ScheduledAgentRunTemplateStateMachine.IsConsistent(result.Value));
        Assert.False(ScheduledAgentRunTemplateStateMachine.IsConsistent(
            result.Value with { MaximumOutputTokens = 8_193 }));
    }

    [Fact]
    public void Create_rejects_duplicate_skills_and_execution_bounds_above_the_runtime_ceiling()
    {
        var duplicate = ScheduledAgentRunTemplateStateMachine.Create(
            Definition(),
            new ProviderProfileId(Guid.NewGuid()),
            0,
            "qwen3.8",
            "Invalid",
            Artifact(HashA, 100),
            Artifact(HashB, 100),
            ["skill:a", "skill:a"],
            HashA,
            262_145,
            271,
            Now,
            new ActorId("operator:local"),
            new CorrelationId("scheduled-template-invalid"));

        Assert.False(duplicate.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, duplicate.Failure?.Code);
    }

    private static ScheduleDefinition Definition() => new(
        new ScheduleId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new InstallationId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        new AgentIdentityId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
        7,
        new ScheduleTrigger(ScheduleTriggerKind.OneShot, Now.AddHours(1), null, null, null, null),
        "UTC",
        ScheduleMisfirePolicy.Skip,
        ScheduleOverlapPolicy.Skip,
        60,
        1,
        1,
        0,
        2,
        0,
        3,
        null,
        HashA,
        HashB,
        HashA,
        HashB);

    private static ArtifactReference Artifact(string hash, long length) =>
        new(hash, length, "text/plain; charset=utf-8", Now);
}
