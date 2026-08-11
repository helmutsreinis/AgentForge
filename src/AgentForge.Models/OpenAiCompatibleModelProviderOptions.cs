namespace AgentForge.Models;

public sealed record OpenAiCompatibleModelProviderOptions(
    Uri ChatCompletionsEndpoint,
    bool AllowInsecureHttp = false,
    bool IncludeUsageInStream = true,
    bool DisableThinking = false,
    int MaximumEventBytes = 1_048_576,
    long MaximumResponseBytes = 16_777_216,
    int MaximumRequestBytes = 8_388_608);
