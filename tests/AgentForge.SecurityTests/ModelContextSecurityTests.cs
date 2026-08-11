using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Models;
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.SecurityTests;

public sealed class ModelContextSecurityTests
{
    [Fact]
    public void Prepares_an_immutable_snapshot_and_redacts_every_payload_location()
    {
        const string bearer = "Bearer abcdefghijklmnopqrstuvwxyz";
        const string providerKey = "sk-" + "1234567890abcdefghijklmnop";
        var request = Request() with
        {
            Messages =
            [
                new ModelMessage(ModelMessageRole.User,
                [
                    new ModelTextContent(bearer),
                    new ModelAttachmentContent(new ModelAttachmentReference(
                        new string('a', 64),
                        "image/png",
                        32,
                        ModelAttachmentModality.Image,
                        providerKey)),
                ]),
                new ModelMessage(ModelMessageRole.Assistant,
                [
                    new ModelToolCallContent("call-1", "read_file", "{\"path\":\"safe\",\"token\":\"hidden\"}"),
                ]),
                new ModelMessage(ModelMessageRole.Tool,
                [
                    new ModelToolResultContent("call-1", "read_file", "{\"password\":\"hidden\",\"status\":\"ok\"}", false),
                ]),
            ],
            Tools =
            [
                new ModelToolDefinition(
                    "read_file",
                    providerKey,
                    "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
            ],
            ResponseFormat = new ModelResponseFormat(
                ModelResponseFormatKind.JsonSchema,
                "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}}}"),
        };
        var preparer = CreatePreparer();

        var result = preparer.Prepare(request);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(5, result.Value.RedactionCount);
        Assert.Equal(ModelContextPreparer.PolicyName, result.Value.Policy);
        Assert.NotSame(request, result.Value.Request);
        Assert.NotSame(request.Messages, result.Value.Request.Messages);
        Assert.Equal("[REDACTED]", Assert.IsType<ModelTextContent>(result.Value.Request.Messages[0].Content[0]).Text);
        Assert.Equal(
            "[REDACTED]",
            Assert.IsType<ModelAttachmentContent>(result.Value.Request.Messages[0].Content[1]).Attachment.FileName);
        Assert.Equal("[REDACTED]", result.Value.Request.Tools[0].Description);
        Assert.Equal(bearer, Assert.IsType<ModelTextContent>(request.Messages[0].Content[0]).Text);
        Assert.Equal(providerKey, request.Tools[0].Description);

        using var arguments = JsonDocument.Parse(
            Assert.IsType<ModelToolCallContent>(result.Value.Request.Messages[1].Content[0]).ArgumentsJson);
        Assert.Equal("[REDACTED]", arguments.RootElement.GetProperty("token").GetString());
        Assert.Equal("safe", arguments.RootElement.GetProperty("path").GetString());
        using var toolResult = JsonDocument.Parse(
            Assert.IsType<ModelToolResultContent>(result.Value.Request.Messages[2].Content[0]).ResultJson);
        Assert.Equal("[REDACTED]", toolResult.RootElement.GetProperty("password").GetString());
        Assert.Equal("ok", toolResult.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Sensitive_identity_and_contract_content_fail_closed_without_echoing_input()
    {
        const string sensitive = "Bearer identity-material-123456";
        var preparer = CreatePreparer();

        var identity = preparer.Prepare(Request() with
        {
            CorrelationId = new CorrelationId(sensitive),
        });
        var contract = preparer.Prepare(Request() with
        {
            Tools =
            [
                new ModelToolDefinition(
                    "credential_tool",
                    "A deliberately hostile schema fixture.",
                    "{\"type\":\"object\",\"properties\":{\"token\":{\"type\":\"string\"}}}"),
            ],
        });

        Assert.False(identity.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, identity.Failure?.Code);
        Assert.DoesNotContain(sensitive, identity.Failure?.Message, StringComparison.Ordinal);
        Assert.False(contract.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, contract.Failure?.Code);
        Assert.DoesNotContain("token", contract.Failure?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redaction_bounds_return_a_typed_failure_without_copying_payload_text()
    {
        var oversized = new string('q', 2048);
        var preparer = CreatePreparer(new Dictionary<string, string?>
        {
            ["AgentForge:Security:MaximumRedactionPayloadBytes"] = "1024",
        });
        var request = Request() with
        {
            Messages = [new ModelMessage(ModelMessageRole.User, [new ModelTextContent(oversized)])],
        };

        var result = preparer.Prepare(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        Assert.DoesNotContain(new string('q', 64), result.Failure?.Message, StringComparison.Ordinal);
    }

    private static IModelContextPreparer CreatePreparer(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddAgentForgeSecurity(configuration);
        services.AddAgentForgeModels();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        return provider.GetRequiredService<IModelContextPreparer>();
    }

    private static ModelRequest Request() => new(
        new ModelRequestId(Guid.Parse("ad7ca089-7c18-48cb-8204-376564d0e633")),
        "security-model",
        [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("safe input")])],
        [],
        new ModelResponseFormat(ModelResponseFormatKind.Text),
        new ModelInvocationLimits(100, 1, 32, 30),
        0,
        1,
        42,
        new CorrelationId("model-context-security"));
}
