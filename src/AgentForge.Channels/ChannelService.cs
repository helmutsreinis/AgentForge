using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Channels;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Channels;

internal sealed class ChannelService(
    IChannelAdapterCatalog adapters,
    IChannelIdentityResolver identities,
    IChannelAttachmentScanner scanner,
    IChannelRepository repository,
    IInstallationRepository installations,
    IAgentIdentityRepository agents,
    ICapabilityApprovalRepository approvals,
    IAuthorizationContextFactory contextFactory,
    ICapabilityPolicyFactory policyFactory,
    ICapabilityPolicyEvaluator policyEvaluator,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers) : IChannelService
{
    public async Task<DomainResult<NormalizedInboundChannelMessage>> ReceiveAsync(
        ChannelWebhookRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !IsText(request.AccountId, 128) || request.Headers.Count > 32 ||
            request.Headers.Any(item => !IsText(item.Key, 128) || item.Value.Length > 1024) ||
            request.Body.Length is < 2 or > 1_048_576 || request.ReceivedAtUtc.Offset != TimeSpan.Zero ||
            !IsText(request.CorrelationId.Value, 128))
        {
            return Invalid<NormalizedInboundChannelMessage>("Webhook envelope bounds are invalid.");
        }

        var resolved = adapters.Resolve(request.Channel, request.AccountId);
        if (!resolved.IsSuccess) return DomainResult.Fail<NormalizedInboundChannelMessage>(resolved.Failure!);
        var bodyCopy = request.Body.ToArray();
        var parsed = await resolved.Value.AuthenticateAndParseAsync(request with { Body = bodyCopy }, cancellationToken);
        Array.Clear(bodyCopy);
        if (!parsed.IsSuccess) return DomainResult.Fail<NormalizedInboundChannelMessage>(parsed.Failure!);
        if (!ValidateParsed(parsed.Value, request))
        {
            return Invalid<NormalizedInboundChannelMessage>("Authenticated channel message bounds are invalid.");
        }

        var binding = await identities.ResolveAsync(
            request.Channel, request.AccountId, parsed.Value.ExternalSenderId, cancellationToken);
        if (binding is null || binding.Channel != request.Channel || binding.AccountId != request.AccountId ||
            binding.ExternalSenderId != parsed.Value.ExternalSenderId || !IsHash(binding.EvidenceHash))
        {
            return DomainResult.Fail<NormalizedInboundChannelMessage>(new DomainFailure(
                FailureCode.PolicyDenied, "Channel sender identity is not bound."));
        }

        var scanned = ImmutableArray.CreateBuilder<ChannelAttachment>(parsed.Value.Attachments.Length);
        foreach (var attachment in parsed.Value.Attachments)
        {
            if (!ValidateAttachment(attachment)) return Invalid<NormalizedInboundChannelMessage>("Channel attachment is invalid.");
            var status = await scanner.ScanAsync(attachment, cancellationToken);
            if (status != AttachmentScanStatus.Clean)
            {
                return DomainResult.Fail<NormalizedInboundChannelMessage>(new DomainFailure(
                    FailureCode.PolicyDenied, "Channel attachment was rejected by scanning."));
            }

            scanned.Add(attachment with { ScanStatus = status });
        }

        var contentHash = ChannelEvidence.ContentHash(parsed.Value.Text, scanned);
        var messageHash = ChannelEvidence.Hash($"v1\n{request.Channel}\n{request.AccountId}\n{parsed.Value.ExternalMessageId}\n{parsed.Value.ExternalSenderId}\n{parsed.Value.RecipientId}\n{contentHash}\n{parsed.Value.OccurredAtUtc.UtcTicks}\n{parsed.Value.AuthenticationEvidenceHash}\n{binding.EvidenceHash}");
        var existing = await repository.FindInboundAsync(
            request.Channel, request.AccountId, parsed.Value.ExternalMessageId, cancellationToken);
        if (existing is not null)
        {
            return existing.MessageHash == messageHash
                ? DomainResult.Success(existing)
                : DomainResult.Fail<NormalizedInboundChannelMessage>(new DomainFailure(
                    FailureCode.ConcurrencyConflict, "A replayed channel identity has different content."));
        }

        var message = new NormalizedInboundChannelMessage(
            new ChannelMessageId(identifiers.NewGuid()), binding.InstallationId, binding.AgentId,
            binding.ActorId, request.Channel, request.AccountId, parsed.Value.ExternalMessageId,
            parsed.Value.ExternalSenderId, parsed.Value.RecipientId, parsed.Value.Text, scanned.ToImmutable(),
            parsed.Value.OccurredAtUtc, request.ReceivedAtUtc, parsed.Value.AuthenticationEvidenceHash,
            messageHash, $"{parsed.Value.OccurredAtUtc.UtcTicks:D19}:{parsed.Value.ExternalMessageId}",
            request.CorrelationId);
        await repository.AddInboundAsync(message, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            binding.InstallationId, binding.ActorId, request.CorrelationId, null, "channel.received",
            AuditOutcome.Succeeded,
            new { Channel = request.Channel.ToString(), request.AccountId, parsed.Value.ExternalMessageId },
            new { message.MessageHash, binding.EvidenceHash, AttachmentCount = message.Attachments.Length, message.OrderKey },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(message) : DomainResult.Fail<NormalizedInboundChannelMessage>(commit.Failure!);
    }

    public async Task<DomainResult<ChannelDelivery>> SendAsync(
        ChannelSendRequest request,
        CancellationToken cancellationToken)
    {
        if (!ValidateSend(request)) return Invalid<ChannelDelivery>("Channel send identity, content, attachments, or policy are invalid.");
        var contentHash = ChannelEvidence.ContentHash(request.Text, request.Attachments);
        var requestHash = ChannelEvidence.RequestHash(request, contentHash);
        var existing = await repository.FindDeliveryByIdempotencyKeyAsync(
            request.InstallationId, request.IdempotencyKey, cancellationToken);
        if (existing is not null && (existing.Id != request.Id || existing.RequestHash != requestHash))
        {
            return Conflict<ChannelDelivery>("Channel send idempotency is bound to another request.");
        }

        if (existing?.State is ChannelDeliveryState.Sent or ChannelDeliveryState.DeadLetter)
        {
            return DomainResult.Success(existing);
        }

        var authority = await ReadAuthorityAsync(request, cancellationToken);
        if (!authority.IsSuccess) return DomainResult.Fail<ChannelDelivery>(authority.Failure!);
        var (installation, agent) = authority.Value;
        if (IsQuiet(request.Policy, clock.UtcNow))
        {
            return DomainResult.Fail<ChannelDelivery>(new DomainFailure(FailureCode.PolicyDenied, "Channel quiet hours are active."));
        }

        var sentCount = await repository.CountSentAsync(
            request.InstallationId, request.AgentId, request.Channel, clock.UtcNow.AddHours(-1), cancellationToken);
        if (sentCount >= request.Policy.MaximumPerHour)
        {
            return DomainResult.Fail<ChannelDelivery>(new DomainFailure(FailureCode.BudgetExceeded, "Channel rate limit is exhausted."));
        }

        var resolved = adapters.Resolve(request.Channel, request.AccountId);
        if (!resolved.IsSuccess) return DomainResult.Fail<ChannelDelivery>(resolved.Failure!);
        ChannelDelivery delivery;
        if (existing is null)
        {
            var authorization = contextFactory.Create(new CapabilityInvocationRequest(
                installation.Id, installation.Version, agent.Id, agent.Version, request.ActorId,
                "channel:send", CapabilityRiskClass.ExternalMutation, null, null, null,
                JsonSerializer.Serialize(new { request.Channel, request.AccountId, request.RecipientId, contentHash }),
                AuthorizationTargetKind.Recipient, request.RecipientId, null,
                request.CorrelationId, request.CausationId));
            if (!authorization.IsSuccess) return DomainResult.Fail<ChannelDelivery>(authorization.Failure!);
            var approval = await approvals.FindLatestAsync(installation.Id, agent.Id, authorization.Value.RequestHash, cancellationToken);
            var evaluation = policyEvaluator.Evaluate(
                authorization.Value, policyFactory.Create(agent, authorization.Value), approval, clock.UtcNow);
            if (evaluation.Decision == CapabilityDecision.Deny)
                return DomainResult.Fail<ChannelDelivery>(new DomainFailure(FailureCode.PolicyDenied, evaluation.Reason));
            if (evaluation.Decision == CapabilityDecision.RequireApproval)
                return DomainResult.Fail<ChannelDelivery>(new DomainFailure(FailureCode.ApprovalRequired, evaluation.Reason));
            CapabilityApprovalId? approvalId = null;
            if (evaluation.ApprovalId is { } selected)
            {
                if (approval is null || approval.Id != selected) return Invalid<ChannelDelivery>("Approval evidence changed.");
                var consumed = CapabilityApprovalStateMachine.Consume(approval, authorization.Value.RequestHash, clock.UtcNow);
                if (!consumed.IsSuccess) return DomainResult.Fail<ChannelDelivery>(consumed.Failure!);
                await approvals.UpdateAsync(consumed.Value, approval.Version, cancellationToken);
                approvalId = selected;
            }

            delivery = new ChannelDelivery(
                request.Id, request.InstallationId, request.AgentId, request.Channel, request.AccountId,
                request.RecipientId, requestHash, contentHash, ChannelDeliveryState.Authorized, 0, null,
                approvalId, null, clock.UtcNow, clock.UtcNow, request.IdempotencyKey, request.ActorId,
                request.CorrelationId, request.CausationId, 0);
            await repository.AddDeliveryAsync(delivery, cancellationToken);
            await RecordDeliveryAsync(delivery, "channel.send-authorized", cancellationToken);
            var authorizationCommit = await unitOfWork.CommitAsync(cancellationToken);
            if (!authorizationCommit.Succeeded) return DomainResult.Fail<ChannelDelivery>(authorizationCommit.Failure!);
        }
        else
        {
            delivery = existing;
        }

        var result = await resolved.Value.SendAsync(request, delivery.RequestHash, cancellationToken);
        var nextState = result.Succeeded
            ? ChannelDeliveryState.Sent
            : !result.DeliveryUncertain && result.Retryable && delivery.AttemptCount + 1 < request.Policy.MaximumAttempts
                ? ChannelDeliveryState.RetryPending
                : ChannelDeliveryState.DeadLetter;
        var next = delivery with
        {
            State = nextState,
            AttemptCount = delivery.AttemptCount + 1,
            ProviderMessageId = result.Succeeded ? result.ProviderMessageId : null,
            LastAttemptEvidenceHash = result.EvidenceHash,
            UpdatedAtUtc = clock.UtcNow,
            Version = delivery.Version + 1,
        };
        await repository.UpdateDeliveryAsync(next, delivery.Version, cancellationToken);
        await RecordDeliveryAsync(next, nextState == ChannelDeliveryState.Sent ? "channel.sent" : "channel.delivery-failed", cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(next) : DomainResult.Fail<ChannelDelivery>(commit.Failure!);
    }

    private async Task<DomainResult<(InstallationSnapshot Installation, AgentIdentity Agent)>> ReadAuthorityAsync(
        ChannelSendRequest request, CancellationToken cancellationToken)
    {
        var installation = await installations.ReadAsync(cancellationToken);
        var agent = await agents.FindByIdAsync(request.AgentId, cancellationToken);
        return installation.Id == request.InstallationId && installation.State == InstallationState.Ready &&
            installation.Version == request.ExpectedInstallationVersion && agent is not null &&
            agent.InstallationId == installation.Id && agent.Version == request.AgentVersion
            ? DomainResult.Success((installation, agent))
            : DomainResult.Fail<(InstallationSnapshot, AgentIdentity)>(new DomainFailure(
                FailureCode.PolicyDenied, "Channel send requires exact current Ready authority."));
    }

    private async Task RecordDeliveryAsync(ChannelDelivery delivery, string operation, CancellationToken cancellationToken) =>
        await audit.RecordAsync(new AuditRecordRequest(
            delivery.InstallationId, delivery.ActorId, delivery.CorrelationId, delivery.CausationId,
            operation, delivery.State == ChannelDeliveryState.Sent ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            new { DeliveryId = delivery.Id.Value, delivery.Channel, delivery.AccountId, delivery.RecipientId },
            new { delivery.RequestHash, delivery.ContentHash, delivery.State, delivery.AttemptCount, delivery.LastAttemptEvidenceHash },
            delivery.State == ChannelDeliveryState.DeadLetter ? "DeliveryDeadLetter" : null), cancellationToken);

    private static bool ValidateParsed(ParsedChannelMessage value, ChannelWebhookRequest request) =>
        IsText(value.ExternalMessageId, 256) && IsText(value.ExternalSenderId, 256) &&
        IsText(value.RecipientId, 256) && value.Text is { Length: <= 16_384 } &&
        !value.Text.Any(character => character == '\0') && value.Attachments.Length <= 8 &&
        value.OccurredAtUtc.Offset == TimeSpan.Zero && value.OccurredAtUtc <= request.ReceivedAtUtc.AddMinutes(5) &&
        value.OccurredAtUtc >= request.ReceivedAtUtc.AddDays(-7) && IsHash(value.AuthenticationEvidenceHash);

    private static bool ValidateSend(ChannelSendRequest request) => request is not null &&
        request.Id.Value != Guid.Empty && request.InstallationId.Value != Guid.Empty &&
        request.AgentId.Value != Guid.Empty && request.ExpectedInstallationVersion >= 0 && request.AgentVersion >= 0 &&
        IsText(request.ActorId.Value, 256) && IsText(request.AccountId, 128) && IsText(request.RecipientId, 256) &&
        request.Text is { Length: >= 1 and <= 16_384 } && !request.Text.Any(character => character == '\0') &&
        request.Attachments.Length <= 8 && request.Attachments.All(ValidateAttachment) &&
        request.Attachments.All(item => item.ScanStatus == AttachmentScanStatus.Clean) &&
        IsText(request.Policy.TimeZoneId, 128) && request.Policy.MaximumPerHour is >= 1 and <= 1000 &&
        request.Policy.MaximumAttempts is >= 1 and <= 10 && IsText(request.IdempotencyKey, 128) &&
        IsText(request.CorrelationId.Value, 128);

    private static bool ValidateAttachment(ChannelAttachment value) => IsText(value.FileName, 256) &&
        IsText(value.MediaType, 128) && value.Length is >= 0 and <= 10_485_760 && IsHash(value.ContentHash);

    private static bool IsQuiet(ChannelDeliveryPolicy policy, DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return true; }
        catch (InvalidTimeZoneException) { return true; }
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return policy.QuietStart == policy.QuietEnd ? false : policy.QuietStart < policy.QuietEnd
            ? local >= policy.QuietStart && local < policy.QuietEnd
            : local >= policy.QuietStart || local < policy.QuietEnd;
    }

    private static bool IsText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    private static bool IsHash(string value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal);
    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
    private static DomainResult<T> Conflict<T>(string message) => DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
