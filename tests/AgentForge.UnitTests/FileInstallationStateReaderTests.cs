using AgentForge.Domain.Installations;
using AgentForge.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests;

public sealed class FileInstallationStateReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-unit-{Guid.NewGuid():N}");

    [Fact]
    public async Task Missing_state_file_is_uninitialized()
    {
        var reader = CreateReader();

        var state = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(InstallationState.Uninitialized, state.State);
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task Corrupt_state_file_enters_recovery_instead_of_assuming_ready()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "installation-state.json"),
            "{ definitely-not-json",
            CancellationToken.None);
        var reader = CreateReader();

        var state = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(InstallationState.RecoveryRequired, state.State);
        Assert.NotNull(state.RecoveryReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileInstallationStateReader CreateReader() => new(
        Options.Create(new InstallationOptions { DataDirectory = _directory }),
        new DefaultDataDirectoryProvider(Options.Create(new InstallationOptions { DataDirectory = _directory })),
        NullLogger<FileInstallationStateReader>.Instance);
}
