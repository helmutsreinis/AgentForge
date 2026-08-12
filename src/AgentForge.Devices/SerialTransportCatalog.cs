using AgentForge.Abstractions.Devices;

namespace AgentForge.Devices;

public sealed class SerialTransportCatalog(IEnumerable<ISerialTransportAdapter> adapters) : ISerialTransportCatalog
{
    private readonly ISerialTransportAdapter[] _adapters = adapters.OrderBy(item => item.AdapterId, StringComparer.Ordinal).ToArray();

    public ISerialTransportAdapter? Resolve(string platform) =>
        _adapters.FirstOrDefault(adapter => adapter.Supports(platform));
}
