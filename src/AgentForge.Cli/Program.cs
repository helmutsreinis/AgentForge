using System.Net;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Setup;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (args is ["setup", "begin", .. var beginArguments])
{
    return await BeginSetupAsync(beginArguments);
}

if (args is ["setup", "provider", "configure", .. var providerConfigureArguments])
{
    return await ConfigureProviderCredentialAsync(providerConfigureArguments);
}

if (args is ["setup", "provider", "edit", "preview", .. var providerEditPreviewArguments])
{
    return await EditProviderAsync(providerEditPreviewArguments, apply: false);
}

if (args is ["setup", "provider", "edit", "apply", .. var providerEditApplyArguments])
{
    return await EditProviderAsync(providerEditApplyArguments, apply: true);
}

if (args is ["setup", "agent", "preview", .. var previewArguments])
{
    return await ConfigureAgentAsync(previewArguments, create: false);
}

if (args is ["setup", "agent", "create", .. var createArguments])
{
    return await ConfigureAgentAsync(createArguments, create: true);
}

if (args is ["setup", "agent", "edit", "preview", .. var agentEditPreviewArguments])
{
    return await EditAgentAsync(agentEditPreviewArguments, apply: false);
}

if (args is ["setup", "agent", "edit", "apply", .. var agentEditApplyArguments])
{
    return await EditAgentAsync(agentEditApplyArguments, apply: true);
}

if (args is ["setup", "complete", .. var completeArguments])
{
    return await CompleteSetupAsync(completeArguments);
}

if (args is ["doctor", .. var doctorArguments])
{
    return await DoctorAsync(doctorArguments);
}

if (args is ["setup", "export", .. var exportArguments])
{
    return await ExportSetupAsync(exportArguments);
}

if (args is ["setup", "recovery", "enter", .. var recoveryEnterArguments])
{
    return await TransitionRecoveryAsync(recoveryEnterArguments, enter: true);
}

if (args is ["setup", "recovery", "resume", .. var recoveryResumeArguments])
{
    return await TransitionRecoveryAsync(recoveryResumeArguments, enter: false);
}

var endpoint = Environment.GetEnvironmentVariable("AGENTFORGE_ENDPOINT") ?? "http://127.0.0.1:5047";
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseAddress))
{
    await Console.Error.WriteLineAsync("AGENTFORGE_ENDPOINT must be an absolute URI.");
    return 1;
}

var path = args switch
{
    ["status"] => "/api/v1/status",
    ["setup", "status"] => "/api/v1/setup/status",
    _ => null,
};

if (path is null)
{
    await Console.Error.WriteLineAsync("Unknown command.");
    PrintHelp();
    return 1;
}

using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
try
{
    using var response = await client.GetAsync(path);
    var payload = await response.Content.ReadAsStringAsync();
    await Console.Out.WriteLineAsync(payload);

    return response.StatusCode switch
    {
        HttpStatusCode.OK => 0,
        HttpStatusCode.ServiceUnavailable => 2,
        _ => 1,
    };
}
catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
{
    await Console.Error.WriteLineAsync($"AgentForge host is unavailable: {exception.Message}");
    return 1;
}

