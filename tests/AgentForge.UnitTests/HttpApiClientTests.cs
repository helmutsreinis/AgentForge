using System.Net;
using System.Text;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.HttpApi;

namespace AgentForge.UnitTests;

public sealed class HttpApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_binds_bearer_origin_path_query_and_dynamic_header_templates()
    {
        const string token = "user-provided-bearer";
        var correlationId = Guid.NewGuid().ToString("D");
        var requestId = Guid.NewGuid().ToString("D");
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json"),
        });
        using var client = HttpApiClient.CreateForTesting(handler, new FixedClock());

        var result = await client.GetAsync(Profile(), token.AsMemory(), new HttpApiReadRequest(
            "customers", new Dictionary<string, string> { ["size"] = "25" }, 16_384,
            correlationId, requestId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal("https://api.partnercenter.microsoft.com/v1/customers?size=25", handler.RequestUri?.AbsoluteUri);
        Assert.Equal($"Bearer {token}", handler.Authorization);
        Assert.Equal("v1", handler.Headers["MS-Contract-Version"]);
        Assert.Equal(correlationId, handler.Headers["MS-CorrelationId"]);
        Assert.Equal(requestId, handler.Headers["MS-RequestId"]);
        Assert.Equal("{\"items\":[]}", result.Value.Body);
        Assert.DoesNotContain(token, result.Value.EvidenceHash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../customers")]
    [InlineData("%2e%2e/customers")]
    [InlineData("https://evil.example/customers")]
    [InlineData("//evil.example/customers")]
    public async Task Get_rejects_any_path_that_can_escape_the_configured_base(string relativePath)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = HttpApiClient.CreateForTesting(handler, new FixedClock());

        var result = await client.GetAsync(Profile(), "token".AsMemory(), new HttpApiReadRequest(
            relativePath, new Dictionary<string, string>(), 1024,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("X-Auth-Token")]
    public async Task Get_rejects_forbidden_or_secret_profile_headers_before_materializing_a_request(
        string headerName)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = HttpApiClient.CreateForTesting(handler, new FixedClock());
        var profile = Profile() with
        {
            StaticHeaders = new Dictionary<string, string> { [headerName] = "embedded-secret" },
        };

        var result = await client.GetAsync(profile, "token".AsMemory(), new HttpApiReadRequest(
            "customers", new Dictionary<string, string>(), 1024,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task Get_fails_closed_when_the_response_exceeds_the_approved_byte_limit()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[2048]),
        });
        using var client = HttpApiClient.CreateForTesting(handler, new FixedClock());

        var result = await client.GetAsync(Profile(), "token".AsMemory(), new HttpApiReadRequest(
            "customers", new Dictionary<string, string>(), 1024,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.RecoverableExternalFailure, result.Failure?.Code);
    }

    private static HttpApiProfile Profile() => new(
        new InstallationId(Guid.NewGuid()),
        new HttpApiProfileId("microsoft-partner-center"),
        "Microsoft Partner Center",
        new Uri("https://api.partnercenter.microsoft.com/v1/"),
        "customers?size=1",
        new Dictionary<string, string>
        {
            ["MS-Contract-Version"] = "v1",
            ["MS-CorrelationId"] = "{correlationId}",
            ["MS-RequestId"] = "{requestId}",
        },
        new SecretReference("test", "partner-center-bearer"),
        true,
        0,
        Now,
        Now,
        new ActorId("operator"),
        new CorrelationId("http-api-test"));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            foreach (var header in request.Headers) Headers[header.Key] = header.Value.Single();
            return Task.FromResult(response);
        }
    }
}
