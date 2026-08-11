using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal static class ModelContractValidator
{
    private const int MaximumMessages = 256;
    private const int MaximumPartsPerMessage = 64;
    private const int MaximumTools = 128;
    private const int MaximumTextCharacters = 1_048_576;
    private const int MaximumTotalTextCharacters = 4_194_304;
    private const int MaximumJsonCharacters = 262_144;
    private const long MaximumAttachmentBytes = 107_374_182_400;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static DomainResult<ModelProviderDescriptor> NormalizeDescriptor(ModelProviderDescriptor descriptor)
    {
        if (descriptor is null || descriptor.ProfileId.Value == Guid.Empty ||
            !IsProviderType(descriptor.ProviderType) || !IsBoundedText(descriptor.Model, 256) ||
            descriptor.Capabilities is null || descriptor.Capabilities.Count is < 2 or > 64)
        {
            return Invalid<ModelProviderDescriptor>("Model provider identity or capability evidence is invalid.");
        }

        var seen = new HashSet<(ModelCapability Capability, ModelCapabilityEvidenceSource Source)>();
        var evidence = new List<ModelCapabilityEvidence>(descriptor.Capabilities.Count);
        foreach (var item in descriptor.Capabilities)
        {
            if (item is null || !Enum.IsDefined(item.Capability) || !Enum.IsDefined(item.Source) ||
                !Enum.IsDefined(item.Availability) || !IsBoundedText(item.Evidence, 1024) ||
                item.ObservedAt == default || item.ExpiresAt is { } expiresAt && expiresAt <= item.ObservedAt ||
                !seen.Add((item.Capability, item.Source)))
            {
                return Invalid<ModelProviderDescriptor>(
                    "Model capability evidence must be typed, bounded, current, and unique by source.");
            }

            evidence.Add(item);
        }

        var normalized = descriptor with
        {
            Capabilities = new ReadOnlyCollection<ModelCapabilityEvidence>(evidence),
            Routing = descriptor.Routing is null ? null : descriptor.Routing with { },
        };
        if (normalized.Routing is not null && !ValidateRoutingEvidence(normalized.Routing))
        {
            return Invalid<ModelProviderDescriptor>("Model provider routing evidence is invalid or unbounded.");
        }

        if (!Supports(normalized, ModelCapability.TextGeneration) ||
            !Supports(normalized, ModelCapability.Streaming))
        {
            return Invalid<ModelProviderDescriptor>(
                "Every streaming provider must have unopposed text-generation and streaming evidence.");
        }

        return DomainResult.Success(normalized);
    }

    public static DomainResult<PreparedModelRequest> NormalizeRequest(
        ModelRequest request,
        ModelProviderDescriptor descriptor,
        DateTimeOffset evaluatedAt)
    {
        if (request is null || request.Id.Value == Guid.Empty || !IsBoundedText(request.Model, 256) ||
            !string.Equals(request.Model, descriptor.Model, StringComparison.Ordinal) ||
            request.Messages is null || request.Messages.Count is < 1 or > MaximumMessages ||
            request.Tools is null || request.Tools.Count > MaximumTools || request.ResponseFormat is null ||
            request.Limits is null || !IsBoundedText(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBoundedText(causation.Value, 128) ||
            request.Temperature is < 0 or > 2 || request.TopP is <= 0 or > 1 ||
            request.Limits.MaximumOutputTokens is < 1 or > 1_000_000 ||
            request.Limits.MaximumToolCalls is < 0 or > 1024 ||
            request.Limits.MaximumEvents is < 2 or > 1_000_000 ||
            request.Limits.MaximumWallClockSeconds is < 1 or > 86_400)
        {
            return Invalid<PreparedModelRequest>("Model request identity, sampling, or limits are invalid.");
        }

        var requiredCapabilities = new HashSet<ModelCapability>
        {
            ModelCapability.TextGeneration,
            ModelCapability.Streaming,
        };
        var messages = new List<ModelMessage>(request.Messages.Count);
        long totalCharacters = 0;
        foreach (var message in request.Messages)
        {
            var normalized = NormalizeMessage(message, requiredCapabilities, ref totalCharacters);
            if (!normalized.IsSuccess)
            {
                return DomainResult.Fail<PreparedModelRequest>(normalized.Failure!);
            }

            messages.Add(normalized.Value);
        }

        var tools = new List<ModelToolDefinition>(request.Tools.Count);
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in request.Tools)
        {
            if (tool is null || !IsToolName(tool.Name) || !IsBoundedContent(tool.Description, 4096) ||
                !TryNormalizeJsonObject(tool.InputSchemaJson, MaximumJsonCharacters, out var schema) ||
                !toolNames.Add(tool.Name))
            {
                return Invalid<PreparedModelRequest>(
                    "Model tools require unique normalized names, bounded descriptions, and object schemas.");
            }

            tools.Add(tool with { InputSchemaJson = schema! });
        }

        if (tools.Count > 0)
        {
            requiredCapabilities.Add(ModelCapability.ToolCalls);
        }

        var responseFormat = NormalizeResponseFormat(request.ResponseFormat);
        if (!responseFormat.IsSuccess)
        {
            return DomainResult.Fail<PreparedModelRequest>(responseFormat.Failure!);
        }

        if (responseFormat.Value.Kind is not ModelResponseFormatKind.Text)
        {
            requiredCapabilities.Add(ModelCapability.StructuredOutput);
        }

        var unsupported = requiredCapabilities
            .Where(capability => !Supports(descriptor, capability, evaluatedAt))
            .OrderBy(capability => capability)
            .ToArray();
        if (unsupported.Length > 0)
        {
            return DomainResult.Fail<PreparedModelRequest>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                $"Provider capability is unavailable: {string.Join(", ", unsupported)}."));
        }

        var normalizedRequest = request with
        {
            Messages = new ReadOnlyCollection<ModelMessage>(messages),
            Tools = new ReadOnlyCollection<ModelToolDefinition>(tools),
            ResponseFormat = responseFormat.Value,
            Limits = request.Limits with { },
        };
        return DomainResult.Success(new PreparedModelRequest(
            normalizedRequest,
            ComputeInputHash(normalizedRequest),
            new ReadOnlySet<ModelCapability>(requiredCapabilities)));
    }

    public static bool Supports(
        ModelProviderDescriptor descriptor,
        ModelCapability capability,
        DateTimeOffset? evaluatedAt = null)
    {
        var evidence = descriptor.Capabilities
            .Where(item => item.Capability == capability)
            .ToArray();
        return evidence.Length > 0 && evidence.All(item =>
            item.Availability is ModelCapabilityAvailability.Available &&
            (evaluatedAt is null || item.ObservedAt <= evaluatedAt &&
                (item.ExpiresAt is null || evaluatedAt < item.ExpiresAt)));
    }

    private static DomainResult<ModelMessage> NormalizeMessage(
        ModelMessage message,
        HashSet<ModelCapability> requiredCapabilities,
        ref long totalCharacters)
    {
        if (message is null || !Enum.IsDefined(message.Role) || message.Content is null ||
            message.Content.Count is < 1 or > MaximumPartsPerMessage ||
            message.Name is not null && !IsBoundedText(message.Name, 128))
        {
            return Invalid<ModelMessage>("Model message role, name, or content bounds are invalid.");
        }

        var content = new List<ModelContent>(message.Content.Count);
        foreach (var part in message.Content)
        {
            switch (part)
            {
                case ModelTextContent text when IsBoundedContent(text.Text, MaximumTextCharacters):
                    totalCharacters = checked(totalCharacters + text.Text.Length);
                    content.Add(text with { });
                    break;
                case ModelAttachmentContent attachment when
                    message.Role is ModelMessageRole.User && ValidateAttachment(attachment.Attachment):
                    requiredCapabilities.Add(attachment.Attachment.Modality switch
                    {
                        ModelAttachmentModality.Image => ModelCapability.ImageInput,
                        ModelAttachmentModality.Audio => ModelCapability.AudioInput,
                        ModelAttachmentModality.Document => ModelCapability.DocumentInput,
                        _ => throw new InvalidOperationException("Validated attachment modality was invalid."),
                    });
                    content.Add(new ModelAttachmentContent(attachment.Attachment with { }));
                    break;
                case ModelToolResultContent toolResult when
                    message.Role is ModelMessageRole.Tool && IsIdentifier(toolResult.ToolCallId, 256) &&
                    IsToolName(toolResult.ToolName) &&
                    TryNormalizeJson(toolResult.ResultJson, MaximumJsonCharacters, out var resultJson):
                    totalCharacters = checked(totalCharacters + resultJson!.Length);
                    content.Add(toolResult with { ResultJson = resultJson });
                    break;
                case ModelToolCallContent toolCall when
                    message.Role is ModelMessageRole.Assistant && IsIdentifier(toolCall.ToolCallId, 256) &&
                    IsToolName(toolCall.ToolName) &&
                    TryNormalizeJsonObject(toolCall.ArgumentsJson, MaximumJsonCharacters, out var argumentsJson):
                    totalCharacters = checked(totalCharacters + argumentsJson!.Length);
                    requiredCapabilities.Add(ModelCapability.ToolCalls);
                    content.Add(toolCall with { ArgumentsJson = argumentsJson });
                    break;
                default:
                    return Invalid<ModelMessage>(
                        "Model content must be bounded text, a user attachment reference, or a typed tool result.");
            }

            if (totalCharacters > MaximumTotalTextCharacters)
            {
                return Invalid<ModelMessage>("Model request content exceeds the total character bound.");
            }
        }

        if (message.Role is ModelMessageRole.System && content.Any(item => item is not ModelTextContent) ||
            message.Role is ModelMessageRole.Tool && content.Any(item => item is not ModelToolResultContent))
        {
            return Invalid<ModelMessage>("System and tool messages contain an invalid content kind.");
        }

        return DomainResult.Success(message with
        {
            Content = new ReadOnlyCollection<ModelContent>(content),
        });
    }

    private static DomainResult<ModelResponseFormat> NormalizeResponseFormat(ModelResponseFormat format)
    {
        if (!Enum.IsDefined(format.Kind))
        {
            return Invalid<ModelResponseFormat>("Model response format is invalid.");
        }

        return format.Kind switch
        {
            ModelResponseFormatKind.Text when format.JsonSchema is null => DomainResult.Success(format),
            ModelResponseFormatKind.JsonObject when format.JsonSchema is null => DomainResult.Success(format),
            ModelResponseFormatKind.JsonSchema when
                TryNormalizeJsonObject(format.JsonSchema, MaximumJsonCharacters, out var schema) =>
                    DomainResult.Success(format with { JsonSchema = schema }),
            _ => Invalid<ModelResponseFormat>("Model response format and JSON schema do not match."),
        };
    }

    private static bool ValidateAttachment(ModelAttachmentReference attachment) =>
        attachment is not null && IsSha256(attachment.ArtifactHash) &&
        IsMediaType(attachment.MediaType) && attachment.Length is >= 1 and <= MaximumAttachmentBytes &&
        Enum.IsDefined(attachment.Modality) &&
        (attachment.FileName is null || IsFileName(attachment.FileName));

    internal static string ComputeInputHash(ModelRequest request)
    {
        var canonical = new
        {
            Id = request.Id.ToString(),
            request.Model,
            Messages = request.Messages.Select(message => new
            {
                Role = message.Role.ToString(),
                message.Name,
                Content = message.Content.Select(ToCanonicalContent).ToArray(),
            }).ToArray(),
            Tools = request.Tools.Select(tool => new
            {
                tool.Name,
                tool.Description,
                tool.InputSchemaJson,
            }).ToArray(),
            ResponseFormat = new
            {
                Kind = request.ResponseFormat.Kind.ToString(),
                request.ResponseFormat.JsonSchema,
            },
            request.Limits,
            request.Temperature,
            request.TopP,
            request.Seed,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static CanonicalContent ToCanonicalContent(ModelContent content) => content switch
    {
        ModelTextContent text => new("text", text.Text, null, null, null, null, null),
        ModelAttachmentContent attachment => new(
            "attachment",
            null,
            attachment.Attachment.ArtifactHash,
            attachment.Attachment.MediaType,
            attachment.Attachment.Length,
            attachment.Attachment.Modality.ToString(),
            attachment.Attachment.FileName),
        ModelToolCallContent toolCall => new(
            "tool-call",
            toolCall.ArgumentsJson,
            toolCall.ToolCallId,
            toolCall.ToolName,
            null,
            null,
            null),
        ModelToolResultContent result => new(
            "tool-result",
            result.ResultJson,
            result.ToolCallId,
            result.ToolName,
            null,
            result.IsError.ToString(),
            null),
        _ => throw new InvalidOperationException("Normalized model content was invalid."),
    };

    internal static string ComputeCapabilityEvidenceHash(ModelProviderDescriptor descriptor)
    {
        var canonical = new
        {
            ProfileId = descriptor.ProfileId.ToString(),
            descriptor.ProviderType,
            descriptor.Model,
            Capabilities = descriptor.Capabilities
                .OrderBy(item => item.Capability)
                .ThenBy(item => item.Source)
                .Select(item => new
                {
                    Capability = item.Capability.ToString(),
                    Source = item.Source.ToString(),
                    Availability = item.Availability.ToString(),
                    item.Evidence,
                    item.ObservedAt,
                    item.ExpiresAt,
                })
                .ToArray(),
            Routing = descriptor.Routing is null
                ? null
                : new
                {
                    DataLocation = descriptor.Routing.DataLocation.ToString(),
                    Source = descriptor.Routing.Source.ToString(),
                    descriptor.Routing.MaximumContextTokens,
                    descriptor.Routing.MaximumOutputTokens,
                    descriptor.Routing.ReliabilityBasisPoints,
                    descriptor.Routing.InputCostPerMillionTokens,
                    descriptor.Routing.OutputCostPerMillionTokens,
                    descriptor.Routing.TypicalLatencyMilliseconds,
                    descriptor.Routing.ObservedAt,
                    descriptor.Routing.ExpiresAt,
                },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    internal static bool TryNormalizeJsonObject(string? value, int maximumCharacters, out string? normalized)
    {
        if (!TryNormalizeJson(value, maximumCharacters, out normalized))
        {
            return false;
        }

        using var document = JsonDocument.Parse(normalized!);
        return document.RootElement.ValueKind is JsonValueKind.Object &&
            HasUniquePropertyNames(document.RootElement);
    }

    private static bool ValidateRoutingEvidence(ModelProviderRoutingEvidence evidence) =>
        Enum.IsDefined(evidence.DataLocation) && Enum.IsDefined(evidence.Source) &&
        evidence.MaximumContextTokens is >= 1 and <= 10_000_000 &&
        evidence.MaximumOutputTokens is >= 1 &&
        evidence.MaximumOutputTokens <= evidence.MaximumContextTokens &&
        evidence.ReliabilityBasisPoints is >= 0 and <= 10_000 &&
        evidence.InputCostPerMillionTokens is null or >= 0 and <= 1_000_000 &&
        evidence.OutputCostPerMillionTokens is null or >= 0 and <= 1_000_000 &&
        evidence.TypicalLatencyMilliseconds is >= 0 and <= 3_600_000 &&
        evidence.ObservedAt != default &&
        (evidence.ExpiresAt is null || evidence.ExpiresAt > evidence.ObservedAt);

    internal static bool TryNormalizeJson(string? value, int maximumCharacters, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            if (!HasUniquePropertyNames(document.RootElement))
            {
                return false;
            }

            normalized = JsonSerializer.Serialize(document.RootElement, SerializerOptions);
            return normalized.Length <= maximumCharacters;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasUniquePropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || !HasUniquePropertyNames(property.Value))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                return element.EnumerateArray().All(HasUniquePropertyNames);
            default:
                return true;
        }
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        IsBoundedText(value, maximumLength) && char.IsAsciiLetterOrDigit(value![0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool IsToolName(string? value) =>
        IsBoundedText(value, 128) && (char.IsAsciiLetter(value![0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsProviderType(string? value) =>
        IsIdentifier(value, 128) && string.Equals(value, value!.ToLowerInvariant(), StringComparison.Ordinal);

    private static bool IsMediaType(string? value) =>
        IsBoundedText(value, 128) && value!.Count(character => character == '/') == 1 &&
        value!.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '+' or '-');

    private static bool IsFileName(string value) =>
        IsBoundedText(value, 256) && !value.Contains('/') && !value.Contains('\\') &&
        value is not "." and not "..";

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBoundedContent(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    internal sealed record PreparedModelRequest(
        ModelRequest Request,
        string InputHash,
        IReadOnlySet<ModelCapability> RequiredCapabilities);

    private sealed record CanonicalContent(
        string Kind,
        string? Value,
        string? Reference,
        string? Type,
        long? Length,
        string? Detail,
        string? FileName);
}
