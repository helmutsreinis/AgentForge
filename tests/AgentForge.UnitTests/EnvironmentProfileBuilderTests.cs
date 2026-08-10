using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;
using AgentForge.Environment;

namespace AgentForge.UnitTests;

public sealed class EnvironmentProfileBuilderTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ubuntu_fixture_is_normalized_and_fingerprint_ignores_request_metadata_and_input_order()
    {
        var first = Build(UbuntuObservation(reverseInventory: false), "operator-a", "capture-a", ObservedAt);
        var second = Build(UbuntuObservation(reverseInventory: true), "operator-b", "capture-b", ObservedAt.AddDays(1));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("ubuntu", first.OperatingSystem.Distribution?.Id);
        Assert.False(first.OperatingSystem.Distribution?.IsKali);
        Assert.Equal(["apt", "systemd"], first.Managers.Select(item => item.Id));
        Assert.Equal(["/usr/bin/dotnet", "/usr/bin/git"], first.Executables.Select(item => item.FullPath));
        Assert.NotEqual(first.ActorId, second.ActorId);
        Assert.NotEqual(first.ObservedAt, second.ObservedAt);
    }

    [Fact]
    public void Kali_is_identified_only_by_exact_distribution_id()
    {
        var kali = UbuntuObservation() with
        {
            OperatingSystem = UbuntuObservation().OperatingSystem with
            {
                Distribution = new DistributionProfile(" KALI ", "debian", "2026.2", null, "Kali GNU/Linux", false),
            },
        };
        var derivative = UbuntuObservation() with
        {
            OperatingSystem = UbuntuObservation().OperatingSystem with
            {
                Distribution = new DistributionProfile("ubuntu", "debian kali", "24.04", "noble", "Ubuntu", true),
            },
        };

        Assert.True(Build(kali).OperatingSystem.Distribution?.IsKali);
        Assert.False(Build(derivative).OperatingSystem.Distribution?.IsKali);
    }

    [Fact]
    public void Windows_fixture_preserves_case_insensitive_path_uniqueness()
    {
        var executable = new ExecutableDescriptor(
            "git.exe",
            @"C:\Program Files\Git\cmd\git.exe",
            1024,
            ObservedAt,
            false,
            null,
            "PATH",
            ExecutableTrust.Unknown);
        var observation = UbuntuObservation() with
        {
            OperatingSystem = new OperatingSystemProfile(
                HostOperatingSystem.Windows,
                "Microsoft Windows 11",
                "10.0.26100",
                HostArchitecture.X64,
                HostArchitecture.X64,
                null),
            Wsl = new WslProfile(false, null, null, "operating-system-family"),
            FileSystem = new FileSystemProfile(@"C:\", @"C:\Temp", '\\', false, "NTFS", "drive-metadata"),
            Executables =
            [
                executable,
                executable with { FullPath = @"c:\program files\git\cmd\GIT.EXE" },
            ],
        };

        var profile = Build(observation);

        Assert.Equal(HostOperatingSystem.Windows, profile.OperatingSystem.Family);
        Assert.Null(profile.OperatingSystem.Distribution);
        Assert.Single(profile.Executables);
    }

    [Fact]
    public void Oversized_executable_inventory_returns_typed_validation_failure()
    {
        var executable = UbuntuObservation().Executables[0];
        var observation = UbuntuObservation() with
        {
            Executables = Enumerable.Repeat(executable, 20_001).ToArray(),
        };

        var result = EnvironmentProfileBuilder.Build(
            observation,
            ObservedAt,
            new ActorId("operator"),
            new CorrelationId("capture"));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    private static EnvironmentProfile Build(
        EnvironmentObservation observation,
        string actor = "operator",
        string correlation = "environment-fixture",
        DateTimeOffset? observedAt = null)
    {
        var result = EnvironmentProfileBuilder.Build(
            observation,
            observedAt ?? ObservedAt,
            new ActorId(actor),
            new CorrelationId(correlation));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static EnvironmentObservation UbuntuObservation(bool reverseInventory = false)
    {
        EnvironmentManagerDescriptor[] managers =
        [
            new("systemd", EnvironmentManagerKind.Service, null, "runtime-marker"),
            new("APT", EnvironmentManagerKind.Package, "/usr/bin/apt", "PATH"),
        ];
        ExecutableDescriptor[] executables =
        [
            new("git", "/usr/bin/git", 100, ObservedAt, false, null, "PATH", ExecutableTrust.SystemDirectory),
            new("dotnet", "/usr/bin/dotnet", 200, ObservedAt, true, "/usr/share/dotnet/dotnet", "PATH", ExecutableTrust.SystemDirectory),
        ];
        if (reverseInventory)
        {
            Array.Reverse(managers);
            Array.Reverse(executables);
        }

        return new EnvironmentObservation(
            new OperatingSystemProfile(
                HostOperatingSystem.Linux,
                "Ubuntu 24.04.3 LTS",
                "6.8.0",
                HostArchitecture.X64,
                HostArchitecture.X64,
                new DistributionProfile(" Ubuntu ", "debian", "24.04", "NOBLE", "Ubuntu 24.04.3 LTS", false)),
            ".NET 10.0.2",
            16,
            new WslProfile(false, null, null, "kernel-and-wsl-metadata"),
            new IsolationProfile(HostIsolationKind.PhysicalOrUnclassified, "linux-passive-markers", "fixture"),
            new FileSystemProfile("/", "/tmp", '/', true, "ext4", "runtime-and-drive-metadata"),
            new PrivilegeProfile(HostPrivilegeLevel.Standard, "proc-self-status-effective-uid"),
            managers,
            [new AcceleratorDescriptor("NVIDIA", "fixture-gpu", "linux-sysfs-drm")],
            executables,
            false);
    }
}
