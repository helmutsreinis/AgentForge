using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;
using AgentForge.Domain.Scheduling;

namespace AgentForge.Runtime;

internal sealed class ScheduledAgentRunService(
    IScheduledAgentRunStore templates,
    IScheduleService schedules,
    IArtifactStore artifacts,
    ISensitiveDataRedactor redactor,
    IAgentIdentityRepository agents,
    IProviderProfileRepository providers,
    ITaskOrchestrator orchestrator,
    ITaskSnapshotStore taskSnapshots,
    IRunConversationRepository conversationSnapshots,
    IRunConversationService conversations,
    ILocalModelInteractionService interactions,
    IClock clock) : IScheduledAgentRunService
{
    private const string MediaType = "text/plain; charset=utf-8";
    private const string Owner = "scheduled-agent-run-worker";

    public async Task<DomainResult<ScheduledAgentRunCreationResult>> CreateAsync(
        CreateScheduledAgentRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Content(request.SystemInstruction, 24_576) || !Content(request.Prompt, 16_384) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256)
        {
            return Invalid<ScheduledAgentRunCreationResult>(
                "Scheduled run content or idempotency bounds are invalid.");
        }

        var preview = schedules.Preview(request.Definition, clock.UtcNow, 5);
        if (!preview.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunCreationResult>(preview.Failure!);
        }

        var system = Redact(request.SystemInstruction);
        var prompt = Redact(request.Prompt);
        if (!system.IsSuccess || !prompt.IsSuccess)
        {
            return Invalid<ScheduledAgentRunCreationResult>(
                "Scheduled run content is empty or invalid after redaction.");
        }

        var systemArtifact = await PutTextAsync(system.Value, cancellationToken);
        var promptArtifact = await PutTextAsync(prompt.Value, cancellationToken);
        var createdTemplate = ScheduledAgentRunTemplateStateMachine.Create(
            request.Definition,
            request.ProviderId,
            request.ProviderVersion,
            request.ProviderModel,
            request.Name,
            systemArtifact,
            promptArtifact,
            request.SkillIds,
            request.SkillSnapshotHash,
            request.MaximumOutputTokens,
            request.MaximumWallClockSeconds,
            clock.UtcNow,
            request.ActorId,
            request.CorrelationId);
        if (!createdTemplate.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunCreationResult>(createdTemplate.Failure!);
        }

        var existing = await templates.FindAsync(request.Definition.Id, cancellationToken);
        if (existing is not null && !string.Equals(
                existing.TemplateHash, createdTemplate.Value.TemplateHash, StringComparison.Ordinal))
        {
            return Conflict<ScheduledAgentRunCreationResult>(
                "The schedule identity is already bound to another immutable run template.");
        }
        if (existing is null)
        {
            await templates.AddAsync(createdTemplate.Value, cancellationToken);
        }

        var schedule = await schedules.CreateAsync(
            request.Definition,
            request.ActorId,
            request.IdempotencyKey,
            request.CorrelationId,
            request.CausationId,
            cancellationToken);
        if (!schedule.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunCreationResult>(schedule.Failure!);
        }

        return DomainResult.Success(new ScheduledAgentRunCreationResult(
            existing ?? createdTemplate.Value,
            schedule.Value.Snapshot,
            preview.Value,
            schedule.Value.WasReplay));
    }

    public async Task<DomainResult<ScheduledAgentRunExecutionResult>> ExecuteAsync(
        ScheduleSnapshot schedule,
        string occurrenceIdHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!ScheduleStateMachine.IsConsistent(schedule) || !Hash(occurrenceIdHash) ||
            schedule.Occurrences.SingleOrDefault(item =>
                string.Equals(item.IdempotencyKeyHash, occurrenceIdHash, StringComparison.Ordinal)) is not
                { State: ScheduleOccurrenceState.Running })
        {
            return Invalid<ScheduledAgentRunExecutionResult>(
                "Scheduled execution requires the exact claimed occurrence snapshot.");
        }

        var template = await templates.FindAsync(schedule.Definition.Id, cancellationToken);
        if (template is null || template.InstallationId != schedule.Definition.InstallationId ||
            template.AgentId != schedule.Definition.AgentId ||
            template.AgentVersion != schedule.Definition.AgentVersion ||
            !string.Equals(template.PolicySnapshotHash, schedule.Definition.PolicySnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(template.CapabilitySnapshotHash, schedule.Definition.CapabilitySnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(template.BudgetSnapshotHash, schedule.Definition.BudgetSnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(template.SkillSnapshotHash, schedule.Definition.SkillSnapshotHash, StringComparison.Ordinal))
        {
            return Denied<ScheduledAgentRunExecutionResult>(
                "The scheduled run template no longer matches the exact schedule authority.");
        }

        var agent = await agents.FindByIdAsync(template.AgentId, cancellationToken);
        if (agent is null || agent.InstallationId != template.InstallationId || agent.Version != template.AgentVersion)
        {
            return Conflict<ScheduledAgentRunExecutionResult>(
                "The pinned agent policy version changed; create a replacement schedule.");
        }
        var provider = await providers.FindByIdAsync(template.ProviderId, cancellationToken);
        if (provider is null || provider.InstallationId != template.InstallationId ||
            provider.Version != template.ProviderVersion ||
            !string.Equals(provider.Model, template.ProviderModel, StringComparison.Ordinal))
        {
            return Conflict<ScheduledAgentRunExecutionResult>(
                "The pinned provider version changed; create a replacement schedule.");
        }

        var systemInstruction = await OpenTextAsync(template.SystemInstructionArtifact, 24_576, cancellationToken);
        if (!systemInstruction.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(systemInstruction.Failure!);
        }
        var prompt = await OpenTextAsync(template.PromptArtifact, 16_384, cancellationToken);
        if (!prompt.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(prompt.Failure!);
        }

        var identity = Convert.FromHexString(occurrenceIdHash[7..]);
        var taskId = new OrchestrationTaskId(new Guid(identity.AsSpan(0, 16)));
        var turnId = new RunConversationTurnId(new Guid(identity.AsSpan(16, 16)));
        var conversationId = new RunConversationId(taskId.Value);
        var correlation = new CorrelationId($"scheduled-run:{Convert.ToHexStringLower(identity)}");
        var definition = new OrchestrationTaskDefinition(
            taskId,
            template.InstallationId,
            template.AgentId,
            template.AgentVersion,
            OrchestrationPattern.Sequential,
            [new TaskNodeDefinition(
                new TaskNodeId("local-model"),
                template.Name,
                [],
                [],
                [],
                new TaskExecutionBudget(
                    0,
                    agent.Budget.MaxInputTokens,
                    template.MaximumOutputTokens,
                    template.MaximumWallClockSeconds),
                new TaskRetryPolicy(2, 0))],
            1,
            0,
            0,
            template.PolicySnapshotHash,
            template.BudgetSnapshotHash,
            template.SkillSnapshotHash);
        var task = await orchestrator.CreateAsync(
            definition,
            template.ActorId,
            $"scheduled-task:{occurrenceIdHash}",
            correlation,
            schedule.CorrelationId,
            cancellationToken);
        if (!task.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(task.Failure!);
        }

        var existingConversation = await conversationSnapshots.FindLatestAsync(conversationId, cancellationToken);
        if (existingConversation is { State: RunConversationState.Ready })
        {
            return DomainResult.Success(new ScheduledAgentRunExecutionResult(
                template.ScheduleId,
                occurrenceIdHash,
                conversationId,
                existingConversation.Turns[^1].EvidenceHash!,
                true));
        }
        if (existingConversation is { State: RunConversationState.Failed or RunConversationState.Canceled })
        {
            return Denied<ScheduledAgentRunExecutionResult>(
                "The durable scheduled conversation is terminal and cannot be repeated.");
        }

        var conversation = existingConversation is null
            ? await conversations.CreateAsync(new CreateRunConversationRequest(
                conversationId,
                template.InstallationId,
                template.AgentId,
                template.AgentVersion,
                template.ProviderId,
                template.ProviderVersion,
                template.ProviderModel,
                template.Name,
                systemInstruction.Value,
                template.SkillIds,
                template.SkillSnapshotHash,
                template.PolicySnapshotHash,
                template.BudgetSnapshotHash,
                turnId,
                taskId,
                prompt.Value,
                "balanced",
                template.MaximumOutputTokens,
                template.MaximumWallClockSeconds,
                template.ActorId,
                $"scheduled-conversation:{occurrenceIdHash}",
                $"scheduled-turn:{occurrenceIdHash}",
                correlation,
                schedule.CorrelationId), cancellationToken)
            : DomainResult.Success(new RunConversationMutationResult(
                existingConversation,
                existingConversation.Turns[^1],
                true));
        if (!conversation.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(conversation.Failure!);
        }

        var currentTask = await taskSnapshots.FindLatestAsync(taskId, cancellationToken)
            ?? task.Value.Snapshot;
        var node = currentTask.Nodes.Single(item => item.Definition.Id == new TaskNodeId("local-model"));
        if (node is { State: TaskNodeState.Leased, Lease: { } lease })
        {
            if (lease.ExpiresAt > clock.UtcNow)
            {
                return Conflict<ScheduledAgentRunExecutionResult>(
                    "The prior scheduled model lease remains active.");
            }
            var recovered = await orchestrator.RecoverExpiredAsync(taskId, currentTask.Version, cancellationToken);
            if (!recovered.IsSuccess)
            {
                return DomainResult.Fail<ScheduledAgentRunExecutionResult>(recovered.Failure!);
            }
            currentTask = recovered.Value.Snapshot;
            node = currentTask.Nodes.Single(item => item.Definition.Id == new TaskNodeId("local-model"));
        }
        if (node.State is not TaskNodeState.Ready)
        {
            return Conflict<ScheduledAgentRunExecutionResult>(
                "The durable scheduled model task is not claimable.");
        }

        var claim = await orchestrator.ClaimAsync(
            taskId,
            currentTask.Version,
            node.Definition.Id,
            Owner,
            TimeSpan.FromSeconds(Math.Min(template.MaximumWallClockSeconds + 30, 300)),
            cancellationToken);
        if (!claim.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(claim.Failure!);
        }
        var started = await conversations.StartTurnAsync(
            conversationId,
            conversation.Value.Snapshot.Version,
            conversation.Value.Turn.Id,
            cancellationToken);
        if (!started.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(started.Failure!);
        }

        var interaction = await interactions.InvokeAsync(new LocalModelInteractionRequest(
            new ModelRequestId(taskId.Value),
            provider,
            systemInstruction.Value,
            prompt.Value,
            new ModelInvocationLimits(
                template.MaximumOutputTokens,
                0,
                Math.Max(4_096, Math.Min(262_656, template.MaximumOutputTokens + 512)),
                template.MaximumWallClockSeconds),
            correlation), cancellationToken);
        if (!interaction.IsSuccess)
        {
            var evidence = Evidence(new
            {
                TaskId = taskId.Value,
                interaction.Failure!.Code,
                interaction.Failure.IsRetryable,
            });
            var failedTask = await orchestrator.FailAsync(
                taskId,
                claim.Value.Snapshot.Version,
                node.Definition.Id,
                Owner,
                claim.Value.LeaseToken,
                evidence,
                interaction.Failure.Code,
                interaction.Failure.IsRetryable,
                CancellationToken.None);
            await conversations.FailTurnAsync(
                conversationId,
                started.Value.Snapshot.Version,
                started.Value.Turn.Id,
                interaction.Failure.Code,
                interaction.Failure.IsRetryable && failedTask.IsSuccess &&
                    !OrchestrationTaskStateMachine.IsTerminal(failedTask.Value.Snapshot.State),
                evidence,
                CancellationToken.None);
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(interaction.Failure);
        }

        var completedTask = await orchestrator.CompleteAsync(
            taskId,
            claim.Value.Snapshot.Version,
            node.Definition.Id,
            Owner,
            claim.Value.LeaseToken,
            interaction.Value.EvidenceHash,
            CancellationToken.None);
        if (!completedTask.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(completedTask.Failure!);
        }
        var completedConversation = await conversations.CompleteTurnAsync(
            conversationId,
            started.Value.Snapshot.Version,
            started.Value.Turn.Id,
            interaction.Value,
            CancellationToken.None);
        if (!completedConversation.IsSuccess)
        {
            return DomainResult.Fail<ScheduledAgentRunExecutionResult>(completedConversation.Failure!);
        }

        return DomainResult.Success(new ScheduledAgentRunExecutionResult(
            template.ScheduleId,
            occurrenceIdHash,
            conversationId,
            interaction.Value.EvidenceHash,
            false));
    }

    private DomainResult<string> Redact(string value)
    {
        var result = redactor.Redact(new { text = value });
        using var document = JsonDocument.Parse(result.Data.Json);
        var text = document.RootElement.GetProperty("text").GetString();
        return string.IsNullOrWhiteSpace(text)
            ? Invalid<string>("Scheduled content is empty after redaction.")
            : DomainResult.Success(text);
    }

    private async Task<ArtifactReference> PutTextAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await artifacts.PutAsync(stream, MediaType, cancellationToken);
    }

    private async Task<DomainResult<string>> OpenTextAsync(
        ArtifactReference artifact,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (artifact.Length is < 1 or > 131_072 || !string.Equals(artifact.MediaType, MediaType, StringComparison.Ordinal))
        {
            return Invalid<string>("Scheduled text artifact metadata is invalid.");
        }
        await using var stream = await artifacts.OpenReadAsync(artifact, cancellationToken);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        if (bytes.LongLength != artifact.Length ||
            !string.Equals($"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}",
                artifact.ContentHash, StringComparison.Ordinal))
        {
            return Denied<string>("Scheduled text artifact integrity validation failed.");
        }
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return Content(text, maximumCharacters)
                ? DomainResult.Success(text)
                : Invalid<string>("Scheduled text artifact content is invalid.");
        }
        catch (DecoderFallbackException)
        {
            return Invalid<string>("Scheduled text artifact is not strict UTF-8.");
        }
    }

    private static string Evidence<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)))}";

    private static bool Hash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static bool Content(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(character =>
            char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Denied<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<T> Conflict<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ConcurrencyConflict, message, IsRetryable: true));
}
