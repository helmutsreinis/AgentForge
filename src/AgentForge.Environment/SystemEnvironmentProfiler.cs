using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using AgentForge.Abstractions.Environments;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AgentForge.Environment;

internal sealed class SystemEnvironmentProfiler(
    IClock clock,
    IOptions<EnvironmentInventoryOptions> options) : IEnvironmentProfiler
{
    private static readonly string[] WindowsExecutableExtensions = [".exe", ".com", ".cmd", ".bat"];
    private static readonly string[] ContainerMarkers = ["docker", "kubepods", "containerd", "lxc"];
    private static readonly string[] LinuxSystemExecutableRoots = ["/bin", "/sbin", "/usr/bin", "/usr/sbin"];
    private static readonly string[] VirtualizationMarkers =
        ["virtual", "vmware", "virtualbox", "kvm", "qemu", "hyper-v", "xen", "parallels"];
    private readonly EnvironmentInventoryOptions _options = options.Value;

    public Task<DomainResult<EnvironmentProfile>> CaptureAsync(
        CaptureEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var operatingSystem = CaptureOperatingSystem();
            var wsl = CaptureWsl(operatingSystem);
            var isolation = CaptureIsolation(operatingSystem, wsl);
            var executables = CaptureExecutables(operatingSystem.Family, cancellationToken);
            var observation = new EnvironmentObservation(
                operatingSystem,
                RuntimeInformation.FrameworkDescription,
                global::System.Environment.ProcessorCount,
                wsl,
                isolation,
                CaptureFileSystem(operatingSystem.Family),
                CapturePrivilege(operatingSystem.Family),
                CaptureShells(operatingSystem.Family, executables.Items),
                CapturePackageDatabases(operatingSystem.Family),
                CaptureNetwork(),
                CaptureManagers(operatingSystem.Family, executables.Items),
                CaptureAccelerators(operatingSystem.Family),
                executables.Items,
                executables.Truncated);
            return Task.FromResult(EnvironmentProfileBuilder.Build(
                observation,
                clock.UtcNow,
                request.ActorId,
                request.CorrelationId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(DomainResult.Fail<EnvironmentProfile>(new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "Passive environment evidence could not be collected.",
                IsRetryable: exception is IOException)));
        }
    }

    private static OperatingSystemProfile CaptureOperatingSystem()
    {
        var family = OperatingSystem.IsWindows()
            ? HostOperatingSystem.Windows
            : OperatingSystem.IsLinux()
                ? HostOperatingSystem.Linux
                : HostOperatingSystem.Unknown;
        var kernel = global::System.Environment.OSVersion.VersionString;
        DistributionProfile? distribution = null;
        if (family is HostOperatingSystem.Linux)
        {
            kernel = TryReadBoundedText("/proc/sys/kernel/osrelease", 4096) ?? kernel;
            var osRelease = TryReadBoundedText("/etc/os-release", 65_536);
            if (osRelease is not null)
            {
                var parsed = OsReleaseParser.Parse(osRelease);
                distribution = parsed.IsSuccess ? parsed.Value : UnknownDistribution();
            }
            else
            {
                distribution = UnknownDistribution();
            }
        }

        return new OperatingSystemProfile(
            family,
            RuntimeInformation.OSDescription,
            kernel.Trim(),
            MapArchitecture(RuntimeInformation.OSArchitecture),
            MapArchitecture(RuntimeInformation.ProcessArchitecture),
            distribution);
    }

    private static WslProfile CaptureWsl(OperatingSystemProfile operatingSystem)
    {
        if (operatingSystem.Family is not HostOperatingSystem.Linux)
        {
            return new WslProfile(false, null, null, "operating-system-family");
        }

        var distributionName = NormalizeBounded(
            global::System.Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"),
            128);
        var kernel = operatingSystem.KernelVersion;
        var isWsl = distributionName is not null ||
            kernel.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
            File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop");
        int? generation = !isWsl
            ? null
            : kernel.Contains("wsl2", StringComparison.OrdinalIgnoreCase) ||
                kernel.Contains("microsoft-standard", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 1;
        return new WslProfile(
            isWsl,
            distributionName,
            generation,
            "kernel-and-wsl-metadata");
    }

    private static IsolationProfile CaptureIsolation(
        OperatingSystemProfile operatingSystem,
        WslProfile wsl)
    {
        if (wsl.IsWsl)
        {
            return new IsolationProfile(
                HostIsolationKind.WindowsSubsystemForLinux,
                wsl.EvidenceSource,
                wsl.DistributionName);
        }

        if (operatingSystem.Family is HostOperatingSystem.Linux)
        {
            if (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv"))
            {
                return new IsolationProfile(HostIsolationKind.Container, "container-marker-file", null);
            }

            var cgroup = TryReadBoundedText("/proc/1/cgroup", 65_536);
            if (cgroup is not null && ContainerMarkers
                .Any(marker => cgroup.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return new IsolationProfile(HostIsolationKind.Container, "proc-cgroup", null);
            }

            var product = NormalizeBounded(TryReadBoundedText("/sys/class/dmi/id/product_name", 4096), 512);
            var vendor = NormalizeBounded(TryReadBoundedText("/sys/class/dmi/id/sys_vendor", 4096), 512);
            var hint = string.Join(' ', new[] { vendor, product }.Where(item => item is not null));
            if (LooksVirtual(hint))
            {
                return new IsolationProfile(HostIsolationKind.VirtualMachine, "linux-dmi", hint);
            }

            return new IsolationProfile(HostIsolationKind.PhysicalOrUnclassified, "linux-passive-markers", product);
        }

        if (operatingSystem.Family is HostOperatingSystem.Windows && OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(global::System.Environment.GetEnvironmentVariable("CONTAINER_SANDBOX_MOUNT_POINT")))
            {
                return new IsolationProfile(HostIsolationKind.Container, "windows-container-metadata", null);
            }

            var product = ReadWindowsRegistryString(
                @"SYSTEM\CurrentControlSet\Control\SystemInformation",
                "SystemProductName");
            var manufacturer = ReadWindowsRegistryString(
                @"SYSTEM\CurrentControlSet\Control\SystemInformation",
                "SystemManufacturer");
            var hint = string.Join(' ', new[] { manufacturer, product }.Where(item => item is not null));
            return LooksVirtual(hint)
                ? new IsolationProfile(HostIsolationKind.VirtualMachine, "windows-system-information", hint)
                : new IsolationProfile(HostIsolationKind.PhysicalOrUnclassified, "windows-system-information", product);
        }

        return new IsolationProfile(HostIsolationKind.Unknown, "unsupported-operating-system", null);
    }

    private static FileSystemProfile CaptureFileSystem(HostOperatingSystem operatingSystem)
    {
        var currentRoot = Path.GetPathRoot(Path.GetFullPath(global::System.Environment.CurrentDirectory))
            ?? Path.DirectorySeparatorChar.ToString();
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string? format = null;
        try
        {
            format = new DriveInfo(currentRoot).DriveFormat;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The remaining filesystem evidence is still useful when a format probe is unavailable.
        }

        return new FileSystemProfile(
            currentRoot,
            temporaryRoot,
            Path.DirectorySeparatorChar,
            operatingSystem switch
            {
                HostOperatingSystem.Windows => false,
                HostOperatingSystem.Linux => true,
                _ => null,
            },
            NormalizeBounded(format, 128),
            "runtime-and-drive-metadata");
    }

    private static PrivilegeProfile CapturePrivilege(HostOperatingSystem operatingSystem)
    {
        if (operatingSystem is HostOperatingSystem.Windows && OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return new PrivilegeProfile(
                    principal.IsInRole(WindowsBuiltInRole.Administrator)
                        ? HostPrivilegeLevel.Elevated
                        : HostPrivilegeLevel.Standard,
                    "windows-token-role");
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
            {
                return new PrivilegeProfile(HostPrivilegeLevel.Unknown, "windows-token-unavailable");
            }
        }

        if (operatingSystem is HostOperatingSystem.Linux)
        {
            var status = TryReadBoundedText("/proc/self/status", 65_536);
            var uidLine = status?.Split('\n')
                .FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
            var fields = uidLine?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields is { Length: >= 3 } && uint.TryParse(fields[2], out var effectiveUid)
                ? new PrivilegeProfile(
                    effectiveUid == 0 ? HostPrivilegeLevel.Root : HostPrivilegeLevel.Standard,
                    "proc-self-status-effective-uid")
                : new PrivilegeProfile(HostPrivilegeLevel.Unknown, "proc-self-status-unavailable");
        }

        return new PrivilegeProfile(HostPrivilegeLevel.Unknown, "unsupported-operating-system");
    }

    private static List<EnvironmentManagerDescriptor> CaptureManagers(
        HostOperatingSystem operatingSystem,
        IReadOnlyList<ExecutableDescriptor> executables)
    {
        var managers = new List<EnvironmentManagerDescriptor>();
        var packageManagers = operatingSystem is HostOperatingSystem.Windows
            ? new[] { "winget", "choco", "scoop" }
            : new[] { "apt", "apt-get", "dnf", "yum", "pacman", "apk", "zypper", "nix" };
        foreach (var id in packageManagers)
        {
            var executable = executables.FirstOrDefault(item =>
                string.Equals(Path.GetFileNameWithoutExtension(item.Name), id, StringComparison.OrdinalIgnoreCase));
            if (executable is not null)
            {
                managers.Add(new EnvironmentManagerDescriptor(
                    id,
                    EnvironmentManagerKind.Package,
                    executable.FullPath,
                    "path-inventory"));
            }
        }

        if (operatingSystem is HostOperatingSystem.Windows)
        {
            managers.Add(new EnvironmentManagerDescriptor(
                "windows-service-control-manager",
                EnvironmentManagerKind.Service,
                null,
                "operating-system-native-service-manager"));
        }
        else if (operatingSystem is HostOperatingSystem.Linux)
        {
            if (Directory.Exists("/run/systemd/system"))
            {
                managers.Add(new EnvironmentManagerDescriptor(
                    "systemd",
                    EnvironmentManagerKind.Service,
                    "/run/systemd/system",
                    "runtime-marker-directory"));
            }

            if (Directory.Exists("/run/openrc"))
            {
                managers.Add(new EnvironmentManagerDescriptor(
                    "openrc",
                    EnvironmentManagerKind.Service,
                    "/run/openrc",
                    "runtime-marker-directory"));
            }
        }

        return managers;
    }

    private static ShellDescriptor[] CaptureShells(
        HostOperatingSystem operatingSystem,
        IReadOnlyList<ExecutableDescriptor> executables)
    {
        var known = operatingSystem is HostOperatingSystem.Windows
            ? new[] { "pwsh", "powershell", "cmd" }
            : new[] { "bash", "sh", "zsh", "fish", "dash", "pwsh" };
        var defaultValue = global::System.Environment.GetEnvironmentVariable(
            operatingSystem is HostOperatingSystem.Windows ? "COMSPEC" : "SHELL");
        var pathComparison = operatingSystem is HostOperatingSystem.Windows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return executables
            .Where(item => known.Contains(Path.GetFileNameWithoutExtension(item.Name), StringComparer.OrdinalIgnoreCase))
            .Select(item => new ShellDescriptor(
                Path.GetFileNameWithoutExtension(item.Name).ToLowerInvariant(),
                item.FullPath,
                defaultValue is not null && string.Equals(item.FullPath, defaultValue, pathComparison),
                "path-inventory-and-process-environment"))
            .DistinctBy(item => item.FullPath, operatingSystem is HostOperatingSystem.Windows
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static List<PackageDatabaseDescriptor> CapturePackageDatabases(
        HostOperatingSystem operatingSystem)
    {
        if (operatingSystem is HostOperatingSystem.Windows && OperatingSystem.IsWindows())
        {
            return
            [
                new PackageDatabaseDescriptor(
                    "windows-uninstall-registry",
                    CountWindowsInstalledPackages(),
                    "windows-registry-metadata"),
            ];
        }

        if (operatingSystem is not HostOperatingSystem.Linux)
        {
            return [];
        }

        var databases = new List<PackageDatabaseDescriptor>();
        if (File.Exists("/var/lib/dpkg/status"))
        {
            databases.Add(new PackageDatabaseDescriptor(
                "dpkg",
                CountBoundedMatchingLines("/var/lib/dpkg/status", "Status: install ok installed"),
                "dpkg-status-metadata"));
        }

        if (Directory.Exists("/var/lib/rpm"))
            databases.Add(new PackageDatabaseDescriptor("rpm", null, "rpm-database-marker"));
        if (Directory.Exists("/lib/apk/db"))
            databases.Add(new PackageDatabaseDescriptor("apk", null, "apk-database-marker"));
        if (Directory.Exists("/var/lib/pacman/local"))
            databases.Add(new PackageDatabaseDescriptor("pacman", null, "pacman-database-marker"));
        return databases;
    }

    [SupportedOSPlatform("windows")]
    private static int? CountWindowsInstalledPackages()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        try
        {
            using var registry64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(uninstall, writable: false);
            using var registry32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(uninstall, writable: false);
            return Math.Min(100_000, (registry64?.SubKeyCount ?? 0) + (registry32?.SubKeyCount ?? 0));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static int? CountBoundedMatchingLines(string path, string exactLine)
    {
        try
        {
            var count = 0;
            var examined = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (++examined > 1_000_000) return null;
                if (string.Equals(line, exactLine, StringComparison.Ordinal)) count++;
            }

            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static NetworkProfile CaptureNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces().Take(1024).ToArray();
            return new NetworkProfile(
                interfaces.Length,
                interfaces.Count(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
                    item.OperationalStatus is OperationalStatus.Up),
                interfaces.Any(item => item.NetworkInterfaceType is NetworkInterfaceType.Loopback),
                "native-network-interface-metadata");
        }
        catch (NetworkInformationException)
        {
            return new NetworkProfile(0, 0, false, "native-network-interface-unavailable");
        }
    }

    private (IReadOnlyList<ExecutableDescriptor> Items, bool Truncated) CaptureExecutables(
        HostOperatingSystem operatingSystem,
        CancellationToken cancellationToken)
    {
        var items = new List<ExecutableDescriptor>();
        var pathValue = global::System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var comparer = operatingSystem is HostOperatingSystem.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TryNormalizeDirectory)
            .Where(item => item is not null)
            .Select(item => item!)
            .Distinct(comparer)
            .Take(_options.MaximumPathDirectories)
            .ToArray();
        var truncated = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Length > directories.Length;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operatingSystem is HostOperatingSystem.Windows && directory.StartsWith(@"\\", StringComparison.Ordinal))
            {
                truncated = true;
                continue;
            }

            try
            {
                var directoryCount = 0;
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    directoryCount++;
                    if (directoryCount > _options.MaximumFilesPerDirectory || items.Count >= _options.MaximumExecutables)
                    {
                        truncated = true;
                        break;
                    }

                    if (!IsExecutable(path, operatingSystem))
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(path);
                        items.Add(new ExecutableDescriptor(
                            info.Name,
                            info.FullName,
                            info.Length,
                            info.LastWriteTimeUtc,
                            info.LinkTarget is not null,
                            info.LinkTarget,
                            "PATH",
                            ClassifyTrust(info.FullName, operatingSystem)));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        // A raced or unreadable entry remains absent; no candidate is opened or executed.
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Inaccessible PATH entries are inventory gaps rather than execution failures.
            }

            if (items.Count >= _options.MaximumExecutables)
            {
                truncated = true;
                break;
            }
        }

        return (items, truncated);
    }

    private static List<AcceleratorDescriptor> CaptureAccelerators(HostOperatingSystem operatingSystem)
    {
        var accelerators = new List<AcceleratorDescriptor>();
        if (operatingSystem is HostOperatingSystem.Linux)
        {
            try
            {
                foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*").Take(32))
                {
                    var leaf = Path.GetFileName(card);
                    if (leaf.AsSpan(4).Contains('-'))
                    {
                        continue;
                    }

                    var vendorCode = TryReadBoundedText(Path.Combine(card, "device", "vendor"), 128)?.Trim();
                    var vendor = vendorCode?.ToLowerInvariant() switch
                    {
                        "0x10de" => "NVIDIA",
                        "0x1002" => "AMD",
                        "0x8086" => "Intel",
                        null => "Unknown",
                        _ => vendorCode,
                    };
                    accelerators.Add(new AcceleratorDescriptor(vendor, leaf, "linux-sysfs-drm"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Accelerator evidence is optional and remains empty when sysfs is unavailable.
            }
        }
        else if (operatingSystem is HostOperatingSystem.Windows && OperatingSystem.IsWindows())
        {
            accelerators.AddRange(ReadWindowsDisplayAdapters());
        }

        return accelerators;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<AcceleratorDescriptor> ReadWindowsDisplayAdapters()
    {
        const string videoRoot = @"SYSTEM\CurrentControlSet\Control\Video";
        using var root = Registry.LocalMachine.OpenSubKey(videoRoot, writable: false);
        if (root is null)
        {
            yield break;
        }

        foreach (var adapterId in root.GetSubKeyNames().Take(64))
        {
            using var adapter = root.OpenSubKey($@"{adapterId}\0000", writable: false);
            var description = NormalizeBounded(
                adapter?.GetValue("DriverDesc") as string ??
                adapter?.GetValue("HardwareInformation.AdapterString") as string,
                512);
            if (description is null)
            {
                continue;
            }

            var vendor = description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                ? "NVIDIA"
                : description.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                  description.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                    ? "AMD"
                    : description.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                        ? "Intel"
                        : "Unknown";
            yield return new AcceleratorDescriptor(vendor, description, "windows-display-registry");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsRegistryString(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        return NormalizeBounded(key?.GetValue(valueName) as string, 512);
    }

    private static bool IsExecutable(string path, HostOperatingSystem operatingSystem)
    {
        if (operatingSystem is HostOperatingSystem.Windows && OperatingSystem.IsWindows())
        {
            return WindowsExecutableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        if (operatingSystem is HostOperatingSystem.Linux && OperatingSystem.IsLinux())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                return (mode & executable) != 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        return false;
    }

    private static ExecutableTrust ClassifyTrust(string path, HostOperatingSystem operatingSystem)
    {
        if (operatingSystem is HostOperatingSystem.Windows)
        {
            var windows = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Windows);
            return IsWithin(path, windows, StringComparison.OrdinalIgnoreCase)
                ? ExecutableTrust.SystemDirectory
                : ExecutableTrust.Unknown;
        }

        return LinuxSystemExecutableRoots
            .Any(root => IsWithin(path, root, StringComparison.Ordinal))
            ? ExecutableTrust.SystemDirectory
            : path.StartsWith("/home/", StringComparison.Ordinal)
                ? ExecutableTrust.UserDirectory
                : ExecutableTrust.Unknown;
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string? TryNormalizeDirectory(string value)
    {
        try
        {
            var path = Path.GetFullPath(value);
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryReadBoundedText(string path, int maximumCharacters)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maximumCharacters * 4L)
            {
                return null;
            }

            using var reader = new StreamReader(path);
            var buffer = new char[maximumCharacters + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return read > maximumCharacters ? null : new string(buffer, 0, read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static DistributionProfile UnknownDistribution() =>
        new("unknown", null, null, null, null, false);

    private static HostArchitecture MapArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X86 => HostArchitecture.X86,
        Architecture.X64 => HostArchitecture.X64,
        Architecture.Arm => HostArchitecture.Arm,
        Architecture.Arm64 => HostArchitecture.Arm64,
        _ => HostArchitecture.Unknown,
    };

    private static bool LooksVirtual(string value) =>
        VirtualizationMarkers
            .Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeBounded(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength || normalized.Any(char.IsControl)
            ? null
            : normalized;
    }
}
