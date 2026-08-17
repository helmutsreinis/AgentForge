using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;

namespace AgentForge.Domain.Scheduling;

public sealed record ScheduledAgentRunTemplate(
    ScheduleId ScheduleId,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    ProviderProfileId ProviderId,
    long ProviderVersion,
    string ProviderModel,
    string Name,
    ArtifactReference SystemInstructionArtifact,
    ArtifactReference PromptArtifact,
    IReadOnlyList<string> SkillIds,
    string SkillSnapshotHash,
    string PolicySnapshotHash,
    string CapabilitySnapshotHash,
    string BudgetSnapshotHash,
    int MaximumOutputTokens,
    int MaximumWallClockSeconds,
    DateTimeOffset CreatedAt,
    ActorId ActorId,
    CorrelationId CorrelationId,
    string TemplateHash);

public sealed record CreateScheduledAgentRunRequest(
    ScheduleDefinition Definition,
    ProviderProfileId ProviderId,
    long ProviderVersion,
    string ProviderModel,
    string Name,
    string SystemInstruction,
    string Prompt,
    IReadOnlyList<string> SkillIds,
    string SkillSnapshotHash,
    int MaximumOutputTokens,
    int MaximumWallClockSeconds,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public sealed record ScheduledAgentRunCreationResult(
    ScheduledAgentRunTemplate Template,
    ScheduleSnapshot Schedule,
    IReadOnlyList<DateTimeOffset> Preview,
    bool WasReplay);

public sealed record ScheduledAgentRunExecutionResult(
    ScheduleId ScheduleId,
    string OccurrenceIdHash,
    RunConversationId ConversationId,
    string EvidenceHash,
    bool WasReplay);

public static class ScheduledAgentRunTemplateStateMachine
{
    public static DomainResult<ScheduledAgentRunTemplate> Create(
        ScheduleDefinition definition,
        ProviderProfileId providerId,
        long providerVersion,
        string providerModel,
        string name,
        ArtifactReference systemInstructionArtifact,
        ArtifactReference promptArtifact,
        IReadOnlyList<string> skillIds,
        string skillSnapshotHash,
        int maximumOutputTokens,
        int maximumWallClockSeconds,
        DateTimeOffset createdAt,
        ActorId actorId,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Id.Value == Guid.Empty || definition.InstallationId.Value == Guid.Empty ||
            definition.AgentId.Value == Guid.Empty || definition.AgentVersion < 0 ||
            providerId.Value == Guid.Empty || providerVersion < 0 || !Text(providerModel, 256) ||
            !Text(name, 120) || !Artifact(systemInstructionArtifact, 131_072) ||
            !Artifact(promptArtifact, 65_536) || skillIds is null || skillIds.Count > 16 ||
            skillIds.Any(item => !Text(item, 128)) ||
            skillIds.Distinct(StringComparer.Ordinal).Count() != skillIds.Count ||
            !Hash(skillSnapshotHash) || !Hash(definition.PolicySnapshotHash) ||
            !Hash(definition.CapabilitySnapshotHash) || !Hash(definition.BudgetSnapshotHash) ||
            maximumOutputTokens is < 1 or > 262_144 || maximumWallClockSeconds is < 1 or > 270 ||
            createdAt == default || !Text(actorId.Value, 256) || !Text(correlationId.Value, 128))
        {
            return DomainResult.Fail<ScheduledAgentRunTemplate>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Scheduled run identity, authority, content, or execution bounds are invalid."));
        }

        var template = new ScheduledAgentRunTemplate(
            definition.Id,
            definition.InstallationId,
            definition.AgentId,
            definition.AgentVersion,
            providerId,
            providerVersion,
            providerModel,
            name,
            systemInstructionArtifact with { },
            promptArtifact with { },
            skillIds.Order(StringComparer.Ordinal).ToArray(),
            skillSnapshotHash,
            definition.PolicySnapshotHash,
            definition.CapabilitySnapshotHash,
            definition.BudgetSnapshotHash,
            maximumOutputTokens,
            maximumWallClockSeconds,
            createdAt,
            actorId,
            correlationId,
            ScheduleStateMachine.EmptyHash);
        return DomainResult.Success(template with { TemplateHash = ComputeHash(template) });
    }

    public static bool IsConsistent(ScheduledAgentRunTemplate? template) => template is not null &&
        template.ScheduleId.Value != Guid.Empty && template.InstallationId.Value != Guid.Empty &&
        template.AgentId.Value != Guid.Empty && template.AgentVersion >= 0 &&
        template.ProviderId.Value != Guid.Empty && template.ProviderVersion >= 0 &&
        Text(template.ProviderModel, 256) && Text(template.Name, 120) &&
        Artifact(template.SystemInstructionArtifact, 131_072) && Artifact(template.PromptArtifact, 65_536) &&
        template.SkillIds is { Count: <= 16 } && template.SkillIds.All(item => Text(item, 128)) &&
        template.SkillIds.SequenceEqual(template.SkillIds.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        template.SkillIds.Distinct(StringComparer.Ordinal).Count() == template.SkillIds.Count &&
        Hash(template.SkillSnapshotHash) && Hash(template.PolicySnapshotHash) &&
        Hash(template.CapabilitySnapshotHash) && Hash(template.BudgetSnapshotHash) &&
        template.MaximumOutputTokens is >= 1 and <= 262_144 &&
        template.MaximumWallClockSeconds is >= 1 and <= 270 && template.CreatedAt != default &&
        Text(template.ActorId.Value, 256) && Text(template.CorrelationId.Value, 128) &&
        Hash(template.TemplateHash) && string.Equals(template.TemplateHash, ComputeHash(template), StringComparison.Ordinal);

    private static string ComputeHash(ScheduledAgentRunTemplate template)
    {
        var builder = new StringBuilder(2048);
        Append(builder, template.ScheduleId);
        Append(builder, template.InstallationId);
        Append(builder, template.AgentId);
        Append(builder, template.AgentVersion);
        Append(builder, template.ProviderId);
        Append(builder, template.ProviderVersion);
        Append(builder, template.ProviderModel);
        Append(builder, template.Name);
        AppendArtifact(builder, template.SystemInstructionArtifact);
        AppendArtifact(builder, template.PromptArtifact);
        foreach (var skillId in template.SkillIds) Append(builder, skillId);
        Append(builder, template.SkillSnapshotHash);
        Append(builder, template.PolicySnapshotHash);
        Append(builder, template.CapabilitySnapshotHash);
        Append(builder, template.BudgetSnapshotHash);
        Append(builder, template.MaximumOutputTokens);
        Append(builder, template.MaximumWallClockSeconds);
        Append(builder, template.CreatedAt.UtcTicks);
        Append(builder, template.ActorId);
        Append(builder, template.CorrelationId);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void AppendArtifact(StringBuilder builder, ArtifactReference artifact)
    {
        Append(builder, artifact.ContentHash);
        Append(builder, artifact.Length);
        Append(builder, artifact.MediaType);
        Append(builder, artifact.CreatedAt.UtcTicks);
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }

    private static bool Artifact(ArtifactReference? artifact, long maximumLength) => artifact is not null &&
        Hash(artifact.ContentHash) && artifact.Length is >= 1 && artifact.Length <= maximumLength &&
        string.Equals(artifact.MediaType, "text/plain; charset=utf-8", StringComparison.Ordinal) &&
        artifact.CreatedAt != default;

    private static bool Hash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(character => char.IsControl(character));
}
