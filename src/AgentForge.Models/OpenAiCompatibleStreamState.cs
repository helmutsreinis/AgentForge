using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;

namespace AgentForge.Models;

internal sealed class OpenAiCompatibleStreamState
{
    private const int MaximumToolArgumentsCharacters = 262_144;
    private readonly ModelRequest _request;
    private readonly IClock _clock;
    private readonly Dictionary<int, ToolCallState> _toolCalls = [];
    private readonly StringBuilder _structuredOutput = new();
    private long _sequence = 1;
    private ModelFinishReason? _finishReason;
    private bool _usageObserved;
    private bool _terminal;

    public OpenAiCompatibleStreamState(ModelRequest request, IClock clock)
    {
        _request = request;
        _clock = clock;
    }

    public StreamTranslation Process(string data)
    {
        if (_terminal)
        {
            return StreamTranslation.EmptyTerminal;
        }

        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
        {
            return Complete();
        }

        if (!ModelContractValidator.TryNormalizeJson(data, MaximumToolArgumentsCharacters, out var normalized))
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider stream contained invalid JSON.");
        }

        using var document = JsonDocument.Parse(normalized!);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider stream event was not an object.");
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
        {
            var code = MapProviderError(error);
            return Fail(
                code,
                "The provider returned an error event.",
                retryable: code is ModelProviderErrorCode.RateLimited or ModelProviderErrorCode.ProviderUnavailable);
        }

        var events = new List<ModelStreamEvent>();
        var handled = false;
        if (root.TryGetProperty("choices", out var choices))
        {
            if (choices.ValueKind is not JsonValueKind.Array || choices.GetArrayLength() > 1)
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid choice set.");
            }

            if (choices.GetArrayLength() == 1)
            {
                handled = true;
                var translated = ProcessChoice(choices[0], events);
                if (translated is not null)
                {
                    return Combine(events, translated.Value);
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind is not JsonValueKind.Null)
        {
            handled = true;
            var translated = ProcessUsage(usage, events);
            if (translated is not null)
            {
                return Combine(events, translated.Value);
            }
        }

        if (!handled)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider stream event contained no usable data.");
        }

