using AgentForge.Abstractions.Installations;
using Microsoft.Extensions.Options;

namespace AgentForge.Setup;

public sealed class DefaultDataDirectoryProvider(IOptions<InstallationOptions> options) : IDataDirectoryProvider
{
    public string GetDataDirectory() => InstallationPathResolver.ResolveConfiguredDataDirectory(options.Value);
}
