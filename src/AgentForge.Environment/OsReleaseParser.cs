using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;

namespace AgentForge.Environment;

public static class OsReleaseParser
{
    private const int MaximumLength = 65_536;

    public static DomainResult<DistributionProfile> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaximumLength)
        {
            return Invalid("os-release content is empty or exceeds 64 KiB.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 1)
            {
                return Invalid("os-release contains a malformed assignment.");
            }

            var key = line[..separator];
            if (key.Any(character => !(char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_')))
            {
                return Invalid("os-release contains an invalid key.");
            }

            var valueResult = DecodeValue(line[(separator + 1)..]);
            if (!valueResult.IsSuccess)
            {
                return DomainResult.Fail<DistributionProfile>(valueResult.Failure!);
            }

            values[key] = valueResult.Value;
        }

        var id = values.GetValueOrDefault("ID")?.Trim();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            return Invalid("os-release ID is required and bounded.");
        }

        return DomainResult.Success(new DistributionProfile(
            id.ToLowerInvariant(),
            Normalize(values.GetValueOrDefault("ID_LIKE")),
            Normalize(values.GetValueOrDefault("VERSION_ID")),
            Normalize(values.GetValueOrDefault("VERSION_CODENAME")),
            Normalize(values.GetValueOrDefault("PRETTY_NAME")),
            string.Equals(id, "kali", StringComparison.OrdinalIgnoreCase)));
    }

    private static DomainResult<string> DecodeValue(string raw)
    {
        if (raw.Length == 0)
        {
            return DomainResult.Success(string.Empty);
        }

        string value;
        if (raw[0] is '\'' or '"')
        {
            var quote = raw[0];
            if (raw.Length < 2 || raw[^1] != quote)
            {
                return DomainResult.Fail<string>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    "os-release contains an unterminated quoted value."));
            }

            value = raw[1..^1];
            if (quote == '"')
            {
                value = value
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal)
                    .Replace("\\$", "$", StringComparison.Ordinal)
                    .Replace("\\`", "`", StringComparison.Ordinal);
            }
        }
        else
        {
            value = raw.Trim();
        }

        if (value.Length > 4096 || value.Any(character => char.IsControl(character) && character is not '\t'))
        {
            return DomainResult.Fail<string>(new DomainFailure(
                FailureCode.ValidationFailure,
                "os-release contains an invalid or oversized value."));
        }

        return DomainResult.Success(value);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DomainResult<DistributionProfile> Invalid(string message) =>
        DomainResult.Fail<DistributionProfile>(new DomainFailure(FailureCode.ValidationFailure, message));
}