static async Task<int> BeginSetupAsync(string[] arguments)
{
    if (arguments is ["--interactive"])
    {
        arguments = await ReadInteractiveArgumentsAsync();
    }

    if (!TryParseBeginOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    string dataDirectory;
    try
    {
        dataDirectory = Path.GetFullPath(options!.DataDirectory);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    var settings = new Dictionary<string, string?>
    {
        ["AgentForge:Installation:DataDirectory"] = dataDirectory,
    };
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(settings)
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();

    await using var provider = services.BuildServiceProvider(validateScopes: true);
    await using var scope = provider.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellation.Token);
        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .BeginAsync(new BeginSetupRequest(
                options.InstallationId,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId)), cancellation.Token);
        if (!result.IsSuccess)
        {
            await WriteJsonAsync(new
            {
                succeeded = false,
                failure = new
                {
                    code = result.Failure!.Code.ToString(),
                    result.Failure.Message,
                    result.Failure.IsRetryable,
                },
            });
            return result.Failure!.Code is FailureCode.ConcurrencyConflict ? 3 : 1;
        }

        var completed = result.Value;
        await WriteJsonAsync(new
        {
            succeeded = true,
            installationId = completed.Installation.Id.ToString(),
            state = completed.Installation.State.ToString(),
            completed.Installation.Version,
            actorId = completed.Installation.ActorId.Value,
            correlationId = completed.Installation.CorrelationId.Value,
            auditEventId = completed.AuditEvent.EventId,
            auditSequence = completed.AuditEvent.Sequence,
        });
        return 0;
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Setup was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        await WriteJsonAsync(new
        {
            succeeded = false,
            failure = new
            {
                code = FailureCode.RecoverableExternalFailure.ToString(),
                message = "Setup storage could not be initialized or updated.",
                isRetryable = true,
            },
        });
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> ConfigureProviderCredentialAsync(string[] arguments)
{
    if (!TryParseProviderConfigureOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    if (!TryNormalizeDataDirectory(options!.DataDirectory, out var dataDirectory))
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    return await RunCancellableMaintenanceAsync("Provider setup", async cancellationToken =>
    {
        var credentialResult = await ReadProviderCredentialAsync(options.ReadFromStandardInput, cancellationToken);
        if (!credentialResult.IsSuccess)
        {
            return await WriteFailureAsync(credentialResult.Failure!);
        }

        var credential = credentialResult.Value;
        try
        {
            await using var provider = BuildSetupProvider(dataDirectory);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(cancellationToken);
            var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .ConfigureProviderCredentialAsync(new ConfigureProviderCredentialRequest(
                    options.Name,
                    options.ProviderType,
                    options.Endpoint,
                    options.Model,
                    credential,
                    new ActorId(options.ActorId),
                    new CorrelationId(options.CorrelationId)), cancellationToken);
            if (!result.IsSuccess)
            {
                return await WriteFailureAsync(result.Failure!);
            }

            await WriteJsonAsync(new
            {
                succeeded = true,
                providerId = result.Value.Profile.Id.ToString(),
                result.Value.Profile.Name,
                type = result.Value.Profile.ProviderType,
                endpoint = result.Value.Profile.Endpoint.AbsoluteUri,
                result.Value.Profile.Model,
                secretReference = new
                {
                    store = result.Value.Profile.SecretReference.Store,
                    key = result.Value.Profile.SecretReference.Key,
                },
                result.Value.Profile.Capabilities,
                version = result.Value.Profile.Version,
            });
            return 0;
        }
        finally
        {
            Array.Clear(credential);
        }
    });
}

static async Task<int> EditProviderAsync(string[] arguments, bool apply)
{
    if (!TryParseProviderEditOptions(arguments, apply, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    if (!TryNormalizeDataDirectory(options!.DataDirectory, out var dataDirectory))
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    return await RunCancellableMaintenanceAsync("Provider profile edit", async cancellationToken =>
    {
        await using var provider = BuildSetupProvider(dataDirectory);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellationToken);
        var installation = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(cancellationToken);
        var materialized = await MaterializeAdministratorAsync(
            scope.ServiceProvider,
            installation.Id,
            cancellationToken);
        if (!materialized.IsSuccess)
        {
            return await WriteFailureAsync(materialized.Failure!);
        }

        await using var credential = materialized.Value;
        var current = await scope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .FindByIdAsync(options.ProviderProfileId, cancellationToken);
        if (current is null)
        {
            return await WriteFailureAsync(new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider profile was not found."));
        }

        var candidate = new ProviderProfileCandidate(
            options.Name,
            options.ProviderType,
            options.Endpoint,
            options.Model,
            current.SecretReference);
        var editor = scope.ServiceProvider.GetRequiredService<ISetupProfileEditor>();
        if (!apply)
        {
            var preview = await editor.PreviewProviderAsync(new PreviewProviderEditRequest(
                options.ProviderProfileId,
                options.ExpectedInstallationVersion,
                options.ExpectedProviderVersion,
                candidate,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId),
                credential.Value), cancellationToken);
            if (!preview.IsSuccess)
            {
                return await WriteFailureAsync(preview.Failure!);
            }

            await WriteJsonAsync(new
            {
                succeeded = true,
                applied = false,
                requestHash = preview.Value.RequestHash,
                changes = preview.Value.Changes,
                effective = new
                {
                    preview.Value.Effective.Name,
                    type = preview.Value.Effective.ProviderType,
                    endpoint = preview.Value.Effective.Endpoint.AbsoluteUri,
                    preview.Value.Effective.Model,
                    preview.Value.Effective.Capabilities,
                    version = preview.Value.Effective.Version,
                },
            });
            return 0;
        }

        var applied = await editor.ApplyProviderAsync(new ApplyProviderEditRequest(
            options.ProviderProfileId,
            options.ExpectedInstallationVersion,
            options.ExpectedProviderVersion,
            candidate,
            options.PreviewHash!,
            new ActorId(options.ActorId),
            new CorrelationId(options.CorrelationId),
            credential.Value), cancellationToken);
        if (!applied.IsSuccess)
        {
            return await WriteFailureAsync(applied.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = true,
            applied = true,
            installationVersion = applied.Value.Installation.Version,
            providerVersion = applied.Value.Provider.Version,
            requestHash = applied.Value.RequestHash,
            changes = applied.Value.Changes,
        });
        return 0;
    });
}

static async Task<int> ConfigureAgentAsync(string[] arguments, bool create)
{
    if (!TryParseAgentOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    string dataDirectory;
    try
    {
        dataDirectory = Path.GetFullPath(options!.DataDirectory);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = dataDirectory,
        })
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();

    await using var provider = services.BuildServiceProvider(validateScopes: true);
    await using var scope = provider.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellation.Token);
        var candidate = CreateAgentCandidate(options);
        var setup = scope.ServiceProvider.GetRequiredService<ISetupApplicationService>();
        if (!create)
        {
            var preview = await setup.PreviewAgentAsync(new PreviewAgentRequest(
                candidate,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId)), cancellation.Token);
            if (!preview.IsSuccess)
            {
                return await WriteFailureAsync(preview.Failure!);
            }

            await WriteEffectiveAgentAsync(preview.Value, agentId: null, created: false);
            return 0;
        }

        var created = await setup.CreateAgentAsync(new CreateAgentRequest(
            candidate,
            new ActorId(options.ActorId),
            new CorrelationId(options.CorrelationId)), cancellation.Token);
        if (!created.IsSuccess)
        {
            return await WriteFailureAsync(created.Failure!);
        }

        await WriteEffectiveAgentAsync(
            created.Value.EffectiveDefinition,
            created.Value.Agent.Id.ToString(),
            created: true);
        return 0;
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Agent setup was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return await WriteFailureAsync(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            "Agent setup storage could not be initialized or updated.",
            IsRetryable: true));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> EditAgentAsync(string[] arguments, bool apply)
{
    if (!TryParseAgentEditOptions(arguments, apply, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    if (!TryNormalizeDataDirectory(options!.Agent.DataDirectory, out var dataDirectory))
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    return await RunCancellableMaintenanceAsync("Agent profile edit", async cancellationToken =>
    {
        await using var provider = BuildSetupProvider(dataDirectory);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellationToken);
        var installation = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(cancellationToken);
        var materialized = await MaterializeAdministratorAsync(
            scope.ServiceProvider,
            installation.Id,
            cancellationToken);
        if (!materialized.IsSuccess)
        {
            return await WriteFailureAsync(materialized.Failure!);
        }

        await using var credential = materialized.Value;
        var candidate = CreateAgentCandidate(options.Agent);
        var editor = scope.ServiceProvider.GetRequiredService<ISetupProfileEditor>();
        if (!apply)
        {
            var preview = await editor.PreviewAgentAsync(new PreviewAgentEditRequest(
                options.AgentIdentityId,
                options.ExpectedInstallationVersion,
                options.ExpectedAgentVersion,
                candidate,
                new ActorId(options.Agent.ActorId),
                new CorrelationId(options.Agent.CorrelationId),
                credential.Value), cancellationToken);
            if (!preview.IsSuccess)
            {
                return await WriteFailureAsync(preview.Failure!);
            }

            await WriteJsonAsync(new
            {
                succeeded = true,
                applied = false,
                requestHash = preview.Value.RequestHash,
                changes = preview.Value.Changes,
                effective = new
                {
                    preview.Value.Effective.Agent.Name,
                    preview.Value.Effective.ProviderName,
                    preview.Value.Effective.Model,
                    preview.Value.Effective.Capabilities,
                    version = preview.Value.Current.Version + 1,
                },
            });
            return 0;
        }

        var applied = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
            options.AgentIdentityId,
            options.ExpectedInstallationVersion,
            options.ExpectedAgentVersion,
            candidate,
            options.PreviewHash!,
            new ActorId(options.Agent.ActorId),
            new CorrelationId(options.Agent.CorrelationId),
            credential.Value), cancellationToken);
        if (!applied.IsSuccess)
        {
            return await WriteFailureAsync(applied.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = true,
            applied = true,
            installationVersion = applied.Value.Installation.Version,
            agentVersion = applied.Value.Agent.Version,
            requestHash = applied.Value.RequestHash,
            changes = applied.Value.Changes,
        });
        return 0;
    });
}

