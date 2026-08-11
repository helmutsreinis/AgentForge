using System.Diagnostics;
using System.Text;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Tools;
using AgentForge.Audit;
using AgentForge.Coding;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Tools;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using AgentForge.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class DurableCodingHarnessTests : IDisposable
{
    private const string EvidenceHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-coding-e2e-{Guid.NewGuid():N}");

    [Fact]
    public async Task Nontrivial_sample_change_resumes_without_repeating_completed_verifiers()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source");
        var worktrees = Path.Combine(_root, "worktrees");
        Directory.CreateDirectory(worktrees);
        CopyDirectory(FindSampleRoot(), source);
        await GitAsync(source, "init");
        await GitAsync(source, "config", "user.name", "AgentForge Fixture");
        await GitAsync(source, "config", "user.email", "fixture@agentforge.invalid");
        await GitAsync(source, "add", ".");
        await GitAsync(source, "commit", "-m", "buggy baseline");
        var baseline = (await GitAsync(source, "rev-parse", "HEAD")).Trim();
        var baselineTree = (await GitAsync(source, "rev-parse", "HEAD^{tree}")).Trim();
        var expectedBytes = await File.ReadAllBytesAsync(Path.Combine(source, "src", "Calculator", "Calculator.cs"));
        var patchResult = CodingPatchValidator.Create(baselineTree,
        [
            new CodingFilePatch(
                "src/Calculator/Calculator.cs",
                $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expectedBytes))}",
                "--- a/src/Calculator/Calculator.cs\n+++ b/src/Calculator/Calculator.cs\n@@ -1,6 +1,6 @@\n namespace CodingHarnessFixture;\n \n public static class Calculator\n {\n-    public static int Add(int left, int right) => left - right;\n+    public static int Add(int left, int right) => left + right;\n }\n"),
        ]);
        Assert.True(patchResult.IsSuccess);
        var backend = new DeterministicBackend(patchResult.Value);
        var sandbox = new InterruptingProcessSandbox();
        await using var services = BuildServices(backend, sandbox);
        await InitializeAsync(services);
        var installationId = new InstallationId(Guid.NewGuid());
        await BeginInstallationAsync(services, installationId);

        CodingWorkspace workspace;
        RepositoryProfile profile;
        await using (var scope = services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<ICodingWorkspaceManager>();
            var created = await manager.CreateAsync(new CodingWorkspaceRequest(
                new CodingSessionId(Guid.Parse("9ba56683-e904-462a-b17e-150bb89aa1c1")), source, worktrees,
                baseline, "codex/sample-fix"), CancellationToken.None);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            workspace = created.Value;
            var discovered = await scope.ServiceProvider.GetRequiredService<IRepositoryDiscovery>()
                .DiscoverAsync(workspace.WorktreeRoot, CancellationToken.None);
            Assert.True(discovered.IsSuccess, discovered.Failure?.Message);
            profile = discovered.Value;
        }

        var plan = CodingSessionStateMachine.CreatePlan(
        [
            ("patch", CodingPlanStepKind.Patch, "src/Calculator/Calculator.cs"),
            ("build", CodingPlanStepKind.Build, "."),
            ("test", CodingPlanStepKind.Test, "."),
            ("review", CodingPlanStepKind.Review, "."),
        ]);
        Assert.True(plan.IsSuccess);
        var dotnet = FindDotnet();
        var verification = CodingPatchValidator.CreateVerificationPlan(
        [
            new CodingVerificationCommand(
                CodingVerificationKind.Build, dotnet, ["build", "CodingHarnessFixture.slnx", "--nologo"], ".",
                new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" }, TimeSpan.FromMinutes(2), 262_144,
                ProcessSandboxKind.Container, ProcessNetworkPolicy.Denied, true),
            new CodingVerificationCommand(
                CodingVerificationKind.Test, dotnet,
                ["run", "--project", "tests/Calculator.Specs/Calculator.Specs.csproj", "--no-build"], ".",
                new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" }, TimeSpan.FromMinutes(2), 262_144,
                ProcessSandboxKind.Container, ProcessNetworkPolicy.Denied, true),
        ]);
        Assert.True(verification.IsSuccess);
        var authority = new CodingAuthoritySnapshot(
            installationId, new AgentIdentityId(Guid.NewGuid()), 1, EvidenceHash, EvidenceHash, EvidenceHash,
            EvidenceHash, CodingRecordValidator.ComputeWorkspaceHash(workspace), new ActorId("coding-operator"),
            new CorrelationId("coding-e2e"), null);
        const string objective = "Correct Calculator.Add and prove the sample build and executable specification.";

        CodingSessionSnapshot proposed;
        await using (var scope = services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICodingSessionService>();
            var created = await service.CreateAsync(new CreateCodingSessionRequest(
                workspace.SessionId, workspace, authority, profile.ProfileHash, objective,
                backend.Descriptor.Id, backend.Descriptor.Version,
                profile.Instructions.Select(item => item.ContentHash).ToArray(), plan.Value, verification.Value,
                authority.ActorId, "coding-sample", authority.CorrelationId, null), CancellationToken.None);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            var proposal = await service.ProposeAsync(
                created.Value.Id, created.Value.Version, objective, CancellationToken.None);
            Assert.True(proposal.IsSuccess, proposal.Failure?.Message);
            proposed = proposal.Value;
            Assert.Equal(CodingSessionState.PatchProposed, proposed.State);
        }

        await using (var interruptedScope = services.CreateAsyncScope())
        {
            var interrupted = await interruptedScope.ServiceProvider.GetRequiredService<ICodingSessionService>()
                .ResumeAsync(proposed.Id, CancellationToken.None);
            Assert.False(interrupted.IsSuccess);
            Assert.Equal(FailureCode.RecoverableExternalFailure, interrupted.Failure?.Code);
        }

        CodingSessionSnapshot completed;
        await using (var resumedScope = services.CreateAsyncScope())
        {
            var service = resumedScope.ServiceProvider.GetRequiredService<ICodingSessionService>();
            var resumed = await service.ResumeAsync(proposed.Id, CancellationToken.None);
            Assert.True(resumed.IsSuccess, resumed.Failure?.Message);
            completed = resumed.Value;
            Assert.Equal(CodingSessionState.Completed, completed.State);
            Assert.Equal(7, completed.Version);
            Assert.Equal(2, completed.VerificationResults.Count);
            Assert.All(completed.Plan.Steps, step => Assert.Equal(CodingPlanStepState.Completed, step.State));

            var replay = await service.ResumeAsync(proposed.Id, CancellationToken.None);
            Assert.True(replay.IsSuccess);
            Assert.Equal(completed.SnapshotHash, replay.Value.SnapshotHash);
        }

        Assert.Equal(1, sandbox.Requests.Count(request => request.Arguments.Count > 0 && request.Arguments[0] == "build"));
        Assert.Equal(2, sandbox.Requests.Count(request => request.Arguments.Count > 0 && request.Arguments[0] == "run"));
        Assert.Contains("left + right", await File.ReadAllTextAsync(Path.Combine(
            workspace.WorktreeRoot, "src", "Calculator", "Calculator.cs")));
        Assert.Contains("left - right", await File.ReadAllTextAsync(Path.Combine(
            source, "src", "Calculator", "Calculator.cs")));
        Assert.True(completed.VerificationReceipt?.Passed);
        Assert.True(completed.ReviewReport?.Passed);
        Assert.Equal(["src/Calculator/Calculator.cs"], completed.ReviewReport?.ChangedPaths);

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_root, "data", "coding.db"));
        var databaseText = Encoding.UTF8.GetString(databaseBytes);
        Assert.DoesNotContain(objective, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("left + right", databaseText, StringComparison.Ordinal);

        await GitAsync(workspace.WorktreeRoot, "add", "src/Calculator/Calculator.cs");
        await GitAsync(workspace.WorktreeRoot, "commit", "-m", "fix calculator");
        await using (var cleanupScope = services.CreateAsyncScope())
        {
            var removed = await cleanupScope.ServiceProvider.GetRequiredService<ICodingWorkspaceManager>()
                .RemoveAsync(workspace, CancellationToken.None);
            Assert.True(removed.IsSuccess, removed.Failure?.Message);
        }
    }

    private ServiceProvider BuildServices(ICodingBackend backend, InterruptingProcessSandbox sandbox)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = Path.Combine(_root, "data"),
            ["AgentForge:Persistence:DatabaseFileName"] = "coding.db",
            ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddSingleton<ISecretStore, DeterministicSecretStore>();
        services.AddAgentForgeAudit();
        services.AddAgentForgeTools(configuration);
        services.AddSingleton<ISandbox>(sandbox);
        services.AddSingleton(backend);
        services.AddAgentForgeCoding();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task InitializeAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }

    private static async Task BeginInstallationAsync(ServiceProvider services, InstallationId installationId)
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>().BeginAsync(
            new BeginSetupRequest(installationId, new ActorId("local-admin"), new CorrelationId("setup")),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.Message);
    }

    private static string FindSampleRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "samples", "CodingHarnessFixture")))
            current = current.Parent;
        return Path.Combine(current?.FullName ?? throw new InvalidOperationException("Sample root was not found."),
            "samples", "CodingHarnessFixture");
    }

    private static string FindDotnet()
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException("The dotnet host was not found on PATH.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = Start("git", workingDirectory, arguments);
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static Process Start(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment) process.StartInfo.Environment[item.Key] = item.Value;
        Assert.True(process.Start());
        return process;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class DeterministicBackend(CodingPatchSet patch) : ICodingBackend
    {
        public CodingBackendDescriptor Descriptor => new(
            "backend:sample", "1.0.0", false, ["C#"], true, true);

        public Task<DomainResult<CodingBackendProposal>> ProposeAsync(
            CodingBackendRequest request,
            CancellationToken cancellationToken) => Task.FromResult(
                CodingPatchValidator.CreateBackendProposal(Descriptor.Id, Descriptor.Version, patch));
    }

    private sealed class InterruptingProcessSandbox : ISandbox
    {
        private bool _interrupted;
        public List<ProcessExecutionRequest> Requests { get; } = [];
        public ProcessSandboxCapabilities Capabilities => new(
            ProcessSandboxKind.Container, true,
            ProcessIsolationFeature.DirectExecutable | ProcessIsolationFeature.ArgumentArray |
            ProcessIsolationFeature.EnvironmentAllowlist | ProcessIsolationFeature.WorkingDirectoryContainment |
            ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.WallClockTimeout |
            ProcessIsolationFeature.ProcessTreeTermination | ProcessIsolationFeature.NetworkIsolation |
            ProcessIsolationFeature.FileSystemIsolation,
            "test-only process-backed container contract fixture");

        public async Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
            ProcessExecutionRequest request,
            IProcessOutputObserver? observer,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!_interrupted && request.Arguments.Count > 0 && request.Arguments[0] == "run")
            {
                _interrupted = true;
                return DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                    FailureCode.RecoverableExternalFailure, "Simulated worker interruption.", true));
            }

            var started = DateTimeOffset.UtcNow;
            using var process = Start(
                request.ExecutablePath, request.WorkingDirectory, request.Arguments, request.Environment);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = Encoding.UTF8.GetBytes(await outputTask);
                var error = Encoding.UTF8.GetBytes(await errorTask);
                if (output.Length + error.Length > request.MaximumOutputBytes)
                    return DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                        FailureCode.BudgetExceeded, "Fixture output exceeded the bound."));
                var completed = DateTimeOffset.UtcNow;
                return DomainResult.Success(new ProcessExecutionResult(
                    process.ExitCode, output, error, started, completed, completed - started, Capabilities));
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }
}
