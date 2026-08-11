using System.Diagnostics;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Coding;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class CodingDiscoveryAndWorkspaceTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), $"agentforge-coding-{Guid.NewGuid():N}");

    [Fact]
    public async Task Discovery_and_Roslyn_navigation_return_bounded_project_evidence()
    {
        var root = FindRepositoryRoot();
        await using var services = BuildServices();
        var result = await services.GetRequiredService<IRepositoryDiscovery>()
            .DiscoverAsync(root, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Contains(result.Value.Projects, item => item.RelativePath == "src/AgentForge.Domain/AgentForge.Domain.csproj");
        Assert.Contains("MSBuild", result.Value.BuildSystems);
        Assert.Contains("C#", result.Value.Languages);
        Assert.StartsWith("sha256:", result.Value.ProfileHash, StringComparison.Ordinal);
        Assert.Contains(result.Value.Instructions, item => item.RelativePath == "README.md");

        const string relativePath = "src/AgentForge.Domain/Coding/CodingRecords.cs";
        var lines = await File.ReadAllLinesAsync(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var line = Array.FindIndex(lines, item => item.Contains("CodingSessionId", StringComparison.Ordinal));
        var column = lines[line].IndexOf("CodingSessionId", StringComparison.Ordinal) + 2;
        var semantic = await services.GetRequiredService<ISemanticNavigator>().AnalyzeAsync(
            result.Value, new SemanticQuery(relativePath, line, column), CancellationToken.None);

        Assert.True(semantic.IsSuccess, semantic.Failure?.Message);
        Assert.Equal("CodingSessionId", semantic.Value.Symbol?.Name);
        Assert.Equal(relativePath, semantic.Value.Symbol?.Definition.RelativePath);
        Assert.StartsWith("sha256:", semantic.Value.EvidenceHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worktree_creation_rejects_dirty_source_and_removes_only_clean_managed_target()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var repository = Path.Combine(_temporaryRoot, "source");
        var workspaceParent = Path.Combine(_temporaryRoot, "worktrees");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(workspaceParent);
        await GitAsync(repository, "init");
        await GitAsync(repository, "config", "user.name", "AgentForge Fixture");
        await GitAsync(repository, "config", "user.email", "fixture@agentforge.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "owned.txt"), "baseline\n");
        await GitAsync(repository, "add", "owned.txt");
        await GitAsync(repository, "commit", "-m", "baseline");
        var baseline = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();

        await using var services = BuildServices();
        var manager = services.GetRequiredService<ICodingWorkspaceManager>();
        var request = new CodingWorkspaceRequest(
            new CodingSessionId(Guid.NewGuid()), repository, workspaceParent, baseline, "codex/fixture");
        await File.WriteAllTextAsync(Path.Combine(repository, "operator.txt"), "operator change\n");
        var denied = await manager.CreateAsync(request, CancellationToken.None);
        Assert.False(denied.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, denied.Failure?.Code);
        File.Delete(Path.Combine(repository, "operator.txt"));

        var created = await manager.CreateAsync(request, CancellationToken.None);
        Assert.True(created.IsSuccess, created.Failure?.Message);
        Assert.True(Directory.Exists(created.Value.WorktreeRoot));
        Assert.Equal("baseline\n", await File.ReadAllTextAsync(Path.Combine(repository, "owned.txt")));
        Assert.Equal(baseline, created.Value.BaselineCommit);

        var removed = await manager.RemoveAsync(created.Value, CancellationToken.None);
        Assert.True(removed.IsSuccess, removed.Failure?.Message);
        Assert.False(Directory.Exists(created.Value.WorktreeRoot));
        Assert.True(File.Exists(Path.Combine(repository, "owned.txt")));
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ISandbox, UnavailableSandbox>();
        services.AddAgentForgeCoding();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AgentForge.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_temporaryRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class UnavailableSandbox : ISandbox
    {
        public ProcessSandboxCapabilities Capabilities => new(
            ProcessSandboxKind.RestrictedHost, false, ProcessIsolationFeature.None, "not-used");

        public Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
            ProcessExecutionRequest request,
            IProcessOutputObserver? observer,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<ProcessExecutionResult>(
                new DomainFailure(FailureCode.UnsupportedCapability, "Not used by this fixture.")));
    }
}
