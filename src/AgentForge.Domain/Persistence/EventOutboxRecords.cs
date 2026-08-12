using System.Text.Json;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Persistence;

public sealed record OutboxEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    string MessageType,
    string PayloadJson,
    DateTimeOffset? ProcessedAt,
    int Attempts,
    long Version);

public static class OutboxEventValidator
{
    public static DomainResult<bool> Validate(OutboxEvent? value)
    {
        if (value is null || value.Id == Guid.Empty || value.OccurredAt == default ||
            string.IsNullOrWhiteSpace(value.MessageType) || value.MessageType.Length > 512 ||
            value.MessageType.Any(char.IsControl) || string.IsNullOrWhiteSpace(value.PayloadJson) ||
            value.PayloadJson.Length > 1_048_576 || value.Attempts is < 0 or > 10_000 || value.Version < 0 ||
            value.ProcessedAt < value.OccurredAt || !IsJsonObject(value.PayloadJson))
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure, "Outbox event is invalid or exceeds a security bound."));
        return DomainResult.Success(true);
    }

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
