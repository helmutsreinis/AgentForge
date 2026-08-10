using System.Diagnostics;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task Headless_agent_preview_then_create_persists_the_previewed_policy()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.Combine(temporaryRoot, $"agentforge-cli-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var providerId = await SeedProviderAsync(dataDirectory);
            var preview = await RunAgentCliAsync(dataDirectory, providerId, create: false);
            Assert.Equal(0, preview.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(preview.StandardError));
            using (var previewJson = JsonDocument.Parse(preview.StandardOutput))
            {
                Assert.False(previewJson.RootElement.GetProperty("created").GetBoolean());
                Assert.Equal(JsonValueKind.Null, previewJson.RootElement.GetProperty("agentId").ValueKind);
                var externalNetwork = previewJson.RootElement.GetProperty("capabilities")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() == "network.external");
                Assert.Equal("Deny", externalNetwork.GetProperty("decision").GetString());
            }

            await using (var previewServices = BuildServices(dataDirectory))
            await using (var previewScope = previewServices.CreateAsyncScope())
            {
                Assert.Null(await previewScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                    .FindByNameAsync(
                        new InstallationId(Guid.Parse("13985bdc-a735-48db-a58f-61609d20814b")),
                        "Architect",
                        CancellationToken.None));
            }

            var created = await RunAgentCliAsync(dataDirectory, providerId, create: true);
            Assert.Equal(0, created.ExitCode);
            using var createdJson = JsonDocument.Parse(created.StandardOutput);
            Assert.True(createdJson.RootElement.GetProperty("created").GetBoolean());
            Assert.True(Guid.TryParseExact(
                createdJson.RootElement.GetProperty("agentId").GetString(),
                "D",
                out var createdId));

            await using var verificationServices = BuildServices(dataDirectory);
            await using var verificationScope = verificationServices.CreateAsyncScope();
            Assert.NotNull(await verificationScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                .FindByIdAsync(new AgentForge.Domain.Agents.AgentIdentityId(createdId), CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, dataDirectory);
        }
    }

    [Fact]
    public async Task Windows_headless_completion_creates_os_protected_administrator_and_ready_state()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.Combine(temporaryRoot, $"agentforge-cli-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var providerId = await SeedProviderAsync(dataDirectory, deterministicSecretStore: false);
            Assert.Equal(0, (await RunAgentCliAsync(dataDirectory, providerId, create: true)).ExitCode);

            var completed = await RunCompleteCliAsync(dataDirectory);
            Assert.Equal(0, completed.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(completed.StandardError));
            using var document = JsonDocument.Parse(completed.StandardOutput);
            Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal("Ready", document.RootElement.GetProperty("state").GetString());
            Assert.Equal("windows-dpapi-current-user", document.RootElement
                .GetProperty("credentialReference")
                .GetProperty("store")
                .GetString());

            var doctor = await RunMaintenanceCliAsync(
                dataDirectory,
                "doctor",
                "--actor", "local-operator",
                "--correlation", "doctor-ready-e2e");
            Assert.Equal(0, doctor.ExitCode);
            using (var doctorJson = JsonDocument.Parse(doctor.StandardOutput))
            {
                Assert.True(doctorJson.RootElement.GetProperty("succeeded").GetBoolean());
                Assert.Equal("Ready", doctorJson.RootElement.GetProperty("state").GetString());
            }

            var exported = await RunMaintenanceCliAsync(
                dataDirectory,
                "setup", "export",
                "--expected-version", "3",
                "--actor", "local-operator",
                "--correlation", "export-e2e");
            Assert.Equal(0, exported.ExitCode);
            using (var exportJson = JsonDocument.Parse(exported.StandardOutput))
            {
                Assert.StartsWith(
                    "sha256:",
                    exportJson.RootElement.GetProperty("report").GetProperty("contentHash").GetString(),
                    StringComparison.Ordinal);
                Assert.StartsWith(
                    "sha256:",
                    exportJson.RootElement.GetProperty("rollback").GetProperty("contentHash").GetString(),
                    StringComparison.Ordinal);
            }

            var entered = await RunMaintenanceCliAsync(
                dataDirectory,
                "setup", "recovery", "enter",
                "--expected-version", "3",
                "--reason", "end-to-end maintenance",
                "--actor", "local-operator",
                "--correlation", "recovery-enter-e2e");
            Assert.Equal(0, entered.ExitCode);
            using (var enteredJson = JsonDocument.Parse(entered.StandardOutput))
            {
                Assert.Equal("RecoveryRequired", enteredJson.RootElement.GetProperty("state").GetString());
                Assert.Equal(4, enteredJson.RootElement.GetProperty("version").GetInt64());
            }

            Assert.Equal(2, (await RunMaintenanceCliAsync(
                dataDirectory,
                "doctor",
                "--actor", "local-operator",
                "--correlation", "doctor-recovery-e2e")).ExitCode);

            var resumed = await RunMaintenanceCliAsync(
                dataDirectory,
                "setup", "recovery", "resume",
                "--expected-version", "4",
                "--actor", "local-operator",
                "--correlation", "recovery-resume-e2e");
            Assert.Equal(0, resumed.ExitCode);
            using (var resumedJson = JsonDocument.Parse(resumed.StandardOutput))
            {
                Assert.Equal("Configuring", resumedJson.RootElement.GetProperty("state").GetString());
                Assert.Equal(5, resumedJson.RootElement.GetProperty("version").GetInt64());
            }

            var recompleted = await RunCompleteCliAsync(dataDirectory);
            Assert.Equal(0, recompleted.ExitCode);
            using (var recompletedJson = JsonDocument.Parse(recompleted.StandardOutput))
            {
                Assert.Equal("Ready", recompletedJson.RootElement.GetProperty("state").GetString());
                Assert.Equal(7, recompletedJson.RootElement.GetProperty("version").GetInt64());
            }

            await using var services = BuildServices(dataDirectory, deterministicSecretStore: false);
            await using var scope = services.CreateAsyncScope();
            var installation = await scope.ServiceProvider.GetRequiredService<AgentForge.Abstractions.Installations.IInstallationRepository>()
                .ReadAsync(CancellationToken.None);
            Assert.Equal(AgentForge.Domain.Installations.InstallationState.Ready, installation.State);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, dataDirectory);
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

    private static async Task<CliResult> RunAgentCliAsync(
        string dataDirectory,
        ProviderProfileId providerId,
        bool create)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(root, "src", "AgentForge.Cli", "bin", configuration, "net10.0", "agentforge.dll"));
        foreach (var argument in new[]
        {
            "setup", "agent", create ? "create" : "preview",
            "--data-directory", dataDirectory,
            "--name", "Architect",
            "--provider-id", providerId.ToString(),
            "--actor", "local-operator",
            "--correlation", create ? "agent-create-e2e" : "agent-preview-e2e",
            "--max-children", "2",
            "--max-child-depth", "2",
            "--max-child-concurrency", "1",
            "--max-child-tokens", "10000",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the AgentForge CLI process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task<CliResult> RunCompleteCliAsync(string dataDirectory)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            Path.Combine(root, "src", "AgentForge.Cli", "bin", configuration, "net10.0", "agentforge.dll"),
            "setup", "complete",
            "--data-directory", dataDirectory,
            "--actor", "local-operator",
            "--correlation", "setup-complete-e2e",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the AgentForge CLI process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task<CliResult> RunMaintenanceCliAsync(
        string dataDirectory,
        params string[] commandArguments)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(
            root,
            "src",
            "AgentForge.Cli",
            "bin",
            configuration,
            "net10.0",
            "agentforge.dll"));
        foreach (var argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--data-directory");
        startInfo.ArgumentList.Add(dataDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the AgentForge CLI process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task<ProviderProfileId> SeedProviderAsync(
        string dataDirectory,
        bool deterministicSecretStore = true)
    {
        await using var services = BuildServices(dataDirectory, deterministicSecretStore);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);
        var setup = scope.ServiceProvider.GetRequiredService<ISetupApplicationService>();
        Assert.True((await setup.BeginAsync(new AgentForge.Domain.Setup.BeginSetupRequest(
            new InstallationId(Guid.Parse("13985bdc-a735-48db-a58f-61609d20814b")),
            new ActorId("local-operator"),
            new CorrelationId("agent-seed-begin")), CancellationToken.None)).IsSuccess);
        var secret = await scope.ServiceProvider.GetRequiredService<ISecretStore>()
            .StoreAsync("e2e-provider", "e2e-fixture".AsMemory(), CancellationToken.None);
        Assert.True(secret.IsSuccess);
        var provider = await setup.ConfigureProviderAsync(new ConfigureProviderRequest(
            new ProviderProfileCandidate(
                "primary",
                "deterministic",
                new Uri("http://127.0.0.1:9000/v1"),
                "deterministic-text-v1",
                secret.Value),
            new ActorId("local-operator"),
            new CorrelationId("agent-seed-provider")), CancellationToken.None);
        Assert.True(provider.IsSuccess);
        return provider.Value.Profile.Id;
    }

    private static ServiceProvider BuildServices(
        string dataDirectory,
        bool deterministicSecretStore = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:Installation:DataDirectory"] = dataDirectory,
                ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        if (deterministicSecretStore)
        {
            services.AddSingleton<ISecretStore, DeterministicSecretStore>();
        }

        services.AddAgentForgeAudit();
        return services.BuildServiceProvider(validateScopes: true);
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
