using System.Diagnostics;
using System.Text.Json;

namespace AgentForge.EndToEndTests;

public sealed class EnvironmentInventoryCliTests
{
    [Fact]
    public async Task Passive_environment_inspection_persists_evidence_and_hides_executable_details_by_default()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.Combine(temporaryRoot, $"agentforge-environment-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var first = await RunEnvironmentInspectAsync(dataDirectory);
            Assert.Equal(0, first.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(first.StandardError), first.StandardError);
            using var firstJson = JsonDocument.Parse(first.StandardOutput);
            var firstRoot = firstJson.RootElement;
            Assert.True(firstRoot.GetProperty("succeeded").GetBoolean());
            Assert.StartsWith("sha256:", firstRoot.GetProperty("fingerprint").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, firstRoot.GetProperty("executables").ValueKind);
            Assert.True(firstRoot.GetProperty("executableCount").GetInt32() >= 0);
            Assert.Equal(
                OperatingSystem.IsWindows() ? "Windows" : "Linux",
                firstRoot.GetProperty("operatingSystem").GetProperty("family").GetString());

            var contentHash = firstRoot.GetProperty("artifact").GetProperty("contentHash").GetString()!;
            var hexHash = contentHash["sha256:".Length..];
            Assert.True(File.Exists(Path.Combine(
                dataDirectory,
                "artifacts",
                "sha256",
                hexHash[..2],
                hexHash)));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, dataDirectory);
        }
    }

    [Fact]
    public async Task Candidate_in_path_is_inventoried_without_being_executed()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var workspace = Path.Combine(temporaryRoot, $"agentforge-environment-e2e-{Guid.NewGuid():N}");
        var candidateDirectory = Path.Combine(workspace, "candidates");
        var dataDirectory = Path.Combine(workspace, "data");
        Directory.CreateDirectory(candidateDirectory);
        Directory.CreateDirectory(dataDirectory);
        var sentinel = Path.Combine(workspace, "candidate-executed.txt");
        var candidate = Path.Combine(
            candidateDirectory,
            OperatingSystem.IsWindows() ? "untrusted-candidate.cmd" : "untrusted-candidate");
        try
        {
            await File.WriteAllTextAsync(
                candidate,
                OperatingSystem.IsWindows()
                    ? $"@echo off\r\necho executed>\"{sentinel}\""
                    : $"#!/bin/sh\nprintf executed > \"{sentinel}\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    candidate,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var result = await RunEnvironmentInspectAsync(
                dataDirectory,
                candidateDirectory,
                includeExecutables: true);

            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.StandardOutput);
            var executable = Assert.Single(json.RootElement.GetProperty("executables").EnumerateArray());
            Assert.Equal(Path.GetFileName(candidate), executable.GetProperty("name").GetString());
            Assert.Equal(Path.GetFullPath(candidate), executable.GetProperty("fullPath").GetString());
            Assert.Equal("Unknown", executable.GetProperty("trust").GetString());
            Assert.False(File.Exists(sentinel));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, workspace);
        }
    }

    private static async Task<CliResult> RunEnvironmentInspectAsync(
        string dataDirectory,
        string? pathOverride = null,
        bool includeExecutables = false)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var startInfo = new ProcessStartInfo
        {
            FileName = global::System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            Path.Combine(root, "src", "AgentForge.Cli", "bin", configuration, "net10.0", "agentforge.dll"),
            "environment", "inspect",
            "--data-directory", dataDirectory,
            "--actor", "environment-operator",
            "--correlation", "environment-e2e",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (includeExecutables)
        {
            startInfo.ArgumentList.Add("--include-executables");
            startInfo.ArgumentList.Add("true");
        }

        if (pathOverride is not null)
        {
            startInfo.Environment["PATH"] = pathOverride;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the AgentForge CLI process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void DeleteTemporaryDirectory(string temporaryRoot, string dataDirectory)
    {
        var verifiedPath = Path.GetFullPath(dataDirectory);
        if (verifiedPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(verifiedPath).StartsWith("agentforge-environment-e2e-", StringComparison.Ordinal))
        {
            Directory.Delete(verifiedPath, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the AgentForge repository root.");
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
