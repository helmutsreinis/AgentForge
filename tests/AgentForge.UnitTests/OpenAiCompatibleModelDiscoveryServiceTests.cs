using System.Net;
using System.Text;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class OpenAiCompatibleModelDiscoveryServiceTests
{
    [Fact]
    public async Task Discovery_returns_bounded_sorted_catalog_without_requiring_local_credentials()
    {
        RecordingHandler? observed = null;
        var service = new OpenAiCompatibleModelDiscoveryService((_, _) =>
        {
            observed = new RecordingHandler("""
                {"object":"list","data":[
                  {"id":"qwen3.6","owned_by":"vllm","max_model_len":131072},
                  {"id":"aeon-fast","owned_by":"vllm"}
                ]}
                """);
            return observed;
        });

        var result = await service.DiscoverAsync(new ModelCatalogDiscoveryRequest(
            new Uri("http://127.0.0.1:8000/v1"),
            "openai-compatible",
            ReadOnlyMemory<char>.Empty), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(["aeon-fast", "qwen3.6"], result.Value.Models.Select(model => model.Id));
        Assert.Equal(131_072, result.Value.Models[1].MaximumContextTokens);
        Assert.Equal(new Uri("http://127.0.0.1:8000/v1/models"), observed!.RequestUri);
        Assert.Null(observed.Authorization);
    }

    [Fact]
    public async Task Probe_uses_selected_model_and_bounded_compatible_request()
    {
        RecordingHandler? observed = null;
        var service = new OpenAiCompatibleModelDiscoveryService((_, _) =>
        {
            observed = new RecordingHandler("""{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""");
            return observed;
        });

        var result = await service.ProbeAsync(new ModelConnectionProbeRequest(
            new Uri("http://192.168.1.89:8000/v1"),
            "vllm",
            "qwen3.6",
            "test-key".ToCharArray()), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal("qwen3.6", result.Value.Model);
        Assert.Equal(new Uri("http://192.168.1.89:8000/v1/chat/completions"), observed!.RequestUri);
        Assert.Equal("Bearer test-key", observed.Authorization);
        Assert.Contains("\"model\":\"qwen3.6\"", observed.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", observed.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"enable_thinking\":false", observed.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_rejects_a_choice_without_visible_response_text()
    {
        var service = new OpenAiCompatibleModelDiscoveryService((_, _) =>
            new RecordingHandler("""{"choices":[{"message":{"role":"assistant","content":""}}]}"""));

        var result = await service.ProbeAsync(new ModelConnectionProbeRequest(
            new Uri("http://127.0.0.1:8000/v1"),
            "openai-compatible",
            "qwen3.8",
            ReadOnlyMemory<char>.Empty), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    [Fact]
    public async Task Discovery_rejects_public_plaintext_endpoint_before_transport()
    {
        var handlerCreated = false;
        var service = new OpenAiCompatibleModelDiscoveryService((_, _) =>
        {
            handlerCreated = true;
            return new RecordingHandler("{}");
        });

        var result = await service.DiscoverAsync(new ModelCatalogDiscoveryRequest(
            new Uri("http://example.com/v1"),
            "openai-compatible",
            ReadOnlyMemory<char>.Empty), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        Assert.False(handlerCreated);
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
