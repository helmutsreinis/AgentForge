using AgentForge.Abstractions.Security;
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.SecurityTests;

public sealed class StructuredSensitiveDataRedactorTests
{
    [Fact]
    public void Redacts_sensitive_names_and_secret_shaped_values_recursively()
    {
        const string password = "correct horse battery staple";
        const string bearer = "Bearer abcdefghijklmnopqrstuvwxyz";
        const string providerKey = "sk-" + "1234567890abcdefghijklmnop";
        var redactor = CreateRedactor();

        var result = redactor.Redact(new
        {
            User = "operator",
            DatabasePassword = password,
            Nested = new Dictionary<string, object?>
            {
                ["authorization"] = bearer,
                ["notes"] = new[] { "safe", providerKey },
            },
        });

        Assert.Equal(3, result.RedactionCount);
        Assert.DoesNotContain(password, result.Data.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(bearer, result.Data.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(providerKey, result.Data.Json, StringComparison.Ordinal);
        Assert.Contains("operator", result.Data.Json, StringComparison.Ordinal);
        Assert.Equal(3, result.Data.Json.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Produces_canonical_object_property_order()
    {
        var redactor = CreateRedactor();

        var result = redactor.Redact(new Dictionary<string, object?>
        {
            ["zeta"] = 3,
            ["alpha"] = 1,
            ["middle"] = true,
        });

        Assert.Equal("{\"alpha\":1,\"middle\":true,\"zeta\":3}", result.Data.Json);
        Assert.False(result.ContainsRedactions);
    }

    [Fact]
    public void Rejects_payloads_over_the_configured_bound()
    {
        var redactor = CreateRedactor(new Dictionary<string, string?>
        {
            ["AgentForge:Security:MaximumRedactionPayloadBytes"] = "1024",
        });

        var exception = Assert.Throws<ArgumentException>(() => redactor.Redact(new string('x', 2048)));
        Assert.DoesNotContain(new string('x', 128), exception.Message, StringComparison.Ordinal);
    }

    private static ISensitiveDataRedactor CreateRedactor(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddAgentForgeSecurity(configuration);
        return services.BuildServiceProvider(validateScopes: true)
            .GetRequiredService<ISensitiveDataRedactor>();
    }
}