static AgentIdentityCandidate CreateAgentCandidate(SetupAgentOptions options) => new(
    options.Name,
    options.Expertise,
    options.Mission,
    options.Language,
    options.TimeZone,
    options.Style,
    options.Workspace,
    new AgentModelPolicy(options.ProviderId, options.DataLocality, options.AllowFallback),
    new AgentMemoryPolicy(options.MemoryScope, options.MemoryRetentionDays),
    new AgentCapabilityPolicy(options.NetworkPosture, [], []),
    new AgentBudget(
        options.MaxTurns,
        options.MaxToolInvocations,
        options.MaxInputTokens,
        options.MaxOutputTokens,
        options.MaxWallClockSeconds),
    new ChildAgentLimits(
        options.MaxChildDepth,
        options.MaxChildren,
        options.MaxChildConcurrency,
        options.MaxChildTotalTokens),
    new AgentLearningPolicy(options.LearningMode, options.MutableSkillScope));

static async Task<int> CompleteSetupAsync(string[] arguments)
{
    if (!TryParseCompleteOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    string dataDirectory;
    try
    {
        dataDirectory = Path.GetFullPath(options!.DataDirectory);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = dataDirectory,
        })
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();

    await using var provider = services.BuildServiceProvider(validateScopes: true);
    await using var scope = provider.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellation.Token);
        var installation = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(cancellation.Token);
        var administrator = await scope.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>()
            .FindAsync(installation.Id, cancellation.Token);
        AgentForge.Domain.Security.SecretLease? existingCredential = null;
        if (administrator is not null)
        {
            var materialized = await scope.ServiceProvider.GetRequiredService<ISecretStore>()
                .MaterializeAsync(administrator.ClientCredentialReference, cancellation.Token);
            if (!materialized.IsSuccess)
            {
                return await WriteFailureAsync(materialized.Failure!);
            }

            existingCredential = materialized.Value;
        }

        DomainResult<AgentForge.Domain.Security.SetupCompletionReport> result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .CompleteAsync(new AgentForge.Domain.Security.CompleteSetupRequest(
                    new ActorId(options.ActorId),
                    new CorrelationId(options.CorrelationId),
                    existingCredential?.Value ?? default), cancellation.Token);
        }
        finally
        {
            if (existingCredential is not null)
            {
                await existingCredential.DisposeAsync();
            }
        }

        if (!result.IsSuccess)
        {
            return await WriteFailureAsync(result.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = true,
            state = result.Value.Installation.State.ToString(),
            version = result.Value.Installation.Version,
            administratorId = result.Value.Administrator.Id.ToString(),
            actorId = result.Value.Administrator.ActorId.Value,
            credentialReference = new
            {
                store = result.Value.Administrator.ClientCredentialReference.Store,
                key = result.Value.Administrator.ClientCredentialReference.Key,
            },
            checks = result.Value.Checks,
        });
        return 0;
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Setup completion was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return await WriteFailureAsync(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            "Setup completion storage could not be initialized or updated.",
            IsRetryable: true));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> DoctorAsync(string[] arguments)
{
    if (!TryParseDoctorOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    if (!TryNormalizeDataDirectory(options!.DataDirectory, out var dataDirectory))
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    return await RunCancellableMaintenanceAsync("Doctor", async cancellationToken =>
    {
        await using var provider = BuildSetupProvider(dataDirectory);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellationToken);
        var result = await scope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
            .DoctorAsync(new DoctorRequest(
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId)), cancellationToken);
        if (!result.IsSuccess)
        {
            return await WriteFailureAsync(result.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = result.Value.IsHealthy,
            generatedAt = result.Value.GeneratedAt,
            installationId = result.Value.Installation.Id.ToString(),
            state = result.Value.Installation.State.ToString(),
            version = result.Value.Installation.Version,
            checks = result.Value.Checks,
        });
        return result.Value.IsHealthy ? 0 : 2;
    });
}

