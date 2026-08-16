using AgentForge.Abstractions.Models;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;

namespace AgentForge.Abstractions.Runtime;

public sealed record CreateRunConversationRequest(
    RunConversationId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    ProviderProfileId ProviderId,
    long ProviderVersion,
    string ProviderModel,
    string Name,
    string SystemInstruction,
    IReadOnlyList<string> SkillIds,
    string SkillSnapshotHash,
    string PolicySnapshotHash,
    string BudgetSnapshotHash,
    RunConversationTurnId TurnId,
    OrchestrationTaskId TaskId,
    string Prompt,
    string ResponseDepth,
    int MaximumOutputTokens,
    int MaximumWallClockSeconds,
    ActorId ActorId,
    string IdempotencyKey,
    string TurnIdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record AddRunConversationTurnRequest(
    RunConversationId ConversationId,
    long ExpectedVersion,
    RunConversationTurnId TurnId,
    OrchestrationTaskId TaskId,
    string Prompt,
    string ResponseDepth,
    int MaximumOutputTokens,
    int MaximumWallClockSeconds,
    string IdempotencyKey);

public sealed record RunConversationMutationResult(
    RunConversationSnapshot Snapshot,
    RunConversationTurn Turn,
    bool WasReplay = false,
    int PersistenceRedactionCount = 0);

public sealed record RunConversationTurnContent(
    RunConversationTurn Turn,
    string Prompt,
    string? Response);

public sealed record RunConversationDetails(
    RunConversationSnapshot Snapshot,
    string SystemInstruction,
    IReadOnlyList<RunConversationTurnContent> Turns);

public interface IRunConversationRepository
{
    ValueTask AppendAsync(RunConversationSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask<RunConversationSnapshot?> FindLatestAsync(
        RunConversationId conversationId,
        CancellationToken cancellationToken);

    ValueTask<RunConversationSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<RunConversationSnapshot?> FindByTaskIdAsync(
        InstallationId installationId,
        OrchestrationTaskId taskId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RunConversationSnapshot>> ListLatestAsync(
        InstallationId installationId,
        int maximumResults,
        CancellationToken cancellationToken);
}

public interface IRunConversationService
{
    Task<DomainResult<RunConversationMutationResult>> CreateAsync(
        CreateRunConversationRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> AddTurnAsync(
        AddRunConversationTurnRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> StartTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> CompleteTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        LocalModelInteractionResult interaction,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> FailTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        FailureCode failureCode,
        bool retryable,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> AwaitToolApprovalAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        LocalModelToolCall toolCall,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> ResolveToolCallAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        string toolCallId,
        string resultJson,
        bool isError,
        bool denied,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationMutationResult>> CancelTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        CancellationToken cancellationToken);

    Task<DomainResult<RunConversationDetails>> GetDetailsAsync(
        RunConversationId conversationId,
        CancellationToken cancellationToken);
}
