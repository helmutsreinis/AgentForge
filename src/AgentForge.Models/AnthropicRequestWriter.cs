using System.Buffers;
using System.Text.Json;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal static class AnthropicRequestWriter
{
    public static DomainResult<byte[]> Write(ModelRequest request, AnthropicModelProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", request.Model);
                writer.WriteNumber("max_tokens", request.Limits.MaximumOutputTokens);
                writer.WriteBoolean("stream", true);
                if (request.Temperature is { } temperature)
                {
                    writer.WriteNumber("temperature", temperature);
                }

                if (request.TopP is { } topP)
                {
                    writer.WriteNumber("top_p", topP);
                }

                var system = request.Messages.Where(message => message.Role is ModelMessageRole.System).ToArray();
                if (system.Length > 0)
                {
                    writer.WritePropertyName("system");
                    writer.WriteStartArray();
                    foreach (var message in system)
                    {
                        foreach (var content in message.Content)
                        {
                            if (content is not ModelTextContent text)
                            {
                                return Invalid("Anthropic system messages support text content only.");
                            }

                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", text.Text);
                            writer.WriteEndObject();
                        }
                    }

                    writer.WriteEndArray();
                }

                writer.WritePropertyName("messages");
                writer.WriteStartArray();
                foreach (var message in request.Messages.Where(message => message.Role is not ModelMessageRole.System))
                {
                    WriteMessage(writer, message);
                }

                writer.WriteEndArray();
                if (request.Tools.Count > 0)
                {
                    writer.WritePropertyName("tools");
                    writer.WriteStartArray();
                    foreach (var tool in request.Tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", tool.Name);
                        writer.WriteString("description", tool.Description);
                        writer.WritePropertyName("input_schema");
                        using var schema = JsonDocument.Parse(tool.InputSchemaJson);
                        schema.RootElement.WriteTo(writer);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return buffer.WrittenCount <= options.MaximumRequestBytes
                ? DomainResult.Success(buffer.WrittenSpan.ToArray())
                : DomainResult.Fail<byte[]>(new DomainFailure(
                    FailureCode.BudgetExceeded,
                    "The Anthropic request exceeded its byte bound."));
        }
        catch (JsonException)
        {
            return Invalid("The Anthropic request contained invalid normalized JSON.");
        }
    }

    private static void WriteMessage(Utf8JsonWriter writer, ModelMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role is ModelMessageRole.Assistant ? "assistant" : "user");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        foreach (var content in message.Content)
        {
            switch (content)
            {
                case ModelTextContent text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;
                case ModelToolCallContent toolCall:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", toolCall.ToolCallId);
                    writer.WriteString("name", toolCall.ToolName);
                    writer.WritePropertyName("input");
                    using (var input = JsonDocument.Parse(toolCall.ArgumentsJson))
                    {
                        input.RootElement.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                    break;
                case ModelToolResultContent toolResult:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", toolResult.ToolCallId);
                    writer.WriteBoolean("is_error", toolResult.IsError);
                    writer.WriteString("content", toolResult.ResultJson);
                    writer.WriteEndObject();
                    break;
                default:
                    throw new JsonException("Unsupported Anthropic message content.");
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static DomainResult<byte[]> Invalid(string message) =>
        DomainResult.Fail<byte[]>(new DomainFailure(FailureCode.ValidationFailure, message));
}
