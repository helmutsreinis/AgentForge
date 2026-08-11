using AgentForge.Domain.Models;

namespace AgentForge.Models;

public sealed record AnthropicModelProviderOptions(
    Uri MessagesEndpoint,
    string ApiVersion = "2023-06-01",
    int MaximumEventBytes = 1_048_576,
    long MaximumResponseBytes = 16_777_216,
    int MaximumRequestBytes = 8_388_608,
    ModelProviderDataLocation DestinationDataLocation = ModelProviderDataLocation.Cloud);
