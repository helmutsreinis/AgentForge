using System.Collections.Immutable;
using AgentForge.Abstractions.Channels;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Primitives;

namespace AgentForge.Channels;

public sealed class ChannelAdapterCatalog : IChannelAdapterCatalog
{
    private readonly ImmutableDictionary<string, IChannelAdapter> _adapters;

    public ChannelAdapterCatalog(IEnumerable<IChannelAdapter> adapters)
    {
        _adapters = adapters.ToImmutableDictionary(
            item => Key(item.Kind, item.AccountId), StringComparer.Ordinal);
    }

    public DomainResult<IChannelAdapter> Resolve(ChannelKind kind, string accountId) =>
        _adapters.TryGetValue(Key(kind, accountId), out var adapter)
            ? DomainResult.Success(adapter)
            : DomainResult.Fail<IChannelAdapter>(new DomainFailure(
                FailureCode.UnsupportedCapability, "The exact channel adapter is unavailable."));

    private static string Key(ChannelKind kind, string accountId) => $"{kind}:{accountId.Trim()}";
}
