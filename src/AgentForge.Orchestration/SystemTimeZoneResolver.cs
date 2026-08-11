using AgentForge.Abstractions.Scheduling;
using AgentForge.Domain.Primitives;

namespace AgentForge.Orchestration;

internal sealed class SystemTimeZoneResolver : ITimeZoneResolver
{
    public DomainResult<TimeZoneInfo> Resolve(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128 || timeZoneId.Any(char.IsControl))
        {
            return Failure();
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return string.Equals(timeZone.Id, timeZoneId, StringComparison.Ordinal)
                ? DomainResult.Success(timeZone)
                : Failure();
        }
        catch (TimeZoneNotFoundException)
        {
            return Failure();
        }
        catch (InvalidTimeZoneException)
        {
            return Failure();
        }
    }

    private static DomainResult<TimeZoneInfo> Failure() =>
        DomainResult.Fail<TimeZoneInfo>(new DomainFailure(
            FailureCode.UnsupportedCapability,
            "The exact configured timezone is unavailable on this host."));
}