        return new StreamTranslation(new ReadOnlyCollection<ModelStreamEvent>(events), false);
    }

    public StreamTranslation EndOfStream() => _terminal ? StreamTranslation.EmptyTerminal : Complete();

    private StreamTranslation? ProcessChoice(JsonElement choice, List<ModelStreamEvent> events)
    {
        if (choice.ValueKind is not JsonValueKind.Object ||
            !choice.TryGetProperty("index", out var index) || !index.TryGetInt32(out var choiceIndex) ||
            choiceIndex != 0)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid choice index.");
        }

        var handled = false;
        if (choice.TryGetProperty("delta", out var delta))
        {
            handled = true;
            if (delta.ValueKind is not JsonValueKind.Object)
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid streaming delta.");
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind is not JsonValueKind.Null)
            {
                if (reasoning.ValueKind is not JsonValueKind.String)
                {
                    return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned invalid reasoning content.");
                }

                if (!string.IsNullOrEmpty(reasoning.GetString()))
                {
                    return Fail(
                        ModelProviderErrorCode.UnsupportedCapability,
                        "The provider emitted an unsupported reasoning-content channel.");
                }
            }

            if (delta.TryGetProperty("content", out var content) && content.ValueKind is not JsonValueKind.Null)
            {
                if (content.ValueKind is not JsonValueKind.String)
                {
                    return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned invalid text content.");
                }

                var text = content.GetString() ?? string.Empty;
                if (text.Length > 0)
                {
                    if (_finishReason is not null)
                    {
                        return Fail(ModelProviderErrorCode.InvalidResponse, "The provider emitted content after finishing.");
                    }

                    if (_request.ResponseFormat.Kind is ModelResponseFormatKind.Text)
                    {
                        var budget = AddNonterminal(
                            events,
                            sequence => new ModelTextDeltaEvent(
                                _request.Id,
                                sequence,
                                _clock.UtcNow,
                                text));
                        if (budget is not null)
                        {
                            return budget;
                        }
                    }
                    else
                    {
                        if (_structuredOutput.Length + text.Length > MaximumToolArgumentsCharacters)
                        {
                            return Fail(
                                ModelProviderErrorCode.BudgetExceeded,
                                "The structured model response exceeded its character bound.");
                        }

                        _structuredOutput.Append(text);
                    }
                }
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls))
            {
                var translated = ProcessToolCalls(toolCalls, events);
                if (translated is not null)
                {
                    return translated;
                }
            }
        }

        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind is not JsonValueKind.Null)
        {
            handled = true;
            if (finish.ValueKind is not JsonValueKind.String ||
                !TryMapFinishReason(finish.GetString(), out var mapped) ||
                _finishReason is not null)
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid finish reason.");
            }

            _finishReason = mapped;
        }

        if (!handled)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider choice contained no streaming data.");
        }

        return null;
    }

    private StreamTranslation? ProcessToolCalls(JsonElement calls, List<ModelStreamEvent> events)
    {
        if (calls.ValueKind is not JsonValueKind.Array)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned invalid tool-call deltas.");
        }

        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind is not JsonValueKind.Object ||
                !call.TryGetProperty("index", out var indexElement) ||
                !indexElement.TryGetInt32(out var index) || index < 0 || index >= _request.Limits.MaximumToolCalls)
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid tool-call index.");
            }

            if (!_toolCalls.TryGetValue(index, out var state))
            {
                if (_toolCalls.Count >= _request.Limits.MaximumToolCalls)
                {
                    return Fail(ModelProviderErrorCode.BudgetExceeded, "The provider exceeded the tool-call limit.");
                }

                state = new ToolCallState();
                _toolCalls.Add(index, state);
            }

            if (call.TryGetProperty("type", out var type) && type.ValueKind is not JsonValueKind.Null &&
                (type.ValueKind is not JsonValueKind.String ||
                    !string.Equals(type.GetString(), "function", StringComparison.Ordinal)))
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an unsupported tool-call type.");
            }

            string? identifier = null;
            if (call.TryGetProperty("id", out var id) && id.ValueKind is not JsonValueKind.Null)
            {
                if (id.ValueKind is not JsonValueKind.String || !IsIdentifier(id.GetString(), 256) ||
                    state.Identifier is not null && !string.Equals(state.Identifier, id.GetString(), StringComparison.Ordinal))
                {
                    return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid tool-call identifier.");
                }

                identifier = id.GetString();
                state.Identifier = identifier;
            }

            string? toolName = null;
            var argumentsDelta = string.Empty;
            if (call.TryGetProperty("function", out var function) && function.ValueKind is not JsonValueKind.Null)
            {
                if (function.ValueKind is not JsonValueKind.Object)
                {
                    return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an invalid tool function.");
                }

                if (function.TryGetProperty("name", out var name) && name.ValueKind is not JsonValueKind.Null)
                {
                    if (name.ValueKind is not JsonValueKind.String || !IsToolName(name.GetString()) ||
                        !_request.Tools.Any(item => string.Equals(item.Name, name.GetString(), StringComparison.Ordinal)) ||
                        state.ToolName is not null && !string.Equals(state.ToolName, name.GetString(), StringComparison.Ordinal))
                    {
                        return Fail(ModelProviderErrorCode.InvalidResponse, "The provider requested an unlisted tool.");
                    }

                    toolName = name.GetString();
                    state.ToolName = toolName;
                }

                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind is not JsonValueKind.Null)
                {
                    if (arguments.ValueKind is not JsonValueKind.String)
                    {
                        return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned invalid tool arguments.");
                    }

                    argumentsDelta = arguments.GetString() ?? string.Empty;
                    if (state.Arguments.Length + argumentsDelta.Length > MaximumToolArgumentsCharacters)
                    {
                        return Fail(ModelProviderErrorCode.BudgetExceeded, "Tool-call arguments exceeded their character bound.");
                    }

                    state.Arguments.Append(argumentsDelta);
                }
            }

            if (state.Identifier is null)
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "A tool-call delta arrived before its identifier.");
            }

            var budget = AddNonterminal(
                events,
                sequence => new ModelToolCallDeltaEvent(
                    _request.Id,
                    sequence,
                    _clock.UtcNow,
                    state.Identifier,
                    toolName,
                    argumentsDelta));
            if (budget is not null)
            {
                return budget.Value;
            }
        }

        return null;
    }

    private StreamTranslation? ProcessUsage(JsonElement usage, List<ModelStreamEvent> events)
    {
        if (_usageObserved || usage.ValueKind is not JsonValueKind.Object ||
            !TryGetNonnegativeInt64(usage, "prompt_tokens", out var inputTokens) ||
            !TryGetNonnegativeInt64(usage, "completion_tokens", out var outputTokens))
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned invalid or duplicate usage.");
        }

        _usageObserved = true;
        if (outputTokens > _request.Limits.MaximumOutputTokens)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "The provider exceeded the output-token limit.");
        }

        return AddNonterminal(
            events,
            sequence => new ModelUsageEvent(
                _request.Id,
                sequence,
                _clock.UtcNow,
                new ModelUsage(inputTokens, outputTokens, _toolCalls.Count, null, null)));
    }

    private StreamTranslation Complete()
    {
        if (_finishReason is null)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "The provider stream ended without a finish reason.");
        }

        if (_finishReason is ModelFinishReason.ToolCalls && _toolCalls.Count == 0 ||
            _finishReason is not ModelFinishReason.ToolCalls && _toolCalls.Count > 0)
        {
            return Fail(ModelProviderErrorCode.InvalidResponse, "Tool calls and finish reason did not agree.");
        }

        var completedCalls = new List<(string Identifier, string ToolName, string Arguments)>();
        foreach (var item in _toolCalls.OrderBy(item => item.Key))
        {
            if (item.Value.Identifier is null || item.Value.ToolName is null ||
                !ModelContractValidator.TryNormalizeJsonObject(
                    item.Value.Arguments.ToString(),
                    MaximumToolArgumentsCharacters,
                    out var arguments))
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned an incomplete tool call.");
            }

            completedCalls.Add((item.Value.Identifier, item.Value.ToolName, arguments!));
        }

        string? structured = null;
        if (_request.ResponseFormat.Kind is not ModelResponseFormatKind.Text)
        {
            if (!ModelContractValidator.TryNormalizeJson(
                _structuredOutput.ToString(),
                MaximumToolArgumentsCharacters,
                out structured))
            {
                return Fail(ModelProviderErrorCode.InvalidResponse, "The provider returned invalid structured output.");
            }
        }

        var nonterminalCount = completedCalls.Count + (structured is null ? 0 : 1);
        if (_sequence + nonterminalCount >= _request.Limits.MaximumEvents)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "The provider exceeded the event limit.");
        }

        var events = new List<ModelStreamEvent>(nonterminalCount + 1);
        foreach (var call in completedCalls)
        {
            events.Add(new ModelToolCallCompletedEvent(
                _request.Id,
                _sequence++,
                _clock.UtcNow,
                call.Identifier,
                call.ToolName,
                call.Arguments));
        }

        if (structured is not null)
        {
            events.Add(new ModelStructuredOutputEvent(
                _request.Id,
                _sequence++,
                _clock.UtcNow,
                structured));
        }

        events.Add(new ModelCompletedEvent(
            _request.Id,
            _sequence++,
            _clock.UtcNow,
            _finishReason.Value));
        _terminal = true;
        return new StreamTranslation(new ReadOnlyCollection<ModelStreamEvent>(events), true);
    }

    private StreamTranslation? AddNonterminal(
        List<ModelStreamEvent> events,
        Func<long, ModelStreamEvent> eventFactory)
    {
        if (_sequence >= _request.Limits.MaximumEvents - 1L)
        {
            return Fail(ModelProviderErrorCode.BudgetExceeded, "The provider exceeded the event limit.");
        }

        events.Add(eventFactory(_sequence++));
        return null;
    }

    private StreamTranslation Fail(
        ModelProviderErrorCode code,
        string message,
        bool retryable = false)
    {
        _terminal = true;
        return new StreamTranslation(
            [new ModelErrorEvent(
                _request.Id,
                Math.Min(_sequence, _request.Limits.MaximumEvents - 1L),
                _clock.UtcNow,
                new ModelProviderError(code, message, retryable))],
            true);
    }

    private static StreamTranslation Combine(
        List<ModelStreamEvent> prefix,
        StreamTranslation terminal)
    {
        if (prefix.Count == 0)
        {
            return terminal;
        }

        var combined = new List<ModelStreamEvent>(prefix.Count + terminal.Events.Count);
        combined.AddRange(prefix);
        combined.AddRange(terminal.Events);
        return new StreamTranslation(new ReadOnlyCollection<ModelStreamEvent>(combined), terminal.IsTerminal);
    }

    private static ModelProviderErrorCode MapProviderError(JsonElement error)
    {
        if (error.ValueKind is not JsonValueKind.Object ||
            !error.TryGetProperty("type", out var type) || type.ValueKind is not JsonValueKind.String)
        {
            return ModelProviderErrorCode.ProviderUnavailable;
        }

        return type.GetString() switch
        {
            "authentication_error" => ModelProviderErrorCode.AuthenticationFailed,
            "rate_limit_error" => ModelProviderErrorCode.RateLimited,
            "invalid_request_error" => ModelProviderErrorCode.InvalidRequest,
            _ => ModelProviderErrorCode.ProviderUnavailable,
        };
    }

    private static bool TryMapFinishReason(string? value, out ModelFinishReason reason)
    {
        reason = value switch
        {
            "stop" => ModelFinishReason.Stop,
            "length" => ModelFinishReason.Length,
            "tool_calls" => ModelFinishReason.ToolCalls,
            "content_filter" => ModelFinishReason.ContentFilter,
            _ => default,
        };
        return value is "stop" or "length" or "tool_calls" or "content_filter";
    }

    private static bool TryGetNonnegativeInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.TryGetInt64(out value) && value >= 0;
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool IsToolName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private sealed class ToolCallState
    {
        public string? Identifier { get; set; }

        public string? ToolName { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}

internal readonly record struct StreamTranslation(
    IReadOnlyList<ModelStreamEvent> Events,
    bool IsTerminal)
{
    public static StreamTranslation EmptyTerminal { get; } = new([], true);
}
