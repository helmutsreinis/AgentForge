using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace AgentForge.Domain.Devices;

public readonly record struct PhysicalDeviceId(string Value)
{
    public override string ToString() => Value;
}

public enum SerialDeviceReadiness
{
    Ready,
    PermissionRequired,
    DriverUnavailable,
    Detached,
    Unknown,
}

public enum DeviceCapability
{
    Inventory,
    Capture,
    Read,
    Write,
    Command,
    Calibration,
    Firmware,
    Privileged,
}

public enum SerialParityMode { None, Odd, Even, Mark, Space }
public enum SerialStopBitsMode { One, OnePointFive, Two }
public enum SerialFlowControl { None, Software, Hardware }

public sealed record SerialProfile(
    int BaudRate,
    int DataBits,
    SerialParityMode Parity,
    SerialStopBitsMode StopBits,
    SerialFlowControl FlowControl,
    bool DtrEnable,
    bool RtsEnable,
    int ReadTimeoutMilliseconds,
    int WriteTimeoutMilliseconds)
{
    public static SerialProfile ConservativeDefault { get; } =
        new(9_600, 8, SerialParityMode.None, SerialStopBitsMode.One,
            SerialFlowControl.None, false, false, 1_000, 1_000);

    public bool IsValid() =>
        BaudRate is >= 50 and <= 12_000_000 &&
        DataBits is >= 5 and <= 9 &&
        ReadTimeoutMilliseconds is >= 1 and <= 60_000 &&
        WriteTimeoutMilliseconds is >= 1 and <= 60_000;
}

public sealed record SerialDeviceDescriptor(
    PhysicalDeviceId PhysicalId,
    string Endpoint,
    string Platform,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string IdentityEvidence,
    SerialDeviceReadiness Readiness,
    string? ReadinessReason,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash)
{
    public bool IsValid() =>
        SerialDeviceRecordValidator.IsPhysicalId(PhysicalId) &&
        SerialDeviceRecordValidator.Text(Endpoint, 1024) &&
        SerialDeviceRecordValidator.Text(Platform, 32) &&
        SerialDeviceRecordValidator.Text(IdentityEvidence, 2048) &&
        SerialDeviceRecordValidator.Optional(VendorId, 16) &&
        SerialDeviceRecordValidator.Optional(ProductId, 16) &&
        SerialDeviceRecordValidator.Optional(SerialNumber, 256) &&
        SerialDeviceRecordValidator.Optional(ReadinessReason, 1024) &&
        SerialDeviceRecordValidator.IsSha256(EvidenceHash);
}

public sealed record SerialInventorySnapshot(
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<SerialDeviceDescriptor> Devices,
    string SnapshotHash)
{
    public bool IsValid() => Devices.Length <= 4096 &&
        Devices.All(device => device.IsValid()) &&
        Devices.Select(device => device.Endpoint).Distinct(StringComparer.Ordinal).Count() == Devices.Length &&
        SerialDeviceRecordValidator.IsSha256(SnapshotHash);
}

public enum SerialInventoryChangeKind { Attached, Detached, Reenumerated, ReadinessChanged }

public sealed record SerialInventoryChange(
    SerialInventoryChangeKind Kind,
    PhysicalDeviceId PhysicalId,
    string? PreviousEndpoint,
    string? CurrentEndpoint,
    SerialDeviceReadiness PreviousReadiness,
    SerialDeviceReadiness CurrentReadiness,
    DateTimeOffset ObservedAtUtc);

public sealed record DeviceCapabilityGrant(
    PhysicalDeviceId PhysicalId,
    ImmutableSortedSet<DeviceCapability> Capabilities,
    DateTimeOffset ExpiresAtUtc,
    string EvidenceHash)
{
    public bool Allows(DeviceCapability capability, DateTimeOffset now) =>
        SerialDeviceRecordValidator.IsPhysicalId(PhysicalId) &&
        SerialDeviceRecordValidator.IsSha256(EvidenceHash) &&
        ExpiresAtUtc > now && Capabilities.Contains(capability);
}

public static class SerialDeviceRecordValidator
{
    public static PhysicalDeviceId PhysicalIdFromEvidence(string platform, string identityEvidence)
    {
        if (!Text(platform, 32) || !Text(identityEvidence, 2048))
            throw new ArgumentException("Stable serial identity evidence is invalid.", nameof(identityEvidence));
        var bytes = Encoding.UTF8.GetBytes($"{platform.Trim().ToLowerInvariant()}\n{identityEvidence.Trim()}");
        return new PhysicalDeviceId($"serial:sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}");
    }

    public static bool IsPhysicalId(PhysicalDeviceId id) =>
        id.Value is { Length: 78 } && id.Value.StartsWith("serial:sha256:", StringComparison.Ordinal) &&
        id.Value[14..].All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    public static bool IsSha256(string value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    public static bool Text(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    public static bool Optional(string? value, int max) => value is null ||
        value.Length <= max && !value.Any(character => character == '\0');
}
