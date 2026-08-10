using AgentForge.Setup;

namespace AgentForge.CrossPlatformTests;

public sealed class InstallationPathResolverTests
{
    [Fact]
    public void Windows_default_uses_local_application_data()
    {
        var path = InstallationPathResolver.ResolveDefaultDataDirectory(
            isWindows: true,
            localApplicationData: Path.Combine(Path.GetTempPath(), "LocalAppData"),
            xdgDataHome: null,
            userProfile: null);

        Assert.EndsWith(Path.Combine("LocalAppData", "AgentForge"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_default_prefers_xdg_data_home()
    {
        var path = InstallationPathResolver.ResolveDefaultDataDirectory(
            isWindows: false,
            localApplicationData: null,
            xdgDataHome: Path.Combine(Path.GetTempPath(), "xdg"),
            userProfile: Path.Combine(Path.GetTempPath(), "home"));

        Assert.EndsWith(Path.Combine("xdg", "agentforge"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_default_falls_back_to_dot_local_share()
    {
        var path = InstallationPathResolver.ResolveDefaultDataDirectory(
            isWindows: false,
            localApplicationData: null,
            xdgDataHome: null,
            userProfile: Path.Combine(Path.GetTempPath(), "home"));

        Assert.EndsWith(Path.Combine("home", ".local", "share", "agentforge"), path, StringComparison.Ordinal);
    }
}
