namespace AgentForge.Setup;

public static class InstallationPathResolver
{
    public static string ResolveDefaultDataDirectory(
        bool isWindows,
        string? localApplicationData,
        string? xdgDataHome,
        string? userProfile)
    {
        if (isWindows)
        {
            var basePath = RequirePath(localApplicationData, nameof(localApplicationData));
            return Path.GetFullPath(Path.Combine(basePath, "AgentForge"));
        }

        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return Path.GetFullPath(Path.Combine(xdgDataHome, "agentforge"));
        }

        var home = RequirePath(userProfile, nameof(userProfile));
        return Path.GetFullPath(Path.Combine(home, ".local", "share", "agentforge"));
    }

    public static string ResolveConfiguredDataDirectory(InstallationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DataDirectory));
        }

        return ResolveDefaultDataDirectory(
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private static string RequirePath(string? value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Unable to resolve a data directory because {parameterName} is unavailable.");
}