static async Task<int> ExportSetupAsync(string[] arguments)
{
    if (!TryParseMaintenanceOptions(arguments, requireReason: false, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    if (!TryNormalizeDataDirectory(options!.DataDirectory, out var dataDirectory))
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    return await RunCancellableMaintenanceAsync("Setup export", async cancellationToken =>
    {
        await using var provider = BuildSetupProvider(dataDirectory);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellationToken);
        var installation = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(cancellationToken);
        var materialized = await MaterializeAdministratorAsync(
            scope.ServiceProvider,
            installation.Id,
            cancellationToken);
        if (!materialized.IsSuccess)
        {
            return await WriteFailureAsync(materialized.Failure!);
        }

        await using var credential = materialized.Value;
        var result = await scope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
            .ExportAsync(new ExportSetupProfileRequest(
                options.ExpectedVersion,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId),
                credential.Value), cancellationToken);
        if (!result.IsSuccess)
        {
            return await WriteFailureAsync(result.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = true,
            profileVersion = options.ExpectedVersion,
            report = result.Value.Report.Artifact,
            rollback = result.Value.Rollback.Artifact,
            result.Value.RedactionCount,
        });
        return 0;
    });
}

static async Task<int> TransitionRecoveryAsync(string[] arguments, bool enter)
{
    if (!TryParseMaintenanceOptions(arguments, requireReason: enter, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    if (!TryNormalizeDataDirectory(options!.DataDirectory, out var dataDirectory))
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    return await RunCancellableMaintenanceAsync("Recovery transition", async cancellationToken =>
    {
        await using var provider = BuildSetupProvider(dataDirectory);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellationToken);
        var installation = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(cancellationToken);
        var materialized = await MaterializeAdministratorAsync(
            scope.ServiceProvider,
            installation.Id,
            cancellationToken);
        if (!materialized.IsSuccess)
        {
            return await WriteFailureAsync(materialized.Failure!);
        }

        await using var credential = materialized.Value;
        var maintenance = scope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>();
        DomainResult<RecoveryTransitionResult> result = enter
            ? await maintenance.EnterRecoveryAsync(new EnterRecoveryRequest(
                options.ExpectedVersion,
                options.Reason!,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId),
                credential.Value), cancellationToken)
            : await maintenance.ResumeRecoveryAsync(new ResumeRecoveryRequest(
                options.ExpectedVersion,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId),
                credential.Value), cancellationToken);
        if (!result.IsSuccess)
        {
            return await WriteFailureAsync(result.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = true,
            state = result.Value.Installation.State.ToString(),
            version = result.Value.Installation.Version,
            result.Value.Installation.RecoveryReason,
            rollback = result.Value.RollbackSnapshot?.Artifact,
        });
        return 0;
    });
}

static ServiceProvider BuildSetupProvider(string dataDirectory)
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = dataDirectory,
        })
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();
    return services.BuildServiceProvider(validateScopes: true);
}

static async Task<DomainResult<AgentForge.Domain.Security.SecretLease>> MaterializeAdministratorAsync(
    IServiceProvider services,
    InstallationId installationId,
    CancellationToken cancellationToken)
{
    var administrator = await services.GetRequiredService<ILocalAdministratorRepository>()
        .FindAsync(installationId, cancellationToken);
    if (administrator is null)
    {
        return DomainResult.Fail<AgentForge.Domain.Security.SecretLease>(new DomainFailure(
            FailureCode.PolicyDenied,
            "No local administrator credential is available."));
    }

    return await services.GetRequiredService<ISecretStore>()
        .MaterializeAsync(administrator.ClientCredentialReference, cancellationToken);
}

static async Task<DomainResult<char[]>> ReadProviderCredentialAsync(
    bool readFromStandardInput,
    CancellationToken cancellationToken)
{
    const int maximumCredentialLength = 16_384;
    if (readFromStandardInput && !Console.IsInputRedirected)
    {
        return DomainResult.Fail<char[]>(new DomainFailure(
            FailureCode.ValidationFailure,
            "--credential-stdin requires redirected standard input."));
    }

    if (!readFromStandardInput && Console.IsInputRedirected)
    {
        return DomainResult.Fail<char[]>(new DomainFailure(
            FailureCode.ValidationFailure,
            "--credential-prompt requires an interactive console."));
    }

    var buffer = new char[maximumCredentialLength + 1];
    var count = 0;
    var promptWritten = false;
    try
    {
        if (readFromStandardInput)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await Console.In.ReadAsync(buffer.AsMemory(count, 1), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer[count] == '\n')
                {
                    break;
                }

                if (buffer[count] != '\r')
                {
                    if (count == maximumCredentialLength)
                    {
                        return DomainResult.Fail<char[]>(new DomainFailure(
                            FailureCode.ValidationFailure,
                            $"Provider credential exceeds {maximumCredentialLength} characters."));
                    }

                    count++;
                }
            }
        }
        else
        {
            await Console.Error.WriteAsync("Provider credential: ");
            promptWritten = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key is ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key is ConsoleKey.Backspace)
                {
                    if (count > 0)
                    {
                        buffer[--count] = '\0';
                    }

                    continue;
                }

                if (char.IsControl(key.KeyChar))
                {
                    continue;
                }

                if (count == maximumCredentialLength)
                {
                    return DomainResult.Fail<char[]>(new DomainFailure(
                        FailureCode.ValidationFailure,
                        $"Provider credential exceeds {maximumCredentialLength} characters."));
                }

                buffer[count++] = key.KeyChar;
            }
        }

        return count == 0
            ? DomainResult.Fail<char[]>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider credential cannot be empty."))
            : DomainResult.Success(buffer.AsSpan(0, count).ToArray());
    }
    finally
    {
        Array.Clear(buffer);
        if (promptWritten)
        {
            await Console.Error.WriteLineAsync();
        }
    }
}

