using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Domain.Runtime;

public readonly record struct RunConversationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct RunConversationTurnId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum RunConversationState
{
    Running,
    Ready,
    NeedsResume,
    Failed,
    Canceled,
}

public enum RunConversationTurnState
{
    Pending,
    Running,
    Completed,
    NeedsResume,
    Failed,
    Canceled,
}

public enum RunConversationToolCallState
{
    AwaitingApproval,
    Executed,
    Denied,
}

public sealed record RunConversationToolCall(
    RunConversationTurnId TurnId,
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    string RequestHash,
    RunConversationToolCallState State,
    string? ResultJson,
    string? ResultHash,
    bool IsError,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt);

public sealed record RunConversationTurn(
    RunConversationTurnId Id,
    int Sequence,
    OrchestrationTaskId TaskId,
    RunConversationTurnState State,
    ArtifactReference PromptArtifact,
    ArtifactReference? ResponseArtifact,
    string RequestHash,
    string? ResponseHash,
    string ResponseDepth,
    int MaximumOutputTokens,
    int MaximumWallClockSeconds,
    ModelUsage? Usage,
    ModelFinishReason? FinishReason,
    int ContextRedactionCount,
    int EventCount,
    string? EvidenceHash,
    FailureCode? FailureCode,
    bool Retryable,
    string IdempotencyKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RunConversationSnapshot(
    RunConversationId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    ProviderProfileId ProviderId,
    long ProviderVersion,
    string ProviderModel,
    string Name,
    ArtifactReference SystemInstructionArtifact,
    IReadOnlyList<string> SkillIds,
    string SkillSnapshotHash,
    string PolicySnapshotHash,
    string BudgetSnapshotHash,
    long Version,
    RunConversationState State,
    IReadOnlyList<RunConversationTurn> Turns,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId)
{
    public IReadOnlyList<RunConversationToolCall> ToolCalls { get; init; } = [];
}

public static class RunConversationStateMachine
{
    public const int MaximumTurns = 64;
    public const string EmptyHash = OrchestrationTaskStateMachine.EmptyHash;

    public static DomainResult<RunConversationSnapshot> Create(
        RunConversationId id,
        InstallationId installationId,
        AgentIdentityId agentId,
        long agentVersion,
        ProviderProfileId providerId,
        long providerVersion,
        string providerModel,
        string name,
        ArtifactReference systemInstructionArtifact,
        IReadOnlyList<string> skillIds,
        string skillSnapshotHash,
        string policySnapshotHash,
        string budgetSnapshotHash,
        RunConversationTurn firstTurn,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset createdAt)
    {
        if (id.Value == Guid.Empty || installationId.Value == Guid.Empty || agentId.Value == Guid.Empty ||
            agentVersion < 0 || providerId.Value == Guid.Empty || providerVersion < 0 ||
            !Text(providerModel, 256) || !Text(name, 120) || !Artifact(systemInstructionArtifact) ||
            skillIds is null || skillIds.Count > 16 || skillIds.Any(item => !Text(item, 128)) ||
            skillIds.Distinct(StringComparer.Ordinal).Count() != skillIds.Count ||
            !Hash(skillSnapshotHash) || !Hash(policySnapshotHash) || !Hash(budgetSnapshotHash) ||
            !ValidTurn(firstTurn, createdAt) || firstTurn.Sequence != 1 ||
            firstTurn.State is not RunConversationTurnState.Pending ||
            !Text(actorId.Value, 256) || !Text(idempotencyKey, 256) ||
            !Text(correlationId.Value, 128) ||
            causationId is { } causation && !Text(causation.Value, 128))
        {
            return Invalid("Conversation identity, authority, artifacts, or first turn are invalid.");
        }

        var snapshot = new RunConversationSnapshot(
            id,
            installationId,
            agentId,
            agentVersion,
            providerId,
            providerVersion,
            providerModel,
            name,
            systemInstructionArtifact,
            skillIds.ToArray(),
            skillSnapshotHash,
            policySnapshotHash,
            budgetSnapshotHash,
            0,
            RunConversationState.Running,
            [firstTurn],
            EmptyHash,
            EmptyHash,
            createdAt,
            createdAt,
            actorId,
            idempotencyKey,
            correlationId,
            causationId);
        return DomainResult.Success(snapshot with { SnapshotHash = ComputeHash(snapshot) });
    }

    public static DomainResult<RunConversationSnapshot> AddTurn(
        RunConversationSnapshot current,
        RunConversationTurn turn,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not RunConversationState.Ready ||
            current.Turns.Count >= MaximumTurns || !ValidTurn(turn, occurredAt) ||
            turn.Sequence != current.Turns.Count + 1 || turn.State is not RunConversationTurnState.Pending ||
            current.Turns.Any(item => item.Id == turn.Id || item.TaskId == turn.TaskId ||
                string.Equals(item.IdempotencyKey, turn.IdempotencyKey, StringComparison.Ordinal)))
        {
            return Conflict("A new turn requires a ready conversation, a unique bounded identity, and sequential order.");
        }

        return DomainResult.Success(Next(
            current, [.. current.Turns, turn], RunConversationState.Running, occurredAt));
    }

    public static DomainResult<RunConversationSnapshot> StartTurn(
        RunConversationSnapshot current,
        RunConversationTurnId turnId,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not (
            RunConversationState.Running or RunConversationState.NeedsResume))
        {
            return Conflict("Only the current pending or resumable turn can start.");
        }

        var index = FindTurn(current, turnId);
        if (index != current.Turns.Count - 1 || current.Turns[index].State is not (
            RunConversationTurnState.Pending or RunConversationTurnState.NeedsResume or
            RunConversationTurnState.Running))
        {
            return Conflict("The requested turn is not the current resumable turn.");
        }

        var turns = current.Turns.ToArray();
        turns[index] = turns[index] with
        {
            State = RunConversationTurnState.Running,
            UpdatedAt = occurredAt,
            FailureCode = null,
            Retryable = false,
        };
        return DomainResult.Success(Next(current, turns, RunConversationState.Running, occurredAt));
    }

    public static DomainResult<RunConversationSnapshot> CompleteTurn(
        RunConversationSnapshot current,
        RunConversationTurnId turnId,
        ArtifactReference responseArtifact,
        string responseHash,
        ModelUsage? usage,
        ModelFinishReason finishReason,
        int contextRedactionCount,
        int eventCount,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not RunConversationState.Running ||
            !Artifact(responseArtifact) || !Hash(responseHash) || !Hash(evidenceHash) ||
            !Enum.IsDefined(finishReason) || contextRedactionCount < 0 || eventCount < 1)
        {
            return Invalid("Turn completion requires current authority and bounded response evidence.");
        }

        var index = FindTurn(current, turnId);
        if (index != current.Turns.Count - 1 || current.Turns[index].State is not RunConversationTurnState.Running)
        {
            return Conflict("Only the running current turn can complete.");
        }

        var turns = current.Turns.ToArray();
        turns[index] = turns[index] with
        {
            State = RunConversationTurnState.Completed,
            ResponseArtifact = responseArtifact,
            ResponseHash = responseHash,
            Usage = usage,
            FinishReason = finishReason,
            ContextRedactionCount = contextRedactionCount,
            EventCount = eventCount,
            EvidenceHash = evidenceHash,
            FailureCode = null,
            Retryable = false,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(Next(current, turns, RunConversationState.Ready, occurredAt));
    }

    public static DomainResult<RunConversationSnapshot> FailTurn(
        RunConversationSnapshot current,
        RunConversationTurnId turnId,
        FailureCode failureCode,
        bool retryable,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || !Enum.IsDefined(failureCode) || !Hash(evidenceHash))
        {
            return Invalid("Turn failure requires current typed, hash-bound evidence.");
        }

        var index = FindTurn(current, turnId);
        if (index != current.Turns.Count - 1 || current.Turns[index].State is not (
            RunConversationTurnState.Pending or RunConversationTurnState.Running))
        {
            return Conflict("Only the current incomplete turn can fail or await resume.");
        }

        var turns = current.Turns.ToArray();
        turns[index] = turns[index] with
        {
            State = retryable ? RunConversationTurnState.NeedsResume : RunConversationTurnState.Failed,
            EvidenceHash = evidenceHash,
            FailureCode = failureCode,
            Retryable = retryable,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(Next(current, turns,
            retryable ? RunConversationState.NeedsResume : RunConversationState.Failed,
            occurredAt));
    }

    public static DomainResult<RunConversationSnapshot> AwaitToolApproval(
        RunConversationSnapshot current,
        RunConversationTurnId turnId,
        RunConversationToolCall toolCall,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not RunConversationState.Running ||
            toolCall is null || toolCall.TurnId != turnId ||
            toolCall.State is not RunConversationToolCallState.AwaitingApproval ||
            !ValidToolCall(toolCall, occurredAt) || !Hash(evidenceHash) ||
            current.ToolCalls.Count >= 32 || current.ToolCalls.Any(item =>
                string.Equals(item.ToolCallId, toolCall.ToolCallId, StringComparison.Ordinal)))
        {
            return Invalid("A pending tool call requires current bounded model evidence and a unique identity.");
        }

        var index = FindTurn(current, turnId);
        if (index != current.Turns.Count - 1 ||
            current.Turns[index].State is not RunConversationTurnState.Running)
        {
            return Conflict("Only the running current turn can await a tool approval.");
        }

        var turns = current.Turns.ToArray();
        turns[index] = turns[index] with
        {
            State = RunConversationTurnState.NeedsResume,
            EvidenceHash = evidenceHash,
            FailureCode = FailureCode.ApprovalRequired,
            Retryable = true,
            UpdatedAt = occurredAt,
        };
        var next = Next(current, turns, RunConversationState.NeedsResume, occurredAt) with
        {
            ToolCalls = [.. current.ToolCalls, toolCall],
            SnapshotHash = EmptyHash,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    public static DomainResult<RunConversationSnapshot> ResolveToolCall(
        RunConversationSnapshot current,
        RunConversationTurnId turnId,
        string toolCallId,
        string resultJson,
        string resultHash,
        bool isError,
        bool denied,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not RunConversationState.NeedsResume ||
            !Text(toolCallId, 256) || !JsonObject(resultJson, 65_536) || !Hash(resultHash))
        {
            return Invalid("Tool resolution requires the current pending call and bounded JSON evidence.");
        }

        var index = current.ToolCalls.ToList().FindIndex(item =>
            item.TurnId == turnId && string.Equals(item.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (index < 0 || current.ToolCalls[index].State is not RunConversationToolCallState.AwaitingApproval)
        {
            return Conflict("The requested tool call is not awaiting an operator decision.");
        }

        var toolCalls = current.ToolCalls.ToArray();
        toolCalls[index] = toolCalls[index] with
        {
            State = denied ? RunConversationToolCallState.Denied : RunConversationToolCallState.Executed,
            ResultJson = resultJson,
            ResultHash = resultHash,
            IsError = isError || denied,
            UpdatedAt = occurredAt,
        };
        var next = current with
        {
            Version = current.Version + 1,
            ToolCalls = toolCalls,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    public static DomainResult<RunConversationSnapshot> CancelTurn(
        RunConversationSnapshot current,
        RunConversationTurnId turnId,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt))
        {
            return Conflict("Only a current conversation can be canceled.");
        }

        var index = FindTurn(current, turnId);
        if (index != current.Turns.Count - 1 || current.Turns[index].State is not (
            RunConversationTurnState.Pending or RunConversationTurnState.Running or
            RunConversationTurnState.NeedsResume))
        {
            return Conflict("Only the current incomplete turn can be canceled.");
        }

        var turns = current.Turns.ToArray();
        turns[index] = turns[index] with
        {
            State = RunConversationTurnState.Canceled,
            Retryable = false,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(Next(current, turns, RunConversationState.Canceled, occurredAt));
    }

    public static bool IsConsistent(RunConversationSnapshot? snapshot) => snapshot is not null &&
        snapshot.Id.Value != Guid.Empty && snapshot.InstallationId.Value != Guid.Empty &&
        snapshot.AgentId.Value != Guid.Empty && snapshot.AgentVersion >= 0 &&
        snapshot.ProviderId.Value != Guid.Empty && snapshot.ProviderVersion >= 0 &&
        Text(snapshot.ProviderModel, 256) && Text(snapshot.Name, 120) &&
        Artifact(snapshot.SystemInstructionArtifact) && snapshot.SkillIds is not null &&
        snapshot.SkillIds.Count <= 16 && snapshot.SkillIds.All(item => Text(item, 128)) &&
        snapshot.SkillIds.Distinct(StringComparer.Ordinal).Count() == snapshot.SkillIds.Count &&
        Hash(snapshot.SkillSnapshotHash) && Hash(snapshot.PolicySnapshotHash) && Hash(snapshot.BudgetSnapshotHash) &&
        snapshot.Version >= 0 && Enum.IsDefined(snapshot.State) && snapshot.Turns is { Count: > 0 and <= MaximumTurns } &&
        snapshot.Turns.Select((turn, index) => ValidTurn(turn, snapshot.UpdatedAt) && turn.Sequence == index + 1)
            .All(valid => valid) &&
        snapshot.Turns.Select(turn => turn.Id).Distinct().Count() == snapshot.Turns.Count &&
        snapshot.Turns.Select(turn => turn.TaskId).Distinct().Count() == snapshot.Turns.Count &&
        snapshot.Turns.Select(turn => turn.IdempotencyKey).Distinct(StringComparer.Ordinal).Count() == snapshot.Turns.Count &&
        snapshot.ToolCalls is { Count: <= 32 } && snapshot.ToolCalls.All(item =>
            ValidToolCall(item, snapshot.UpdatedAt) && snapshot.Turns.Any(turn => turn.Id == item.TurnId)) &&
        snapshot.ToolCalls.Select(item => item.ToolCallId).Distinct(StringComparer.Ordinal).Count() == snapshot.ToolCalls.Count &&
        StateMatches(snapshot) && Hash(snapshot.PreviousSnapshotHash) && Hash(snapshot.SnapshotHash) &&
        snapshot.CreatedAt != default && snapshot.UpdatedAt >= snapshot.CreatedAt &&
        Text(snapshot.ActorId.Value, 256) && Text(snapshot.IdempotencyKey, 256) &&
        Text(snapshot.CorrelationId.Value, 128) &&
        (snapshot.CausationId is null || Text(snapshot.CausationId.Value.Value, 128)) &&
        string.Equals(snapshot.SnapshotHash, ComputeHash(snapshot), StringComparison.Ordinal);

    private static bool StateMatches(RunConversationSnapshot snapshot)
    {
        var state = snapshot.Turns[^1].State;
        return snapshot.State switch
        {
            RunConversationState.Running => state is RunConversationTurnState.Pending or RunConversationTurnState.Running,
            RunConversationState.Ready => state is RunConversationTurnState.Completed,
            RunConversationState.NeedsResume => state is RunConversationTurnState.NeedsResume,
            RunConversationState.Failed => state is RunConversationTurnState.Failed,
            RunConversationState.Canceled => state is RunConversationTurnState.Canceled,
            _ => false,
        };
    }

    private static bool ValidTurn(RunConversationTurn? turn, DateTimeOffset ceiling) => turn is not null &&
        turn.Id.Value != Guid.Empty && turn.Sequence is >= 1 and <= MaximumTurns &&
        turn.TaskId.Value != Guid.Empty && Enum.IsDefined(turn.State) && Artifact(turn.PromptArtifact) &&
        (turn.ResponseArtifact is null || Artifact(turn.ResponseArtifact)) && Hash(turn.RequestHash) &&
        (turn.ResponseHash is null || Hash(turn.ResponseHash)) &&
        turn.ResponseDepth is "concise" or "balanced" or "detailed" or "extended" or "maximum" &&
        turn.MaximumOutputTokens is >= 1 and <= 262_144 &&
        turn.MaximumWallClockSeconds is >= 1 and <= 270 &&
        (turn.FinishReason is null || Enum.IsDefined(turn.FinishReason.Value)) &&
        turn.ContextRedactionCount >= 0 && turn.EventCount >= 0 &&
        (turn.EvidenceHash is null || Hash(turn.EvidenceHash)) &&
        (turn.FailureCode is null || Enum.IsDefined(turn.FailureCode.Value)) &&
        Text(turn.IdempotencyKey, 256) && turn.CreatedAt != default &&
        turn.UpdatedAt >= turn.CreatedAt && turn.UpdatedAt <= ceiling &&
        turn.State is RunConversationTurnState.Completed
            ? turn!.ResponseArtifact is not null && turn.ResponseHash is not null && turn.EvidenceHash is not null &&
                turn.FinishReason is not null && turn.EventCount > 0 && turn.FailureCode is null && !turn.Retryable
            : turn!.ResponseArtifact is null && turn.ResponseHash is null && turn.FinishReason is null;

    private static RunConversationSnapshot Next(
        RunConversationSnapshot current,
        IReadOnlyList<RunConversationTurn> turns,
        RunConversationState state,
        DateTimeOffset occurredAt)
    {
        var next = current with
        {
            Version = current.Version + 1,
            State = state,
            Turns = turns.ToArray(),
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return next with { SnapshotHash = ComputeHash(next) };
    }

    private static bool CanMutate(RunConversationSnapshot? snapshot, DateTimeOffset occurredAt) =>
        IsConsistent(snapshot) && occurredAt >= snapshot!.UpdatedAt;

    private static int FindTurn(RunConversationSnapshot snapshot, RunConversationTurnId id) =>
        snapshot.Turns.ToList().FindIndex(turn => turn.Id == id);

    private static string ComputeHash(RunConversationSnapshot snapshot)
    {
        var builder = new StringBuilder(4096);
        Append(builder, snapshot.Id);
        Append(builder, snapshot.InstallationId);
        Append(builder, snapshot.AgentId);
        Append(builder, snapshot.AgentVersion);
        Append(builder, snapshot.ProviderId);
        Append(builder, snapshot.ProviderVersion);
        Append(builder, snapshot.ProviderModel);
        Append(builder, snapshot.Name);
        AppendArtifact(builder, snapshot.SystemInstructionArtifact);
        foreach (var skillId in snapshot.SkillIds) Append(builder, skillId);
        Append(builder, snapshot.SkillSnapshotHash);
        Append(builder, snapshot.PolicySnapshotHash);
        Append(builder, snapshot.BudgetSnapshotHash);
        Append(builder, snapshot.Version);
        Append(builder, snapshot.State);
        foreach (var turn in snapshot.Turns)
        {
            Append(builder, turn.Id);
            Append(builder, turn.Sequence);
            Append(builder, turn.TaskId);
            Append(builder, turn.State);
            AppendArtifact(builder, turn.PromptArtifact);
            if (turn.ResponseArtifact is { } response) AppendArtifact(builder, response);
            Append(builder, turn.RequestHash);
            Append(builder, turn.ResponseHash ?? string.Empty);
            Append(builder, turn.ResponseDepth);
            Append(builder, turn.MaximumOutputTokens);
            Append(builder, turn.MaximumWallClockSeconds);
            Append(builder, turn.Usage?.InputTokens ?? 0);
            Append(builder, turn.Usage?.OutputTokens ?? 0);
            Append(builder, turn.Usage?.ToolCalls ?? 0);
            Append(builder, turn.Usage?.Cost ?? 0);
            Append(builder, turn.Usage?.Currency ?? string.Empty);
            Append(builder, turn.FinishReason?.ToString() ?? string.Empty);
            Append(builder, turn.ContextRedactionCount);
            Append(builder, turn.EventCount);
            Append(builder, turn.EvidenceHash ?? string.Empty);
            Append(builder, turn.FailureCode?.ToString() ?? string.Empty);
            Append(builder, turn.Retryable);
            Append(builder, turn.IdempotencyKey);
            Append(builder, turn.CreatedAt.UtcTicks);
            Append(builder, turn.UpdatedAt.UtcTicks);
        }
        foreach (var toolCall in snapshot.ToolCalls)
        {
            Append(builder, toolCall.TurnId);
            Append(builder, toolCall.ToolCallId);
            Append(builder, toolCall.ToolName);
            Append(builder, toolCall.ArgumentsJson);
            Append(builder, toolCall.RequestHash);
            Append(builder, toolCall.State);
            Append(builder, toolCall.ResultJson ?? string.Empty);
            Append(builder, toolCall.ResultHash ?? string.Empty);
            Append(builder, toolCall.IsError);
            Append(builder, toolCall.RequestedAt.UtcTicks);
            Append(builder, toolCall.UpdatedAt.UtcTicks);
        }
        Append(builder, snapshot.PreviousSnapshotHash);
        Append(builder, snapshot.CreatedAt.UtcTicks);
        Append(builder, snapshot.UpdatedAt.UtcTicks);
        Append(builder, snapshot.ActorId);
        Append(builder, snapshot.IdempotencyKey);
        Append(builder, snapshot.CorrelationId);
        Append(builder, snapshot.CausationId?.Value ?? string.Empty);
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

    private static bool Artifact(ArtifactReference? artifact) => artifact is not null &&
        Hash(artifact.ContentHash) && artifact.Length is >= 1 and <= 4_194_304 &&
        string.Equals(artifact.MediaType, "text/plain; charset=utf-8", StringComparison.Ordinal) &&
        artifact.CreatedAt != default;

    private static bool ValidToolCall(RunConversationToolCall? call, DateTimeOffset ceiling)
    {
        if (call is null || call.TurnId.Value == Guid.Empty || !Text(call.ToolCallId, 256) ||
            !Text(call.ToolName, 128) || !JsonObject(call.ArgumentsJson, 16_384) ||
            !Hash(call.RequestHash) || !Enum.IsDefined(call.State) || call.RequestedAt == default ||
            call.UpdatedAt < call.RequestedAt || call.UpdatedAt > ceiling)
        {
            return false;
        }
        return call.State is RunConversationToolCallState.AwaitingApproval
            ? call.ResultJson is null && call.ResultHash is null && !call.IsError
            : JsonObject(call.ResultJson, 65_536) && Hash(call.ResultHash);
    }

    private static bool JsonObject(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(character => character == '\0')) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value);
            return document.RootElement.ValueKind is System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool Hash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value.All(character => !char.IsControl(character));

    private static DomainResult<RunConversationSnapshot> Invalid(string message) =>
        DomainResult.Fail<RunConversationSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<RunConversationSnapshot> Conflict(string message) =>
        DomainResult.Fail<RunConversationSnapshot>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
