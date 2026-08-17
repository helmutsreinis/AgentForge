using Microsoft.Extensions.Options;

namespace AgentForge.Host.Http;

public sealed class HostSecurityOptions
{
    public const string SectionName = "AgentForge:Host";

    public string Urls { get; init; } = "http://127.0.0.1:5047";

    public bool RemoteEnabled { get; init; }

    public string[] AllowedOrigins { get; init; } = [];

    public string RemoteAccessCode { get; init; } = string.Empty;

    public int RequestsPerMinute { get; init; } = 120;

    public long MaximumRequestBodyBytes { get; init; } = 262_144;
}

internal sealed class HostSecurityOptionsValidator : IValidateOptions<HostSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, HostSecurityOptions options)
    {
        if (options.RequestsPerMinute is < 10 or > 10_000 ||
            options.MaximumRequestBodyBytes is < 1_024 or > 1_048_576)
            return ValidateOptionsResult.Fail("Host request rate and body bounds are invalid.");
        var urls = options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var value in urls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
                return ValidateOptionsResult.Fail("Every host URL must be absolute and contain no user information.");
            if (IsLoopback(uri)) continue;
            if (!options.RemoteEnabled || uri.Scheme != Uri.UriSchemeHttps)
                return ValidateOptionsResult.Fail("Non-loopback binding requires explicit remote mode and HTTPS.");
        }

        if (options.RemoteEnabled && (options.AllowedOrigins.Length == 0 ||
            options.RemoteAccessCode.Length is < 20 or > 256 ||
            options.RemoteAccessCode.Any(char.IsControl)))
            return ValidateOptionsResult.Fail(
                "Remote mode requires at least one exact HTTPS origin and a 20-256 character access code.");
        if (options.AllowedOrigins.Length > 64 || options.AllowedOrigins.Distinct(StringComparer.Ordinal).Count() !=
            options.AllowedOrigins.Length || options.AllowedOrigins.Any(value => !IsOrigin(value)))
            return ValidateOptionsResult.Fail("Allowed origins must be distinct exact HTTPS origins without paths.");
        return ValidateOptionsResult.Success;
    }

    internal static bool IsLoopback(Uri uri) => uri.IsLoopback ||
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" &&
        string.IsNullOrEmpty(uri.Fragment) && value.IndexOf('*', StringComparison.Ordinal) < 0;
}
