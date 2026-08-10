using System.Buffers;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Security;
using Microsoft.Extensions.Options;

namespace AgentForge.Security;

internal sealed class StructuredSensitiveDataRedactor(IOptions<SecurityOptions> options) : ISensitiveDataRedactor
{
    private const string Placeholder = "[REDACTED]";
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.Ordinal)
    {
        "accesstoken",
        "apikey",
        "authorization",
        "clientsecret",
        "connectionstring",
        "cookie",
        "credential",
        "password",
        "passwd",
        "privatekey",
        "refreshtoken",
        "secret",
        "token",
    };
    private readonly SecurityOptions _settings = options.Value;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = options.Value.MaximumRedactionDepth,
    };

    public RedactionResult Redact(object? value)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
        if (utf8.Length > _settings.MaximumRedactionPayloadBytes)
        {
            throw new ArgumentException("The payload exceeds the configured audit redaction limit.", nameof(value));
        }

        using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
        {
            MaxDepth = _settings.MaximumRedactionDepth,
        });
        var output = new ArrayBufferWriter<byte>(Math.Min(utf8.Length, _settings.MaximumRedactionPayloadBytes));
        using var writer = new Utf8JsonWriter(output);
        var redactionCount = 0;
        WriteElement(writer, document.RootElement, null, ref redactionCount);
        writer.Flush();

        return new RedactionResult(
            new RedactedData(Encoding.UTF8.GetString(output.WrittenSpan)),
            redactionCount);
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        string? propertyName,
        ref int redactionCount)
    {
        if (propertyName is not null && IsSensitiveProperty(propertyName))
        {
            writer.WriteStringValue(Placeholder);
            redactionCount++;
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, property.Name, ref redactionCount);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, null, ref redactionCount);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (LooksLikeSecret(text))
                {
                    writer.WriteStringValue(Placeholder);
                    redactionCount++;
                }
                else
                {
                    writer.WriteStringValue(text);
                }

                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}.");
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        var normalized = new string(propertyName
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return SensitivePropertyNames.Contains(normalized)
            || normalized.EndsWith("password", StringComparison.Ordinal)
            || normalized.EndsWith("secret", StringComparison.Ordinal)
            || normalized.EndsWith("token", StringComparison.Ordinal)
            || normalized.EndsWith("apikey", StringComparison.Ordinal)
            || normalized.EndsWith("credential", StringComparison.Ordinal)
            || normalized.EndsWith("privatekey", StringComparison.Ordinal);
    }

    private static bool LooksLikeSecret(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 12)
        {
            return false;
        }

        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("sk-", StringComparison.Ordinal) && trimmed.Length >= 20
            || trimmed.StartsWith("xoxb-", StringComparison.Ordinal) && trimmed.Length >= 20
            || trimmed.StartsWith("xoxp-", StringComparison.Ordinal) && trimmed.Length >= 20
            || trimmed.StartsWith("AKIA", StringComparison.Ordinal) && trimmed.Length == 20
            || IsGitHubToken(trimmed)
            || LooksLikeJwt(trimmed)
            || trimmed.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
            || trimmed.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
            || trimmed.Contains("password=", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("pwd=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitHubToken(string value) =>
        value.Length >= 24 &&
        value.StartsWith("gh", StringComparison.Ordinal) &&
        value[2] is 'p' or 'o' or 'u' or 's' or 'r' &&
        value[3] == '_';

    private static bool LooksLikeJwt(string value)
    {
        if (!value.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return false;
        }

        var firstDot = value.IndexOf('.');
        var lastDot = value.LastIndexOf('.');
        return firstDot >= 8 && lastDot > firstDot + 8 && lastDot < value.Length - 8;
    }
}
