using System.Diagnostics;
using System.Text.Json;

namespace AgentForge.EndToEndTests;

public sealed class HeadlessSetupCliTests
{
    [Fact]
    public async Task Deterministic_headless_begin_persists_and_rejects_duplicate_transition()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.Combine(temporaryRoot, $"agentforge-cli-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var first = await RunCliAsync(dataDirectory);
            Assert.Equal(0, first.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(first.StandardError));
            using (var document = JsonDocument.Parse(first.StandardOutput))
            {
                var root = document.RootElement;
                Assert.True(root.GetProperty("succeeded").GetBoolean());
                Assert.Equal("7a7b7fe3-c185-4c92-bb61-5daf4ca5e8a0", root.GetProperty("installationId").GetString());
                Assert.Equal("Configuring", root.GetProperty("state").GetString());
                Assert.Equal(1, root.GetProperty("version").GetInt64());
                Assert.Equal("headless-e2e-001", root.GetProperty("correlationId").GetString());
                Assert.Equal(1, root.GetProperty("auditSequence").GetInt64());
            }

            var duplicate = await RunCliAsync(dataDirectory);
            Assert.Equal(1, duplicate.ExitCode);
            using var duplicateDocument = JsonDocument.Parse(duplicate.StandardOutput);
            Assert.False(duplicateDocument.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal(
                "InvalidStateTransition",
                duplicateDocument.RootElement.GetProperty("failure").GetProperty("code").GetString());
        }
        finally
        {
            var verifiedPath = Path.GetFullPath(dataDirectory);
            var leafName = Path.GetFileName(verifiedPath);
            if (verifiedPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
                leafName.StartsWith("agentforge-cli-e2e-", StringComparison.Ordinal))
            {
                Directory.Delete(verifiedPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Interactive_and_headless_input_create_equivalent_setup_state()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var headlessDirectory = Path.Combine(temporaryRoot, $"agentforge-cli-e2e-{Guid.NewGuid():N}");
        var interactiveDirectory = Path.Combine(temporaryRoot, $"agentforge-cli-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(headlessDirectory);
        Directory.CreateDirectory(interactiveDirectory);
        try
        {
            var headless = await RunCliAsync(headlessDirectory);
            var interactive = await RunCliAsync(interactiveDirectory, interactive: true);

            Assert.Equal(0, headless.ExitCode);
            Assert.Equal(0, interactive.ExitCode);
            using var headlessJson = JsonDocument.Parse(headless.StandardOutput);
            using var interactiveJson = JsonDocument.Parse(interactive.StandardOutput);
            foreach (var property in new[] { "installationId", "state", "version", "actorId", "correlationId", "auditSequence" })
            {
                Assert.Equal(
                    headlessJson.RootElement.GetProperty(property).GetRawText(),
                    interactiveJson.RootElement.GetProperty(property).GetRawText());
            }

            Assert.Contains("Data directory:", interactive.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, headlessDirectory);
            DeleteTemporaryDirectory(temporaryRoot, interactiveDirectory);
        }
    }

    private static async Task<CliResult> RunCliAsync(string dataDirectory, bool interactive = false)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var cliAssembly = Path.Combine(root, "src", "AgentForge.Cli", "bin", configuration, "net10.0", "agentforge.dll");
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = interactive,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("setup");
        startInfo.ArgumentList.Add("begin");
        if (interactive)
        {
            startInfo.ArgumentList.Add("--interactive");
        }
        else
        {
            startInfo.ArgumentList.Add("--data-directory");
            startInfo.ArgumentList.Add(dataDirectory);
            startInfo.ArgumentList.Add("--actor");
            startInfo.ArgumentList.Add("local-operator");
            startInfo.ArgumentList.Add("--correlation");
            startInfo.ArgumentList.Add("headless-e2e-001");
            startInfo.ArgumentList.Add("--installation-id");
            startInfo.ArgumentList.Add("7a7b7fe3-c185-4c92-bb61-5daf4ca5e8a0");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the AgentForge CLI process.");
        if (interactive)
        {
            await process.StandardInput.WriteLineAsync(dataDirectory);
            await process.StandardInput.WriteLineAsync("local-operator");
            await process.StandardInput.WriteLineAsync("headless-e2e-001");
            await process.StandardInput.WriteLineAsync("7a7b7fe3-c185-4c92-bb61-5daf4ca5e8a0");
            process.StandardInput.Close();
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static void DeleteTemporaryDirectory(string temporaryRoot, string dataDirectory)
    {
        var verifiedPath = Path.GetFullPath(dataDirectory);
        var leafName = Path.GetFileName(verifiedPath);
        if (Directory.Exists(verifiedPath) &&
            verifiedPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            leafName.StartsWith("agentforge-cli-e2e-", StringComparison.Ordinal))
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