static async Task<int> RunCancellableMaintenanceAsync(
    string operation,
    Func<CancellationToken, Task<int>> action)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        return await action(cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync($"{operation} was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return await WriteFailureAsync(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            $"{operation} storage could not be initialized or updated.",
            IsRetryable: true));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static bool TryNormalizeDataDirectory(string value, out string normalized)
{
    try
    {
        normalized = Path.GetFullPath(value);
        return true;
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        normalized = string.Empty;
        return false;
    }
}

static async Task<int> WriteFailureAsync(DomainFailure failure)
{
    await WriteJsonAsync(new
    {
        succeeded = false,
        failure = new
        {
            code = failure.Code.ToString(),
            failure.Message,
            failure.IsRetryable,
        },
    });
    return failure.Code is FailureCode.ConcurrencyConflict ? 3 : 1;
}

static Task WriteEffectiveAgentAsync(
    EffectiveAgentDefinition definition,
    string? agentId,
    bool created) => WriteJsonAsync(new
    {
        succeeded = true,
        created,
        agentId,
        name = definition.Agent.Name,
        provider = definition.ProviderName,
        definition.Model,
        dataLocality = definition.Agent.ModelPolicy.DataLocality.ToString(),
        memoryScope = definition.Agent.MemoryPolicy.Scope.ToString(),
        networkPosture = definition.Agent.CapabilityPolicy.NetworkPosture.ToString(),
        learningMode = definition.Agent.LearningPolicy.Mode.ToString(),
        budget = definition.Agent.Budget,
        childLimits = definition.Agent.ChildLimits,
        capabilities = definition.Capabilities.Select(item => new
        {
            id = item.CapabilityId,
            decision = item.Decision.ToString(),
            item.Reason,
        }),
    });

static async Task<string[]> ReadInteractiveArgumentsAsync()
{
    static async Task<string> PromptAsync(string prompt)
    {
        await Console.Error.WriteAsync(prompt);
        return await Console.In.ReadLineAsync() ?? string.Empty;
    }

    var dataDirectory = await PromptAsync("Data directory: ");
    var actor = await PromptAsync("Operator actor ID: ");
    var correlation = await PromptAsync("Correlation ID: ");
    var installationId = await PromptAsync("Installation ID (optional GUID): ");
    var collected = new List<string>
    {
        "--data-directory", dataDirectory,
        "--actor", actor,
        "--correlation", correlation,
    };
    if (!string.IsNullOrWhiteSpace(installationId))
    {
        collected.Add("--installation-id");
        collected.Add(installationId);
    }

    return [.. collected];
}

static bool TryParseBeginOptions(
    string[] arguments,
    out SetupBeginOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--actor",
        "--correlation",
        "--data-directory",
        "--installation-id",
    };

    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown setup option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    InstallationId? installationId = null;
    if (values.TryGetValue("--installation-id", out var requestedId))
    {
        if (!Guid.TryParseExact(requestedId, "D", out var parsedId) || parsedId == Guid.Empty)
        {
            error = "--installation-id must be a non-empty GUID in D format.";
            return false;
        }

        installationId = new InstallationId(parsedId);
    }

    options = new SetupBeginOptions(dataDirectory, actorId, correlationId, installationId);
    return true;
}

