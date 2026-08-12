using System.Runtime.Versioning;
using AgentForge.Abstractions.Devices;
using AgentForge.Domain.Devices;
using Microsoft.Win32;

namespace AgentForge.Devices;

internal sealed class SystemPassiveSerialInventorySource : IPassiveSerialInventorySource
{
    public ValueTask<IReadOnlyList<PassiveSerialCandidate>> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PassiveSerialCandidate> result = OperatingSystem.IsWindows() ? InspectWindows() : InspectUnix();
        return ValueTask.FromResult(result);
    }

    [SupportedOSPlatform("windows")]
    private static List<PassiveSerialCandidate> InspectWindows()
    {
        var devices = new List<PassiveSerialCandidate>();
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM", false);
        if (key is null) return devices;
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            if (key.GetValue(name) is not string endpoint || string.IsNullOrWhiteSpace(endpoint)) continue;
            devices.Add(new(endpoint.Trim(), "windows", $"registry:{name.Trim()}", null, null, null,
                SerialDeviceReadiness.Unknown, "Passive registry inventory cannot prove driver readiness."));
        }
        return devices;
    }

    private static List<PassiveSerialCandidate> InspectUnix()
    {
        const string sysClassTty = "/sys/class/tty";
        if (!Directory.Exists(sysClassTty)) return [];
        var devices = new List<PassiveSerialCandidate>();
        foreach (var path in Directory.EnumerateDirectories(sysClassTty).Order(StringComparer.Ordinal).Take(4097))
        {
            var name = Path.GetFileName(path);
            if (!IsCandidateName(name)) continue;
            var endpoint = $"/dev/{name}";
            var deviceLink = Path.Combine(path, "device");
            var vendor = ReadText(path, "device", "idVendor");
            var product = ReadText(path, "device", "idProduct");
            var serial = ReadText(path, "device", "serial");
            var resolvedDevice = ResolveLink(deviceLink);
            var evidence = serial is not null
                ? $"usb:{vendor ?? "unknown"}:{product ?? "unknown"}:{serial}"
                : resolvedDevice is not null ? $"sysfs:{resolvedDevice}" : $"tty:{name}";
            var readiness = File.Exists(endpoint) ? SerialDeviceReadiness.Ready : SerialDeviceReadiness.DriverUnavailable;
            devices.Add(new(endpoint, "linux", evidence, vendor, product, serial, readiness,
                readiness == SerialDeviceReadiness.Ready ? null : "The passive device node is absent."));
        }
        return devices;
    }

    private static bool IsCandidateName(string name) =>
        name.StartsWith("ttyUSB", StringComparison.Ordinal) ||
        name.StartsWith("ttyACM", StringComparison.Ordinal) ||
        name.StartsWith("ttyS", StringComparison.Ordinal) ||
        name.StartsWith("ttyAMA", StringComparison.Ordinal) ||
        name.StartsWith("rfcomm", StringComparison.Ordinal);

    private static string? ReadText(params string[] segments)
    {
        try
        {
            var path = Path.Combine(segments);
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return value.Length is > 0 and <= 256 ? value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ResolveLink(string path)
    {
        try
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(true);
            return target?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
