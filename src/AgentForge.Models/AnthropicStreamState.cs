using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;

namespace AgentForge.Models;

internal sealed class AnthropicStreamState(ModelRequest request, IClock clock)
{
    private const int MaximumContentBlocks = 1_024;
    private const int MaximumToolArgumentsCharacters = 262_144;
    private readonly Dictionary<int, ContentBlockState> _blocks = [];
    private long _sequence = 1;
    private long? _inputTokens;
    private long? _outputTokens;
    private ModelFinishReason? _finishReason;
    private bool _messageStarted;
    private bool _messageDeltaObserved;
    private bool _terminal;

    public StreamTranslation Process(string data)
    {
        if (_terminal)
        {
            return StreamTranslation.EmptyTerminal;
        }

        if (!ModelContractValidator.TryNormalizeJson(data, MaximumToolArgumentsCharacters, out var normalized))
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic stream contained invalid JSON.");
        }

        using var document = JsonDocument.Parse(normalized!);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) || type.ValueKind is not JsonValueKind.String)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic stream event lacked a valid type.");
        }

        return type.GetString() switch
        {
            "message_start" => MessageStart(root),
            "content_block_start" => ContentBlockStart(root),
            "content_block_delta" => ContentBlockDelta(root),
            "content_block_stop" => ContentBlockStop(root),
            "message_delta" => MessageDelta(root),
            "message_stop" => MessageStop(),
            "ping" => new StreamTranslation([], false),
            "error" => Error(root),
            _ => Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic stream event type was unsupported."),
        };
    }

    public StreamTranslation EndOfStream() => _terminal
        ? StreamTranslation.EmptyTerminal
        : Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic stream ended before message_stop.");

    private StreamTranslation MessageStart(JsonElement root)
    {
        if (_messageStarted || !root.TryGetProperty("message", out var message) ||
            message.ValueKind is not JsonValueKind.Object ||
            !message.TryGetProperty("model", out var model) || model.ValueKind is not JsonValueKind.String ||
            !string.Equals(model.GetString(), request.Model, StringComparison.Ordinal) ||
            !message.TryGetProperty("usage", out var usage) || usage.ValueKind is not JsonValueKind.Object ||
            !TryGetNonnegativeInt64(usage, "input_tokens", out var inputTokens))
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic message_start event was invalid.");
        }

        _messageStarted = true;
        _inputTokens = inputTokens;
        return new StreamTranslation([], false);
    }

    private StreamTranslation ContentBlockStart(JsonElement root)
    {
        if (!_messageStarted || _messageDeltaObserved || !TryGetIndex(root, out var index) ||
            _blocks.Count >= MaximumContentBlocks || _blocks.ContainsKey(index) ||
            !root.TryGetProperty("content_block", out var block) ||
            block.ValueKind is not JsonValueKind.Object || !block.TryGetProperty("type", out var type) ||
            type.ValueKind is not JsonValueKind.String)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic content block start was invalid.");
        }

        if (string.Equals(type.GetString(), "text", StringComparison.Ordinal))
        {
            if (block.TryGetProperty("text", out var text) &&
                (text.ValueKind is not JsonValueKind.String || !string.IsNullOrEmpty(text.GetString())))
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "Anthropic text blocks must start empty.");
            }

            _blocks.Add(index, new ContentBlockState(ContentBlockKind.Text, null, null));
            return new StreamTranslation([], false);
        }

        if (!string.Equals(type.GetString(), "tool_use", StringComparison.Ordinal) ||
            _blocks.Values.Count(item => item.Kind is ContentBlockKind.Tool) >= request.Limits.MaximumToolCalls ||
            !block.TryGetProperty("id", out var id) || id.ValueKind is not JsonValueKind.String ||
            !IsIdentifier(id.GetString(), 256) ||
            !block.TryGetProperty("name", out var name) || name.ValueKind is not JsonValueKind.String ||
            !request.Tools.Any(tool => string.Equals(tool.Name, name.GetString(), StringComparison.Ordinal)) ||
            !block.TryGetProperty("input", out var input) || input.ValueKind is not JsonValueKind.Object ||
            input.EnumerateObject().Any())
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic tool block was invalid or unlisted.");
        }

        var state = new ContentBlockState(ContentBlockKind.Tool, id.GetString(), name.GetString());
        _blocks.Add(index, state);
        return Add(sequence => new ModelToolCallDeltaEvent(
            request.Id,
            sequence,
            clock.UtcNow,
            state.Identifier!,
            state.ToolName,
            string.Empty));
    }

    private StreamTranslation ContentBlockDelta(JsonElement root)
    {
        if (!_messageStarted || _messageDeltaObserved || !TryGetIndex(root, out var index) ||
            !_blocks.TryGetValue(index, out var state) || state.Stopped ||
            !root.TryGetProperty("delta", out var delta) || delta.ValueKind is not JsonValueKind.Object ||
            !delta.TryGetProperty("type", out var type) || type.ValueKind is not JsonValueKind.String)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic content block delta was invalid.");
        }

        if (state.Kind is ContentBlockKind.Text)
        {
            if (!string.Equals(type.GetString(), "text_delta", StringComparison.Ordinal) ||
                !delta.TryGetProperty("text", out var text) || text.ValueKind is not JsonValueKind.String)
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic text delta was invalid.");
            }

            var value = text.GetString() ?? string.Empty;
            return string.IsNullOrEmpty(value)
                ? new StreamTranslation([], false)
                : Add(sequence => new ModelTextDeltaEvent(request.Id, sequence, clock.UtcNow, value));
        }

        if (!string.Equals(type.GetString(), "input_json_delta", StringComparison.Ordinal) ||
            !delta.TryGetProperty("partial_json", out var partial) || partial.ValueKind is not JsonValueKind.String)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic tool-input delta was invalid.");
        }

        var fragment = partial.GetString() ?? string.Empty;
        if (state.Arguments.Length + fragment.Length > MaximumToolArgumentsCharacters)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "Anthropic tool input exceeded its character bound.");
        }

        state.Arguments.Append(fragment);
        return Add(sequence => new ModelToolCallDeltaEvent(
            request.Id,
            sequence,
            clock.UtcNow,
            state.Identifier!,
            null,
            fragment));
    }

    private StreamTranslation ContentBlockStop(JsonElement root)
    {
        if (!TryGetIndex(root, out var index) || !_blocks.TryGetValue(index, out var state) || state.Stopped)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic content block stop was invalid.");
        }

        state.Stopped = true;
        if (state.Kind is ContentBlockKind.Text)
        {
            return new StreamTranslation([], false);
        }

        if (!ModelContractValidator.TryNormalizeJsonObject(
                state.Arguments.ToString(),
                MaximumToolArgumentsCharacters,
                out var arguments))
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic tool input was not a JSON object.");
        }

        return Add(sequence => new ModelToolCallCompletedEvent(
            request.Id,
            sequence,
            clock.UtcNow,
            state.Identifier!,
            state.ToolName!,
            arguments!));
    }

    private StreamTranslation MessageDelta(JsonElement root)
    {
        if (!_messageStarted || _messageDeltaObserved || _blocks.Values.Any(block => !block.Stopped) ||
            !root.TryGetProperty("delta", out var delta) || delta.ValueKind is not JsonValueKind.Object ||
            !delta.TryGetProperty("stop_reason", out var stopReason) || stopReason.ValueKind is not JsonValueKind.String ||
            !TryMapFinishReason(stopReason.GetString(), out var finishReason) ||
            !root.TryGetProperty("usage", out var usage) || usage.ValueKind is not JsonValueKind.Object ||
            !TryGetNonnegativeInt64(usage, "output_tokens", out var outputTokens))
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic message_delta event was invalid.");
        }

        var toolCount = _blocks.Values.Count(block => block.Kind is ContentBlockKind.Tool);
        if (outputTokens > request.Limits.MaximumOutputTokens ||
            finishReason is ModelFinishReason.ToolCalls && toolCount == 0 ||
            finishReason is not ModelFinishReason.ToolCalls && toolCount > 0)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "Anthropic usage, tools, or finish reason did not agree.");
        }

        _messageDeltaObserved = true;
        _finishReason = finishReason;
        _outputTokens = outputTokens;
        return new StreamTranslation([], false);
    }

    private StreamTranslation MessageStop()
    {
        if (!_messageDeltaObserved || _inputTokens is null || _outputTokens is null || _finishReason is null)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The Anthropic message stopped without terminal evidence.");
        }

        var toolCount = _blocks.Values.Count(block => block.Kind is ContentBlockKind.Tool);
        var events = new List<ModelStreamEvent>();
        var usage = Add(events, sequence => new ModelUsageEvent(
            request.Id,
            sequence,
            clock.UtcNow,
            new ModelUsage(_inputTokens.Value, _outputTokens.Value, toolCount, null, null)));
        if (usage is not null)
        {
            return usage.Value;
        }

        if (_sequence >= request.Limits.MaximumEvents)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "The Anthropic stream exceeded its event limit.");
        }

        events.Add(new ModelCompletedEvent(request.Id, _sequence++, clock.UtcNow, _finishReason.Value));
        _terminal = true;
        return new StreamTranslation(new ReadOnlyCollection<ModelStreamEvent>(events), true);
    }

    private StreamTranslation Error(JsonElement root)
    {
        var code = ModelProviderErrorCode.ProviderUnavailable;
        if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object &&
            error.TryGetProperty("type", out var type) && type.ValueKind is JsonValueKind.String)
        {
            code = type.GetString() switch
            {
                "authentication_error" or "permission_error" => ModelProviderErrorCode.AuthenticationFailed,
                "rate_limit_error" => ModelProviderErrorCode.RateLimited,
                "invalid_request_error" => ModelProviderErrorCode.InvalidRequest,
                "overloaded_error" => ModelProviderErrorCode.ProviderUnavailable,
                _ => ModelProviderErrorCode.ProviderUnavailable,
            };
        }

        return Fail(
            code,
            "Anthropic returned an error event.",
            code is ModelProviderErrorCode.RateLimited or ModelProviderErrorCode.ProviderUnavailable);
    }

    private StreamTranslation Add(Func<long, ModelStreamEvent> eventFactory)
    {
        if (_sequence >= request.Limits.MaximumEvents - 1L)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "The Anthropic stream exceeded its event limit.");
        }

        return new StreamTranslation([eventFactory(_sequence++)], false);
    }

    private StreamTranslation? Add(
        List<ModelStreamEvent> events,
        Func<long, ModelStreamEvent> eventFactory)
    {
        if (_sequence >= request.Limits.MaximumEvents - 1L)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "The Anthropic stream exceeded its event limit.");
        }

        events.Add(eventFactory(_sequence++));
        return null;
    }

    private StreamTranslation Fail(ModelProviderErrorCode code, string message, bool retryable = false)
    {
        _terminal = true;
        return new StreamTranslation(
            [new ModelErrorEvent(
                request.Id,
                Math.Min(_sequence, request.Limits.MaximumEvents - 1L),
                clock.UtcNow,
                new ModelProviderError(code, message, retryable))],
            true);
    }

    private static bool TryGetIndex(JsonElement root, out int index)
    {
        index = 0;
        return root.TryGetProperty("index", out var element) && element.TryGetInt32(out index) &&
            index is >= 0 and < MaximumContentBlocks;
    }

    private static bool TryGetNonnegativeInt64(JsonElement root, string property, out long value)
    {
        value = 0;
        return root.TryGetProperty(property, out var element) && element.TryGetInt64(out value) && value >= 0;
    }

    private static bool TryMapFinishReason(string? value, out ModelFinishReason reason)
    {
        reason = value switch
        {
            "end_turn" or "stop_sequence" => ModelFinishReason.Stop,
            "max_tokens" => ModelFinishReason.Length,
            "tool_use" => ModelFinishReason.ToolCalls,
            "refusal" => ModelFinishReason.ContentFilter,
            _ => default,
        };
        return value is "end_turn" or "stop_sequence" or "max_tokens" or "tool_use" or "refusal";
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private enum ContentBlockKind
    {
        Text,
        Tool,
    }

    private sealed class ContentBlockState(
        ContentBlockKind kind,
        string? identifier,
        string? toolName)
    {
        public ContentBlockKind Kind { get; } = kind;

        public string? Identifier { get; } = identifier;

        public string? ToolName { get; } = toolName;

        public StringBuilder Arguments { get; } = new();

        public bool Stopped { get; set; }
    }
}