static bool TryParseProviderConfigureOptions(
    string[] arguments,
    out SetupProviderConfigureOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(
        ["--actor", "--correlation", "--data-directory", "--endpoint", "--model", "--name", "--type"],
        StringComparer.Ordinal);
    var credentialMode = string.Empty;
    for (var index = 0; index < arguments.Length; index++)
    {
        var name = arguments[index];
        if (name is "--credential-stdin" or "--credential-prompt")
        {
            if (!string.IsNullOrEmpty(credentialMode))
            {
                error = "Specify exactly one credential input mode.";
                return false;
            }

            credentialMode = name;
            continue;
        }

        if (!allowed.Contains(name))
        {
            error = $"Unknown provider option '{name}'.";
            return false;
        }

        if (++index >= arguments.Length)
        {
            error = $"Option '{name}' requires a value.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--name", out var providerName, out error) ||
        !Require(values, "--type", out var providerType, out error) ||
        !Require(values, "--endpoint", out var endpointText, out error) ||
        !Require(values, "--model", out var model, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
    {
        error = "--endpoint must be an absolute URI.";
        return false;
    }

    if (string.IsNullOrEmpty(credentialMode))
    {
        error = "Specify --credential-stdin or --credential-prompt; credentials are never accepted as arguments.";
        return false;
    }

    options = new SetupProviderConfigureOptions(
        dataDirectory,
        providerName,
        providerType,
        endpoint,
        model,
        credentialMode == "--credential-stdin",
        actorId,
        correlationId);
    return true;
}

static bool TryParseProviderEditOptions(
    string[] arguments,
    bool apply,
    out SetupProviderEditOptions? options,
    out string? error)
{
    options = null;
    if (!TryParseExactOptions(
        arguments,
        [
            "--actor", "--correlation", "--data-directory", "--endpoint", "--expected-installation-version",
            "--expected-provider-version", "--model", "--name", "--preview-hash", "--provider-id", "--type",
        ],
        out var values,
        out error) ||
        !Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--provider-id", out var providerIdText, out error) ||
        !Require(values, "--expected-installation-version", out var installationVersionText, out error) ||
        !Require(values, "--expected-provider-version", out var providerVersionText, out error) ||
        !Require(values, "--name", out var providerName, out error) ||
        !Require(values, "--type", out var providerType, out error) ||
        !Require(values, "--endpoint", out var endpointText, out error) ||
        !Require(values, "--model", out var model, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    if (!Guid.TryParseExact(providerIdText, "D", out var providerId) || providerId == Guid.Empty)
    {
        error = "--provider-id must be a non-empty GUID in D format.";
        return false;
    }

    if (!TryNonNegativeVersion(installationVersionText, "--expected-installation-version", out var installationVersion, out error) ||
        !TryNonNegativeVersion(providerVersionText, "--expected-provider-version", out var providerVersion, out error))
    {
        return false;
    }

    if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
    {
        error = "--endpoint must be an absolute URI.";
        return false;
    }

    var previewHash = values.GetValueOrDefault("--preview-hash");
    if (apply && string.IsNullOrWhiteSpace(previewHash))
    {
        error = "Required option '--preview-hash' is missing or empty.";
        return false;
    }

    if (!apply && previewHash is not null)
    {
        error = "--preview-hash is valid only when applying an edit.";
        return false;
    }

    options = new SetupProviderEditOptions(
        dataDirectory,
        new ProviderProfileId(providerId),
        installationVersion,
        providerVersion,
        providerName,
        providerType,
        endpoint,
        model,
        previewHash,
        actorId,
        correlationId);
    error = null;
    return true;
}

static bool TryParseAgentEditOptions(
    string[] arguments,
    bool apply,
    out SetupAgentEditOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var editValues = new Dictionary<string, string>(StringComparer.Ordinal);
    var agentArguments = new List<string>();
    var editOptions = new HashSet<string>(
        ["--agent-id", "--expected-agent-version", "--expected-installation-version", "--preview-hash"],
        StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        var value = arguments[index + 1];
        if (editOptions.Contains(name))
        {
            if (!editValues.TryAdd(name, value))
            {
                error = $"Option '{name}' may be specified only once.";
                return false;
            }
        }
        else
        {
            agentArguments.Add(name);
            agentArguments.Add(value);
        }
    }

    if (!Require(editValues, "--agent-id", out var agentIdText, out error) ||
        !Require(editValues, "--expected-installation-version", out var installationVersionText, out error) ||
        !Require(editValues, "--expected-agent-version", out var agentVersionText, out error) ||
        !TryParseAgentOptions([.. agentArguments], out var agentOptions, out error))
    {
        return false;
    }

    if (!Guid.TryParseExact(agentIdText, "D", out var agentId) || agentId == Guid.Empty)
    {
        error = "--agent-id must be a non-empty GUID in D format.";
        return false;
    }

    if (!TryNonNegativeVersion(installationVersionText, "--expected-installation-version", out var installationVersion, out error) ||
        !TryNonNegativeVersion(agentVersionText, "--expected-agent-version", out var agentVersion, out error))
    {
        return false;
    }

    var previewHash = editValues.GetValueOrDefault("--preview-hash");
    if (apply && string.IsNullOrWhiteSpace(previewHash))
    {
        error = "Required option '--preview-hash' is missing or empty.";
        return false;
    }

    if (!apply && previewHash is not null)
    {
        error = "--preview-hash is valid only when applying an edit.";
        return false;
    }

    options = new SetupAgentEditOptions(
        agentOptions!,
        new AgentIdentityId(agentId),
        installationVersion,
        agentVersion,
        previewHash);
    error = null;
    return true;
}

static bool TryParseAgentOptions(
    string[] arguments,
    out SetupAgentOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--actor", "--allow-fallback", "--correlation", "--data-directory", "--data-locality",
        "--expertise", "--language", "--learning-mode", "--max-child-concurrency", "--max-child-depth",
        "--max-child-tokens", "--max-children", "--max-input-tokens", "--max-output-tokens",
        "--max-tool-invocations", "--max-turns", "--max-wall-seconds", "--memory-retention-days",
        "--memory-scope", "--mission", "--mutable-skill-scope", "--name", "--network-posture",
        "--provider-id", "--style", "--timezone", "--workspace",
    };
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown agent option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--name", out var agentName, out error) ||
        !Require(values, "--provider-id", out var providerIdText, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    if (!Guid.TryParseExact(providerIdText, "D", out var providerId) || providerId == Guid.Empty)
    {
        error = "--provider-id must be a non-empty GUID in D format.";
        return false;
    }

    if (!TryEnum(values, "--data-locality", ModelDataLocality.LocalOnly, out var dataLocality, out error) ||
        !TryEnum(values, "--memory-scope", AgentMemoryScope.Agent, out var memoryScope, out error) ||
        !TryEnum(values, "--network-posture", NetworkPosture.Denied, out var networkPosture, out error) ||
        !TryEnum(values, "--learning-mode", LearningMode.Propose, out var learningMode, out error) ||
        !TryInt(values, "--memory-retention-days", memoryScope is AgentMemoryScope.Task ? 0 : 30, out var retentionDays, out error) ||
        !TryInt(values, "--max-turns", 64, out var maxTurns, out error) ||
        !TryInt(values, "--max-tool-invocations", 0, out var maxTools, out error) ||
        !TryLong(values, "--max-input-tokens", 16_000, out var maxInputTokens, out error) ||
        !TryLong(values, "--max-output-tokens", 4_000, out var maxOutputTokens, out error) ||
        !TryInt(values, "--max-wall-seconds", 3600, out var maxWallSeconds, out error) ||
        !TryInt(values, "--max-child-depth", 0, out var maxChildDepth, out error) ||
        !TryInt(values, "--max-children", 0, out var maxChildren, out error) ||
        !TryInt(values, "--max-child-concurrency", 0, out var maxChildConcurrency, out error) ||
        !TryLong(values, "--max-child-tokens", 0, out var maxChildTokens, out error))
    {
        return false;
    }

    var defaultMutableScope = learningMode switch
    {
        LearningMode.Off or LearningMode.Observe => MutableSkillScope.None,
        LearningMode.Propose => MutableSkillScope.ProposalWorkspaceOnly,
        LearningMode.ScopedAuto => MutableSkillScope.ApprovedSkillClasses,
        _ => MutableSkillScope.None,
    };
    if (!TryEnum(values, "--mutable-skill-scope", defaultMutableScope, out var mutableSkillScope, out error))
    {
        return false;
    }

    var allowFallback = false;
    if (values.TryGetValue("--allow-fallback", out var allowFallbackText) &&
        !bool.TryParse(allowFallbackText, out allowFallback))
    {
        error = "--allow-fallback must be true or false.";
        return false;
    }

    options = new SetupAgentOptions(
        dataDirectory,
        agentName,
        values.GetValueOrDefault("--expertise"),
        values.GetValueOrDefault("--mission"),
        values.GetValueOrDefault("--language", "en"),
        values.GetValueOrDefault("--timezone", "UTC"),
        values.GetValueOrDefault("--style", "Concise"),
        values.GetValueOrDefault("--workspace"),
        new ProviderProfileId(providerId),
        dataLocality,
        allowFallback,
        memoryScope,
        retentionDays,
        networkPosture,
        maxTurns,
        maxTools,
        maxInputTokens,
        maxOutputTokens,
        maxWallSeconds,
        maxChildDepth,
        maxChildren,
        maxChildConcurrency,
        maxChildTokens,
        learningMode,
        mutableSkillScope,
        actorId,
        correlationId);
    return true;
}

static bool TryParseCompleteOptions(
    string[] arguments,
    out SetupCompleteOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(["--actor", "--correlation", "--data-directory"], StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown setup completion option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    options = new SetupCompleteOptions(dataDirectory, actorId, correlationId);
    return true;
}

static bool TryParseDoctorOptions(
    string[] arguments,
    out SetupDoctorOptions? options,
    out string? error)
{
    options = null;
    if (!TryParseExactOptions(
        arguments,
        ["--actor", "--correlation", "--data-directory"],
        out var values,
        out error) ||
        !Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    options = new SetupDoctorOptions(dataDirectory, actorId, correlationId);
    return true;
}

static bool TryParseMaintenanceOptions(
    string[] arguments,
    bool requireReason,
    out SetupMaintenanceOptions? options,
    out string? error)
{
    options = null;
    if (!TryParseExactOptions(
        arguments,
        ["--actor", "--correlation", "--data-directory", "--expected-version", "--reason"],
        out var values,
        out error) ||
        !Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error) ||
        !Require(values, "--expected-version", out var expectedVersionText, out error))
    {
        return false;
    }

    if (!long.TryParse(
        expectedVersionText,
        System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture,
        out var expectedVersion))
    {
        error = "--expected-version must be a non-negative integer.";
        return false;
    }

    var reason = values.GetValueOrDefault("--reason");
    if (requireReason && string.IsNullOrWhiteSpace(reason))
    {
        error = "Required option '--reason' is missing or empty.";
        return false;
    }

    if (!requireReason && reason is not null)
    {
        error = "--reason is valid only when entering recovery.";
        return false;
    }

    options = new SetupMaintenanceOptions(
        dataDirectory,
        expectedVersion,
        reason,
        actorId,
        correlationId);
    error = null;
    return true;
}

static bool TryParseExactOptions(
    string[] arguments,
    IReadOnlyCollection<string> allowedOptions,
    out Dictionary<string, string> values,
    out string? error)
{
    values = new Dictionary<string, string>(StringComparer.Ordinal);
    error = null;
    var allowed = new HashSet<string>(allowedOptions, StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    return true;
}

static bool TryInt(
    IReadOnlyDictionary<string, string> values,
    string name,
    int defaultValue,
    out int value,
    out string? error)
{
    if (!values.TryGetValue(name, out var text))
    {
        value = defaultValue;
        error = null;
        return true;
    }

    if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
    {
        error = null;
        return true;
    }

    error = $"{name} must be a non-negative integer.";
    return false;
}

static bool TryLong(
    IReadOnlyDictionary<string, string> values,
    string name,
    long defaultValue,
    out long value,
    out string? error)
{
    if (!values.TryGetValue(name, out var text))
    {
        value = defaultValue;
        error = null;
        return true;
    }

    if (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
    {
        error = null;
        return true;
    }

    error = $"{name} must be a non-negative integer.";
    return false;
}

static bool TryNonNegativeVersion(
    string text,
    string optionName,
    out long value,
    out string? error)
{
    if (long.TryParse(
        text,
        System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture,
        out value))
    {
        error = null;
        return true;
    }

    error = $"{optionName} must be a non-negative integer.";
    return false;
}

static bool TryEnum<T>(
    IReadOnlyDictionary<string, string> values,
    string name,
    T defaultValue,
    out T value,
    out string? error)
    where T : struct, Enum
{
    if (!values.TryGetValue(name, out var text))
    {
        value = defaultValue;
        error = null;
        return true;
    }

    if (Enum.TryParse<T>(text, ignoreCase: true, out value) && Enum.IsDefined(value))
    {
        error = null;
        return true;
    }

    error = $"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.";
    return false;
}

static bool Require(
    IReadOnlyDictionary<string, string> values,
    string name,
    out string value,
    out string? error)
{
    if (!values.TryGetValue(name, out value!) || string.IsNullOrWhiteSpace(value))
    {
        error = $"Required option '{name}' is missing or empty.";
        return false;
    }

    error = null;
    return true;
}

static Task WriteJsonAsync(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(
    value,
    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static void PrintHelp()
{
    Console.WriteLine("AgentForge CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  agentforge status");
    Console.WriteLine("  agentforge setup status");
    Console.WriteLine("  agentforge setup begin --data-directory <path> --actor <id> --correlation <id> [--installation-id <guid>]");
    Console.WriteLine("  agentforge setup begin --interactive");
    Console.WriteLine("  agentforge setup provider configure --data-directory <path> --name <name> --type <type> --endpoint <uri> --model <model> (--credential-stdin | --credential-prompt) --actor <id> --correlation <id>");
    Console.WriteLine("  agentforge setup provider edit preview --data-directory <path> --provider-id <guid> --expected-installation-version <n> --expected-provider-version <n> --name <name> --type <type> --endpoint <uri> --model <model> --actor <id> --correlation <id>");
    Console.WriteLine("  agentforge setup provider edit apply <same-options> --preview-hash <sha256>");
    Console.WriteLine("  agentforge setup agent preview --data-directory <path> --name <name> --provider-id <guid> --actor <id> --correlation <id> [policy options]");
    Console.WriteLine("  agentforge setup agent create --data-directory <path> --name <name> --provider-id <guid> --actor <id> --correlation <id> [policy options]");
    Console.WriteLine("  agentforge setup agent edit preview <agent-options> --agent-id <guid> --expected-installation-version <n> --expected-agent-version <n>");
    Console.WriteLine("  agentforge setup agent edit apply <same-options> --preview-hash <sha256>");
    Console.WriteLine("  agentforge setup complete --data-directory <path> --actor <id> --correlation <id>");
    Console.WriteLine("  agentforge doctor --data-directory <path> --actor <id> --correlation <id>");
    Console.WriteLine("  agentforge setup export --data-directory <path> --expected-version <n> --actor <id> --correlation <id>");
    Console.WriteLine("  agentforge setup recovery enter --data-directory <path> --expected-version <n> --reason <text> --actor <id> --correlation <id>");
    Console.WriteLine("  agentforge setup recovery resume --data-directory <path> --expected-version <n> --actor <id> --correlation <id>");
}

internal sealed record SetupBeginOptions(
    string DataDirectory,
    string ActorId,
    string CorrelationId,
    InstallationId? InstallationId);

internal sealed record SetupProviderConfigureOptions(
    string DataDirectory,
    string Name,
    string ProviderType,
    Uri Endpoint,
    string Model,
    bool ReadFromStandardInput,
    string ActorId,
    string CorrelationId);

internal sealed record SetupProviderEditOptions(
    string DataDirectory,
    ProviderProfileId ProviderProfileId,
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    string Name,
    string ProviderType,
    Uri Endpoint,
    string Model,
    string? PreviewHash,
    string ActorId,
    string CorrelationId);

internal sealed record SetupAgentOptions(
    string DataDirectory,
    string Name,
    string? Expertise,
    string? Mission,
    string Language,
    string TimeZone,
    string Style,
    string? Workspace,
    ProviderProfileId ProviderId,
    ModelDataLocality DataLocality,
    bool AllowFallback,
    AgentMemoryScope MemoryScope,
    int MemoryRetentionDays,
    NetworkPosture NetworkPosture,
    int MaxTurns,
    int MaxToolInvocations,
    long MaxInputTokens,
    long MaxOutputTokens,
    int MaxWallClockSeconds,
    int MaxChildDepth,
    int MaxChildren,
    int MaxChildConcurrency,
    long MaxChildTotalTokens,
    LearningMode LearningMode,
    MutableSkillScope MutableSkillScope,
    string ActorId,
    string CorrelationId);

internal sealed record SetupAgentEditOptions(
    SetupAgentOptions Agent,
    AgentIdentityId AgentIdentityId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    string? PreviewHash);

internal sealed record SetupCompleteOptions(
    string DataDirectory,
    string ActorId,
    string CorrelationId);

internal sealed record SetupDoctorOptions(
    string DataDirectory,
    string ActorId,
    string CorrelationId);

internal sealed record SetupMaintenanceOptions(
    string DataDirectory,
    long ExpectedVersion,
    string? Reason,
    string ActorId,
    string CorrelationId);
