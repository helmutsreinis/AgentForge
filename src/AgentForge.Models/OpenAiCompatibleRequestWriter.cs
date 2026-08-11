using System.Buffers;
using System.Text;
using System.Text.Json;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal static class OpenAiCompatibleRequestWriter
{
    public static DomainResult<byte[]> Write(
        ModelRequest request,
        OpenAiCompatibleModelProviderOptions options)
    {
        try
        {
            var output = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", request.Limits.MaximumOutputTokens);
            if (request.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (request.TopP is { } topP)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (request.Seed is { } seed)
            {
                writer.WriteNumber("seed", seed);
            }

            if (options.IncludeUsageInStream)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            if (options.DisableThinking)
            {
                writer.WriteStartObject("chat_template_kwargs");
                writer.WriteBoolean("enable_thinking", false);
                writer.WriteEndObject();
            }

            WriteMessages(writer, request.Messages);
            WriteTools(writer, request.Tools);
            WriteResponseFormat(writer, request.ResponseFormat);
            writer.WriteEndObject();
            writer.Flush();

            if (output.WrittenCount > options.MaximumRequestBytes)
            {
                return Invalid<byte[]>("The normalized model request exceeds the adapter request bound.");
            }

            return DomainResult.Success(output.WrittenSpan.ToArray());
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return Invalid<byte[]>("The normalized model request could not be translated.");
        }
    }

    private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<ModelMessage> messages)
    {
        writer.WriteStartArray("messages");
        foreach (var message in messages)
        {
            if (message.Role is ModelMessageRole.Tool)
            {
                foreach (var result in message.Content.Cast<ModelToolResultContent>())
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", result.ToolCallId);
                    writer.WriteString("name", result.ToolName);
                    writer.WriteString("content", WriteToolResult(result));
                    writer.WriteEndObject();
                }

                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("role", ToRole(message.Role));
            if (message.Name is not null)
            {
                writer.WriteString("name", message.Name);
            }

            var text = string.Concat(message.Content.OfType<ModelTextContent>().Select(item => item.Text));
            if (text.Length > 0)
            {
                writer.WriteString("content", text);
            }
            else
            {
                writer.WriteNull("content");
            }

            var toolCalls = message.Content.OfType<ModelToolCallContent>().ToArray();
            if (toolCalls.Length > 0)
            {
                writer.WriteStartArray("tool_calls");
                foreach (var toolCall in toolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", toolCall.ToolCallId);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", toolCall.ToolName);
                    writer.WriteString("arguments", toolCall.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ModelToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            writer.WriteRawValue(tool.InputSchemaJson, skipInputValidation: false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteResponseFormat(Utf8JsonWriter writer, ModelResponseFormat responseFormat)
    {
        switch (responseFormat.Kind)
        {
            case ModelResponseFormatKind.Text:
                return;
            case ModelResponseFormatKind.JsonObject:
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
                return;
            case ModelResponseFormatKind.JsonSchema:
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_schema");
                writer.WriteStartObject("json_schema");
                writer.WriteString("name", "agentforge_response");
                writer.WriteBoolean("strict", true);
                writer.WritePropertyName("schema");
                writer.WriteRawValue(responseFormat.JsonSchema!, skipInputValidation: false);
                writer.WriteEndObject();
                writer.WriteEndObject();
                return;
            default:
                throw new InvalidOperationException("The normalized response format was invalid.");
        }
    }

    private static string ToRole(ModelMessageRole role) => role switch
    {
        ModelMessageRole.System => "system",
        ModelMessageRole.User => "user",
        ModelMessageRole.Assistant => "assistant",
        _ => throw new InvalidOperationException("The normalized model message role was invalid."),
    };

    private static string WriteToolResult(ModelToolResultContent result)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteBoolean("is_error", result.IsError);
        writer.WritePropertyName("result");
        writer.WriteRawValue(result.ResultJson, skipInputValidation: false);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
}
