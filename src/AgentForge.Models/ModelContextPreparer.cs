using System.Collections.ObjectModel;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

public sealed class ModelContextPreparer(ISensitiveDataRedactor redactor) : IModelContextPreparer
{
    public const string PolicyName = "agentforge-context-redaction-v1";

    public DomainResult<PreparedModelContext> Prepare(ModelRequest request)
    {
        if (request is null)
        {
            return Invalid("Model context is required.");
        }

        try
        {
            if (!IsSafeIdentity(request.Model) || !IsSafeIdentity(request.CorrelationId.Value) ||
                request.CausationId is { } causation && !IsSafeIdentity(causation.Value))
            {
                return Invalid("Model context identity contains sensitive content and cannot cross the provider boundary.");
            }

            var redactionCount = 0;
            var messages = new List<ModelMessage>(request.Messages?.Count ?? 0);
            if (request.Messages is null)
            {
                return Invalid("Model context messages are required.");
            }

            foreach (var message in request.Messages)
            {
                var prepared = PrepareMessage(message, ref redactionCount);
                if (!prepared.IsSuccess)
                {
                    return DomainResult.Fail<PreparedModelContext>(prepared.Failure!);
                }

                messages.Add(prepared.Value);
            }

            var tools = new List<ModelToolDefinition>(request.Tools?.Count ?? 0);
            if (request.Tools is null || request.ResponseFormat is null || request.Limits is null)
            {
                return Invalid("Model context contracts are required.");
            }

            foreach (var tool in request.Tools)
            {
                if (tool is null || !IsSafeIdentity(tool.Name))
                {
                    return Invalid("Model tool identity contains sensitive content and cannot cross the provider boundary.");
                }

                var description = RedactText(tool.Description, ref redactionCount);
                var schema = RequireSafeJsonContract(tool.InputSchemaJson);
                if (!description.IsSuccess || !schema.IsSuccess)
                {
                    return DomainResult.Fail<PreparedModelContext>(
                        description.Failure ?? schema.Failure!);
                }

                tools.Add(tool with
                {
                    Description = description.Value,
                    InputSchemaJson = schema.Value,
                });
            }

            string? responseSchema = null;
            if (request.ResponseFormat.JsonSchema is not null)
            {
                var schema = RequireSafeJsonContract(request.ResponseFormat.JsonSchema);
                if (!schema.IsSuccess)
                {
                    return DomainResult.Fail<PreparedModelContext>(schema.Failure!);
                }

                responseSchema = schema.Value;
            }

            var preparedRequest = request with
            {
                Messages = new ReadOnlyCollection<ModelMessage>(messages),
                Tools = new ReadOnlyCollection<ModelToolDefinition>(tools),
                ResponseFormat = request.ResponseFormat with { JsonSchema = responseSchema },
                Limits = request.Limits with { },
            };
            return DomainResult.Success(new PreparedModelContext(preparedRequest, redactionCount, PolicyName));
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException or OverflowException)
        {
            return Invalid("Model context could not be safely prepared within the configured bounds.");
        }
    }

    private DomainResult<ModelMessage> PrepareMessage(ModelMessage message, ref int redactionCount)
    {
        if (message is null || message.Content is null ||
            message.Name is not null && !IsSafeIdentity(message.Name))
        {
            return DomainResult.Fail<ModelMessage>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model message identity contains sensitive or missing content."));
        }

        var content = new List<ModelContent>(message.Content.Count);
        foreach (var part in message.Content)
        {
            switch (part)
            {
                case ModelTextContent text:
                    {
                        var prepared = RedactText(text.Text, ref redactionCount);
                        if (!prepared.IsSuccess)
                        {
                            return DomainResult.Fail<ModelMessage>(prepared.Failure!);
                        }

                        content.Add(text with { Text = prepared.Value });
                        break;
                    }
                case ModelAttachmentContent attachment when attachment.Attachment is not null:
                    {
                        var fileName = attachment.Attachment.FileName;
                        if (fileName is not null)
                        {
                            var prepared = RedactText(fileName, ref redactionCount);
                            if (!prepared.IsSuccess)
                            {
                                return DomainResult.Fail<ModelMessage>(prepared.Failure!);
                            }

                            fileName = prepared.Value;
                        }

                        content.Add(new ModelAttachmentContent(attachment.Attachment with { FileName = fileName }));
                        break;
                    }
                case ModelToolCallContent toolCall:
                    {
                        if (!IsSafeIdentity(toolCall.ToolCallId) || !IsSafeIdentity(toolCall.ToolName))
                        {
                            return DomainResult.Fail<ModelMessage>(new DomainFailure(
                                FailureCode.ValidationFailure,
                                "Model tool-call identity contains sensitive content."));
                        }

                        var arguments = RedactJson(toolCall.ArgumentsJson, ref redactionCount);
                        if (!arguments.IsSuccess)
                        {
                            return DomainResult.Fail<ModelMessage>(arguments.Failure!);
                        }

                        content.Add(toolCall with { ArgumentsJson = arguments.Value });
                        break;
                    }
                case ModelToolResultContent toolResult:
                    {
                        if (!IsSafeIdentity(toolResult.ToolCallId) || !IsSafeIdentity(toolResult.ToolName))
                        {
                            return DomainResult.Fail<ModelMessage>(new DomainFailure(
                                FailureCode.ValidationFailure,
                                "Model tool-result identity contains sensitive content."));
                        }

                        var result = RedactJson(toolResult.ResultJson, ref redactionCount);
                        if (!result.IsSuccess)
                        {
                            return DomainResult.Fail<ModelMessage>(result.Failure!);
                        }

                        content.Add(toolResult with { ResultJson = result.Value });
                        break;
                    }
                default:
                    return DomainResult.Fail<ModelMessage>(new DomainFailure(
                        FailureCode.ValidationFailure,
                        "Model message contains an unsupported content kind."));
            }
        }

        return DomainResult.Success(message with
        {
            Content = new ReadOnlyCollection<ModelContent>(content),
        });
    }

    private DomainResult<string> RedactText(string value, ref int redactionCount)
    {
        if (value is null)
        {
            return DomainResult.Fail<string>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model context text is missing."));
        }

        var result = redactor.Redact(value);
        redactionCount = checked(redactionCount + result.RedactionCount);
        using var document = JsonDocument.Parse(result.Data.Json);
        return document.RootElement.ValueKind is JsonValueKind.String
            ? DomainResult.Success(document.RootElement.GetString()!)
            : DomainResult.Fail<string>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model context text did not remain a string after preparation."));
    }

    private DomainResult<string> RedactJson(string value, ref int redactionCount)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions
        {
            MaxDepth = 64,
        });
        var result = redactor.Redact(document.RootElement);
        redactionCount = checked(redactionCount + result.RedactionCount);
        return DomainResult.Success(result.Data.Json);
    }

    private DomainResult<string> RequireSafeJsonContract(string value)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions
        {
            MaxDepth = 64,
        });
        var result = redactor.Redact(document.RootElement);
        return result.ContainsRedactions
            ? DomainResult.Fail<string>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Model JSON contract contains sensitive content and cannot cross the provider boundary."))
            : DomainResult.Success(result.Data.Json);
    }

    private bool IsSafeIdentity(string value)
    {
        if (value is null)
        {
            return false;
        }

        return !redactor.Redact(value).ContainsRedactions;
    }

    private static DomainResult<PreparedModelContext> Invalid(string message) =>
        DomainResult.Fail<PreparedModelContext>(new DomainFailure(FailureCode.ValidationFailure, message));
}
