using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;

namespace AgentForge.Runtime;

internal sealed class RunConversationService(
    IRunConversationRepository conversations,
    IArtifactStore artifacts,
    ISensitiveDataRedactor redactor,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : IRunConversationService
{
    private const string TextMediaType = "text/plain; charset=utf-8";

    public async Task<DomainResult<RunConversationMutationResult>> CreateAsync(
        CreateRunConversationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Content(request.SystemInstruction, 24_576) || !Content(request.Prompt, 16_384))
        {
            return Invalid("Conversation system context and first prompt must be bounded printable text.");
        }

        var system = RedactText(request.SystemInstruction);
        var prompt = RedactText(request.Prompt);
        var existing = await conversations.FindByIdempotencyKeyAsync(
            request.InstallationId, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var exact = existing.Id == request.Id && existing.AgentId == request.AgentId &&
                existing.AgentVersion == request.AgentVersion && existing.ProviderId == request.ProviderId &&
                existing.ProviderVersion == request.ProviderVersion &&
                string.Equals(existing.ProviderModel, request.ProviderModel, StringComparison.Ordinal) &&
                string.Equals(existing.Name, request.Name, StringComparison.Ordinal) &&
                string.Equals(existing.SystemInstructionArtifact.ContentHash, HashText(system.Text), StringComparison.Ordinal) &&
                existing.SkillIds.SequenceEqual(request.SkillIds, StringComparer.Ordinal) &&
                string.Equals(existing.SkillSnapshotHash, request.SkillSnapshotHash, StringComparison.Ordinal) &&
                existing.Turns[0].Id == request.TurnId && existing.Turns[0].TaskId == request.TaskId &&
                string.Equals(existing.Turns[0].PromptArtifact.ContentHash, HashText(prompt.Text), StringComparison.Ordinal) &&
                string.Equals(existing.Turns[0].ResponseDepth, request.ResponseDepth, StringComparison.Ordinal) &&
                existing.Turns[0].MaximumOutputTokens == request.MaximumOutputTokens &&
                existing.Turns[0].MaximumWallClockSeconds == request.MaximumWallClockSeconds &&
                string.Equals(existing.Turns[0].IdempotencyKey, request.TurnIdempotencyKey, StringComparison.Ordinal);
            return exact
                ? DomainResult.Success(new RunConversationMutationResult(
                    existing, existing.Turns[0], true, system.Redactions + prompt.Redactions))
                : Conflict("Conversation idempotency is already bound to different input or authority.");
        }

        var systemArtifact = await PutTextAsync(system.Text, cancellationToken);
        var promptArtifact = await PutTextAsync(prompt.Text, cancellationToken);
        var occurredAt = clock.UtcNow;
        var turn = new RunConversationTurn(
            request.TurnId,
            1,
            request.TaskId,
            RunConversationTurnState.Pending,
            promptArtifact,
            null,
            HashRequest(request.TaskId, promptArtifact.ContentHash, request.ResponseDepth,
                request.MaximumOutputTokens, request.MaximumWallClockSeconds),
            null,
            request.ResponseDepth,
            request.MaximumOutputTokens,
            request.MaximumWallClockSeconds,
            null,
            null,
            0,
            0,
            null,
            null,
            false,
            request.TurnIdempotencyKey,
            occurredAt,
            occurredAt);
        var created = RunConversationStateMachine.Create(
            request.Id,
            request.InstallationId,
            request.AgentId,
            request.AgentVersion,
            request.ProviderId,
            request.ProviderVersion,
            request.ProviderModel,
            request.Name,
            systemArtifact,
            request.SkillIds,
            request.SkillSnapshotHash,
            request.PolicySnapshotHash,
            request.BudgetSnapshotHash,
            turn,
            request.ActorId,
            request.IdempotencyKey,
            request.CorrelationId,
            request.CausationId,
            occurredAt);
        return created.IsSuccess
            ? await PersistAsync(created.Value, turn, "runtime.conversation-created",
                system.Redactions + prompt.Redactions, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(created.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> AddTurnAsync(
        AddRunConversationTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Content(request.Prompt, 16_384)) return Invalid("Conversation prompts must be bounded printable text.");

        var current = await conversations.FindLatestAsync(request.ConversationId, cancellationToken);
        if (current is null) return NotFound();
        var prompt = RedactText(request.Prompt);
        var promptHash = HashText(prompt.Text);
        var existing = current.Turns.SingleOrDefault(turn =>
            string.Equals(turn.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal));
        if (existing is not null)
        {
            var exact = existing.Id == request.TurnId && existing.TaskId == request.TaskId &&
                string.Equals(existing.PromptArtifact.ContentHash, promptHash, StringComparison.Ordinal) &&
                string.Equals(existing.ResponseDepth, request.ResponseDepth, StringComparison.Ordinal) &&
                existing.MaximumOutputTokens == request.MaximumOutputTokens &&
                existing.MaximumWallClockSeconds == request.MaximumWallClockSeconds;
            return exact
                ? DomainResult.Success(new RunConversationMutationResult(current, existing, true, prompt.Redactions))
                : Conflict("Turn idempotency is already bound to different input or execution bounds.");
        }
        if (current.Version != request.ExpectedVersion)
        {
            return Conflict("The conversation version is stale. Reload the run details and retry.");
        }

        var promptArtifact = await PutTextAsync(prompt.Text, cancellationToken);
        var occurredAt = AtLeast(clock.UtcNow, current.UpdatedAt);
        var turn = new RunConversationTurn(
            request.TurnId,
            current.Turns.Count + 1,
            request.TaskId,
            RunConversationTurnState.Pending,
            promptArtifact,
            null,
            HashRequest(request.TaskId, promptArtifact.ContentHash, request.ResponseDepth,
                request.MaximumOutputTokens, request.MaximumWallClockSeconds),
            null,
            request.ResponseDepth,
            request.MaximumOutputTokens,
            request.MaximumWallClockSeconds,
            null,
            null,
            0,
            0,
            null,
            null,
            false,
            request.IdempotencyKey,
            occurredAt,
            occurredAt);
        var added = RunConversationStateMachine.AddTurn(current, turn, occurredAt);
        return added.IsSuccess
            ? await PersistAsync(added.Value, turn, "runtime.conversation-turn-added",
                prompt.Redactions, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(added.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> StartTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(conversationId, expectedVersion, cancellationToken);
        if (!current.IsSuccess) return DomainResult.Fail<RunConversationMutationResult>(current.Failure!);
        var turn = current.Value.Turns.SingleOrDefault(item => item.Id == turnId);
        if (turn is null) return Invalid("The conversation turn does not exist.");
        if (turn.State is RunConversationTurnState.Running)
        {
            return DomainResult.Success(new RunConversationMutationResult(current.Value, turn, true));
        }
        var started = RunConversationStateMachine.StartTurn(
            current.Value, turnId, AtLeast(clock.UtcNow, current.Value.UpdatedAt));
        return started.IsSuccess
            ? await PersistAsync(started.Value, started.Value.Turns[^1],
                "runtime.conversation-turn-started", 0, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(started.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> CompleteTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        LocalModelInteractionResult interaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var current = await ReadCurrentAsync(conversationId, expectedVersion, cancellationToken);
        if (!current.IsSuccess) return DomainResult.Fail<RunConversationMutationResult>(current.Failure!);
        var existing = current.Value.Turns.SingleOrDefault(item => item.Id == turnId);
        if (existing is null) return Invalid("The conversation turn does not exist.");
        if (existing.State is RunConversationTurnState.Completed)
        {
            return string.Equals(existing.EvidenceHash, interaction.EvidenceHash, StringComparison.Ordinal)
                ? DomainResult.Success(new RunConversationMutationResult(current.Value, existing, true))
                : Conflict("The completed turn is bound to different model evidence.");
        }

        var response = RedactText(interaction.Text);
        var responseArtifact = await PutTextAsync(response.Text, cancellationToken);
        var completed = RunConversationStateMachine.CompleteTurn(
            current.Value,
            turnId,
            responseArtifact,
            responseArtifact.ContentHash,
            interaction.Usage,
            interaction.FinishReason,
            interaction.ContextRedactionCount + response.Redactions,
            interaction.EventCount,
            interaction.EvidenceHash,
            AtLeast(clock.UtcNow, current.Value.UpdatedAt));
        return completed.IsSuccess
            ? await PersistAsync(completed.Value, completed.Value.Turns[^1],
                "runtime.conversation-turn-completed", response.Redactions, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(completed.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> FailTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        FailureCode failureCode,
        bool retryable,
        string evidenceHash,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(conversationId, expectedVersion, cancellationToken);
        if (!current.IsSuccess) return DomainResult.Fail<RunConversationMutationResult>(current.Failure!);
        var failed = RunConversationStateMachine.FailTurn(
            current.Value, turnId, failureCode, retryable, evidenceHash,
            AtLeast(clock.UtcNow, current.Value.UpdatedAt));
        return failed.IsSuccess
            ? await PersistAsync(failed.Value, failed.Value.Turns[^1],
                retryable ? "runtime.conversation-turn-interrupted" : "runtime.conversation-turn-failed",
                0, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(failed.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> AwaitToolApprovalAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        LocalModelToolCall toolCall,
        string evidenceHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        if (!Content(toolCall.ToolCallId, 256) || !Content(toolCall.ToolName, 128) ||
            !Content(toolCall.ArgumentsJson, 16_384))
        {
            return Invalid("The model tool request is not bounded printable content.");
        }

        var current = await ReadCurrentAsync(conversationId, expectedVersion, cancellationToken);
        if (!current.IsSuccess) return DomainResult.Fail<RunConversationMutationResult>(current.Failure!);
        var arguments = RedactText(toolCall.ArgumentsJson);
        var occurredAt = AtLeast(clock.UtcNow, current.Value.UpdatedAt);
        var pending = new RunConversationToolCall(
            turnId,
            toolCall.ToolCallId,
            toolCall.ToolName,
            arguments.Text,
            HashText($"v1\n{turnId.Value:D}\n{toolCall.ToolCallId}\n{toolCall.ToolName}\n{arguments.Text}"),
            RunConversationToolCallState.AwaitingApproval,
            null,
            null,
            false,
            occurredAt,
            occurredAt);
        var awaiting = RunConversationStateMachine.AwaitToolApproval(
            current.Value, turnId, pending, evidenceHash, occurredAt);
        return awaiting.IsSuccess
            ? await PersistAsync(awaiting.Value, awaiting.Value.Turns[^1],
                "runtime.conversation-tool-approval-required", arguments.Redactions, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(awaiting.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> ResolveToolCallAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        string toolCallId,
        string resultJson,
        bool isError,
        bool denied,
        CancellationToken cancellationToken)
    {
        if (!Content(toolCallId, 256) || !Content(resultJson, 65_536))
        {
            return Invalid("The tool decision result is not bounded printable content.");
        }
        var current = await ReadCurrentAsync(conversationId, expectedVersion, cancellationToken);
        if (!current.IsSuccess) return DomainResult.Fail<RunConversationMutationResult>(current.Failure!);
        var result = RedactText(resultJson);
        var resolved = RunConversationStateMachine.ResolveToolCall(
            current.Value,
            turnId,
            toolCallId,
            result.Text,
            HashText(result.Text),
            isError,
            denied,
            AtLeast(clock.UtcNow, current.Value.UpdatedAt));
        return resolved.IsSuccess
            ? await PersistAsync(resolved.Value, resolved.Value.Turns[^1],
                denied ? "runtime.conversation-tool-denied" : "runtime.conversation-tool-executed",
                result.Redactions, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(resolved.Failure!);
    }

    public async Task<DomainResult<RunConversationMutationResult>> CancelTurnAsync(
        RunConversationId conversationId,
        long expectedVersion,
        RunConversationTurnId turnId,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(conversationId, expectedVersion, cancellationToken);
        if (!current.IsSuccess) return DomainResult.Fail<RunConversationMutationResult>(current.Failure!);
        if (current.Value.Turns[^1].State is RunConversationTurnState.Canceled)
        {
            return DomainResult.Success(new RunConversationMutationResult(
                current.Value, current.Value.Turns[^1], true));
        }
        var canceled = RunConversationStateMachine.CancelTurn(
            current.Value, turnId, AtLeast(clock.UtcNow, current.Value.UpdatedAt));
        return canceled.IsSuccess
            ? await PersistAsync(canceled.Value, canceled.Value.Turns[^1],
                "runtime.conversation-turn-canceled", 0, cancellationToken)
            : DomainResult.Fail<RunConversationMutationResult>(canceled.Failure!);
    }

    public async Task<DomainResult<RunConversationDetails>> GetDetailsAsync(
        RunConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var snapshot = await conversations.FindLatestAsync(conversationId, cancellationToken);
        if (snapshot is null) return DomainResult.Fail<RunConversationDetails>(NotFoundFailure());
        try
        {
            var systemInstruction = await OpenTextAsync(snapshot.SystemInstructionArtifact, cancellationToken);
            var turns = new List<RunConversationTurnContent>(snapshot.Turns.Count);
            foreach (var turn in snapshot.Turns)
            {
                var prompt = await OpenTextAsync(turn.PromptArtifact, cancellationToken);
                var response = turn.ResponseArtifact is null
                    ? null
                    : await OpenTextAsync(turn.ResponseArtifact, cancellationToken);
                turns.Add(new RunConversationTurnContent(turn, prompt, response));
            }
            return DomainResult.Success(new RunConversationDetails(snapshot, systemInstruction, turns));
        }
        catch (InvalidDataException exception)
        {
            return DomainResult.Fail<RunConversationDetails>(new DomainFailure(
                FailureCode.ValidationFailure, exception.Message));
        }
    }

    private async Task<DomainResult<RunConversationSnapshot>> ReadCurrentAsync(
        RunConversationId id,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await conversations.FindLatestAsync(id, cancellationToken);
        return current is null
            ? DomainResult.Fail<RunConversationSnapshot>(NotFoundFailure())
            : current.Version != expectedVersion
                ? DomainResult.Fail<RunConversationSnapshot>(new DomainFailure(
                    FailureCode.ConcurrencyConflict,
                    "The conversation version is stale. Reload the run details and retry."))
                : DomainResult.Success(current);
    }

    private async Task<DomainResult<RunConversationMutationResult>> PersistAsync(
        RunConversationSnapshot snapshot,
        RunConversationTurn turn,
        string operation,
        int redactionCount,
        CancellationToken cancellationToken)
    {
        await conversations.AppendAsync(snapshot, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            snapshot.InstallationId,
            snapshot.ActorId,
            snapshot.CorrelationId,
            snapshot.CausationId,
            operation,
            snapshot.State is RunConversationState.Failed ? AuditOutcome.Failed :
                snapshot.State is RunConversationState.Canceled ? AuditOutcome.Canceled : AuditOutcome.Succeeded,
            new
            {
                ConversationId = snapshot.Id.ToString(),
                snapshot.Version,
                TurnId = turn.Id.ToString(),
                turn.Sequence,
                TaskId = turn.TaskId.ToString(),
                turn.RequestHash,
                PromptArtifactHash = turn.PromptArtifact.ContentHash,
            },
            new
            {
                State = snapshot.State.ToString(),
                TurnState = turn.State.ToString(),
                snapshot.SnapshotHash,
                turn.ResponseHash,
                turn.EvidenceHash,
                FailureCode = turn.FailureCode?.ToString(),
                turn.Retryable,
                PersistenceRedactionCount = redactionCount,
            },
            turn.FailureCode?.ToString()), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new RunConversationMutationResult(snapshot, turn, false, redactionCount))
            : DomainResult.Fail<RunConversationMutationResult>(commit.Failure!);
    }

    private async Task<ArtifactReference> PutTextAsync(string value, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value), writable: false);
        return await artifacts.PutAsync(stream, TextMediaType, cancellationToken);
    }

    private async Task<string> OpenTextAsync(ArtifactReference reference, CancellationToken cancellationToken)
    {
        if (reference.Length is < 1 or > 4_194_304 ||
            !string.Equals(reference.MediaType, TextMediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A conversation text artifact has invalid bounds or media type.");
        }

        await using var source = await artifacts.OpenReadAsync(reference, cancellationToken);
        await using var output = new MemoryStream((int)reference.Length);
        var buffer = new byte[81_920];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length += read;
            if (length > reference.Length) throw new InvalidDataException("A conversation artifact exceeded its recorded length.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (length != reference.Length) throw new InvalidDataException("A conversation artifact length did not match its receipt.");
        var bytes = output.ToArray();
        if (!string.Equals(HashBytes(bytes), reference.ContentHash, StringComparison.Ordinal))
            throw new InvalidDataException("A conversation artifact failed content-hash verification.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private (string Text, int Redactions) RedactText(string value)
    {
        var redacted = redactor.Redact(value);
        var text = JsonSerializer.Deserialize<string>(redacted.Data.Json)
            ?? throw new InvalidOperationException("The text redactor returned no string value.");
        return (text, redacted.RedactionCount);
    }

    private static string HashRequest(
        OrchestrationTaskId taskId,
        string promptHash,
        string responseDepth,
        int maximumOutputTokens,
        int maximumWallClockSeconds) => HashText(
        $"v1\n{taskId.Value:D}\n{promptHash}\n{responseDepth}\n{maximumOutputTokens}\n{maximumWallClockSeconds}");

    private static string HashText(string value) => HashBytes(Encoding.UTF8.GetBytes(value));

    private static string HashBytes(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static bool Content(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static DateTimeOffset AtLeast(DateTimeOffset value, DateTimeOffset minimum) =>
        value < minimum ? minimum : value;

    private static DomainResult<RunConversationMutationResult> Invalid(string message) =>
        DomainResult.Fail<RunConversationMutationResult>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<RunConversationMutationResult> Conflict(string message) =>
        DomainResult.Fail<RunConversationMutationResult>(new DomainFailure(FailureCode.ConcurrencyConflict, message));

    private static DomainResult<RunConversationMutationResult> NotFound() =>
        DomainResult.Fail<RunConversationMutationResult>(NotFoundFailure());

    private static DomainFailure NotFoundFailure() =>
        new(FailureCode.ValidationFailure, "The durable conversation does not exist.");
}
