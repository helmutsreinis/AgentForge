using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Security;

internal sealed class AuthorizationContextFactory : IAuthorizationContextFactory
{
    private const int MaximumParametersBytes = 65_536;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public DomainResult<AuthorizationContext> Create(CapabilityInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstallationId.Value == Guid.Empty || request.InstallationVersion < 0 ||
            request.AgentId.Value == Guid.Empty ||
            request.AgentVersion < 0 || !IsIdentifier(request.ActorId.Value, 256) ||
            !IsIdentifier(request.CorrelationId.Value, 128) ||
            (request.CausationId is { } causation && !IsIdentifier(causation.Value, 128)) ||
            !Enum.IsDefined(request.RiskClass) || !IsCapabilityId(request.CapabilityId) ||
            !IsOptionalIdentifier(request.ToolId, 256) ||
            !IsOptionalIdentifier(request.ToolVersion, 128))
        {
            return Invalid("Authorization identity, capability, tool, or version is invalid.");
        }

        var parameters = CanonicalizeParameters(request.ParametersJson);
        if (!parameters.IsSuccess)
        {
            return DomainResult.Fail<AuthorizationContext>(parameters.Failure!);
        }

        var target = NormalizeTarget(request.TargetKind, request.Target);
        if (!target.IsSuccess)
        {
            return DomainResult.Fail<AuthorizationContext>(target.Failure!);
        }

        var workspace = NormalizeWorkspace(request.Workspace);
        if (!workspace.IsSuccess)
        {
            return DomainResult.Fail<AuthorizationContext>(workspace.Failure!);
        }

        var capabilityId = request.CapabilityId.Trim().ToLowerInvariant();
        var toolId = NormalizeOptional(request.ToolId)?.ToLowerInvariant();
        var toolVersion = NormalizeOptional(request.ToolVersion);
        var parametersHash = Hash(parameters.Value);
        var targetHash = Hash(target.Value ?? string.Empty);
        var workspaceHash = Hash(workspace.Value ?? string.Empty);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            InstallationId = request.InstallationId.ToString(),
            request.InstallationVersion,
            AgentId = request.AgentId.ToString(),
            request.AgentVersion,
            ActorId = request.ActorId.Value.Trim(),
            CapabilityId = capabilityId,
            RiskClass = request.RiskClass.ToString(),
            ToolId = toolId,
            ToolVersion = toolVersion,
            ParametersHash = parametersHash,
            TargetKind = request.TargetKind.ToString(),
            TargetHash = targetHash,
            WorkspaceHash = workspaceHash,
        }, SerializerOptions);
        var requestHash = Hash(requestBytes);
        return DomainResult.Success(new AuthorizationContext(
            request.InstallationId,
            request.InstallationVersion,
            request.AgentId,
            request.AgentVersion,
            new ActorId(request.ActorId.Value.Trim()),
            capabilityId,
            request.RiskClass,
            toolId,
            toolVersion,
            parameters.Value,
            parametersHash,
            request.TargetKind,
            target.Value,
            targetHash,
            workspace.Value,
            workspaceHash,
            new CorrelationId(request.CorrelationId.Value.Trim()),
            request.CausationId is null ? null : new CorrelationId(request.CausationId.Value.Value.Trim()),
            requestHash));
    }

    private static DomainResult<string> CanonicalizeParameters(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson) || Encoding.UTF8.GetByteCount(parametersJson) > MaximumParametersBytes)
        {
            return DomainResult.Fail<string>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Parameters must be a bounded JSON object."));
        }

        try
        {
            using var document = JsonDocument.Parse(parametersJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return DomainResult.Fail<string>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    "Parameters must be a JSON object."));
            }

            var output = new ArrayBufferWriter<byte>(Math.Min(parametersJson.Length, MaximumParametersBytes));
            using var writer = new Utf8JsonWriter(output);
            WriteCanonical(writer, document.RootElement);
            writer.Flush();
            if (output.WrittenCount > MaximumParametersBytes)
            {
                return DomainResult.Fail<string>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    "Canonical parameters exceed 64 KiB."));
            }

            return DomainResult.Success(Encoding.UTF8.GetString(output.WrittenSpan));
        }
        catch (JsonException)
        {
            return DomainResult.Fail<string>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Parameters are not valid bounded JSON."));
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw new JsonException("Duplicate JSON properties are not allowed.");
                }

                foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported JSON parameter value.");
        }
    }

    private static DomainResult<string?> NormalizeTarget(AuthorizationTargetKind kind, string? value)
    {
        var normalized = NormalizeOptional(value);
        switch (kind)
        {
            case AuthorizationTargetKind.None when normalized is null:
                return DomainResult.Success<string?>(null);
            case AuthorizationTargetKind.None:
                return InvalidTarget("A target is not allowed for target kind None.");
            case AuthorizationTargetKind.FileSystemPath:
                return NormalizeAbsolutePath(normalized, "Target path");
            case AuthorizationTargetKind.Uri:
                if (normalized is null || normalized.Length > 2048 ||
                    !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                    !string.IsNullOrEmpty(uri.UserInfo))
                {
                    return InvalidTarget("Target URI must be absolute, bounded, and contain no user information.");
                }

                return DomainResult.Success<string?>(uri.AbsoluteUri);
            case AuthorizationTargetKind.Device:
            case AuthorizationTargetKind.Recipient:
                return normalized is not null && IsIdentifier(normalized, 1024)
                    ? DomainResult.Success<string?>(normalized)
                    : InvalidTarget("Opaque target must be a bounded printable value.");
            default:
                return InvalidTarget("Target kind is unsupported.");
        }
    }

    private static DomainResult<string?> NormalizeWorkspace(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null
            ? DomainResult.Success<string?>(null)
            : NormalizeAbsolutePath(normalized, "Workspace path");
    }

    private static DomainResult<string?> NormalizeAbsolutePath(string? value, string label)
    {
        if (value is null || value.Length > 2048)
        {
            return InvalidTarget($"{label} must be an absolute bounded path.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                return InvalidTarget($"{label} must be fully qualified.");
            }

            return DomainResult.Success<string?>(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidTarget($"{label} is invalid.");
        }
    }

    private static bool IsCapabilityId(string? value) =>
        IsIdentifier(value, 256) && value!.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool IsOptionalIdentifier(string? value, int maximumLength) =>
        value is null || IsIdentifier(value, maximumLength);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static DomainResult<AuthorizationContext> Invalid(string message) =>
        DomainResult.Fail<AuthorizationContext>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<string?> InvalidTarget(string message) =>
        DomainResult.Fail<string?>(new DomainFailure(FailureCode.ValidationFailure, message));
}
