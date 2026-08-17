using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;
using AgentForge.Domain.Skills;
using AgentForge.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.EndToEndTests;

public sealed class WebSetupWizardTests : IDisposable
{
    private static readonly string[] PassingCritiqueFindings = ["bounded-authority-reviewed"];
    private static readonly string[] LocalDocsProvider = ["local-docs"];
    private static readonly string[] BraveProvider = ["brave"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-web-setup-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public WebSetupWizardTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AgentForge:Installation:DataDirectory"] = _directory,
                    ["AgentForge:Host:Urls"] = string.Empty,
                    ["AgentForge:Host:RequestsPerMinute"] = "1000",
                    ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore, WebFakeSecretStore>();
                services.RemoveAll<IModelCatalogDiscoveryService>();
                services.AddSingleton<IModelCatalogDiscoveryService, WebFakeModelDiscovery>();
                services.RemoveAll<ILocalModelInteractionService>();
                services.AddSingleton<ILocalModelInteractionService, WebFakeLocalInteraction>();
                services.RemoveAll<IBraveSearchConnectivityProbe>();
                services.AddSingleton<IBraveSearchConnectivityProbe, WebFakeBraveSearchProbe>();
                services.AddSingleton<ISearchProvider>(new DeterministicSearchProvider(
                    "local-docs",
                    [new SearchProviderHit(
                        "local-docs",
                        1,
                        new Uri("https://docs.example.test/agentforge"),
                        "AgentForge local reference",
                        "A cited deterministic reference for the Ready run context.",
                        DateTimeOffset.UnixEpoch)],
                    kind: SearchProviderKind.Local));
            });
        });
    }

    [Fact]
    public async Task Loopback_wizard_hides_bootstrap_security_discovers_models_resumes_and_completes_shared_setup()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = true,
        });
        using var nonceResponse = await client.GetAsync("/api/v1/setup/web/nonce");
        Assert.Equal(HttpStatusCode.NotFound, nonceResponse.StatusCode);
        using var sessionResponse = await client.PostAsJsonAsync("/api/v1/setup/web/session", new { });
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var csrf = await ReadPropertyAsync(sessionResponse, "csrfToken");
        Assert.Equal("1", await ReadRawPropertyAsync(sessionResponse, "currentStep"));

        using var missingCsrf = await client.PostAsync("/api/v1/setup/web/begin", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, missingCsrf.StatusCode);
        var concurrentBegins = await Task.WhenAll(
            MutationAsync(client, "/api/v1/setup/web/begin", "begin-1", csrf, JsonContent.Create(new { })),
            MutationAsync(client, "/api/v1/setup/web/begin", "begin-1", csrf, JsonContent.Create(new { })));
        using var begin = concurrentBegins[0];
        using var beginReplay = concurrentBegins[1];
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, beginReplay.StatusCode);
        Assert.Equal(await begin.Content.ReadAsStringAsync(), await beginReplay.Content.ReadAsStringAsync());

        using var discovery = await MutationAsync(client, "/api/v1/setup/web/provider/discover", "provider-discover-1", csrf, JsonContent.Create(new
        {
            name = "primary",
            providerType = "openai-compatible",
            endpoint = "http://192.168.1.89:8000/v1",
        }));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        using var models = await MutationAsync(client, "/api/v1/setup/web/provider/models", "models-1", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Contains("qwen3.6", await models.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var resumed = await client.GetAsync("/api/v1/setup/web/session");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal("true", await ReadRawPropertyAsync(resumed, "resumed"));
        Assert.Equal("2", await ReadRawPropertyAsync(resumed, "currentStep"));

        using var selected = await MutationAsync(client, "/api/v1/setup/web/provider/select", "model-select-1", csrf, JsonContent.Create(new
        {
            name = "primary",
            providerType = "openai-compatible",
            endpoint = "http://192.168.1.89:8000/v1",
            model = "qwen3.6",
        }));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var tested = await MutationAsync(client, "/api/v1/setup/web/provider/test", "model-test-1", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        using var credential = await MutationAsync(client, "/api/v1/setup/web/provider/credential", "credential-1", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, credential.StatusCode);

        var agent = new
        {
            name = "web-agent",
            expertise = "safe automation",
            mission = "test shared setup",
            preferredLanguage = "en",
            timeZone = "UTC",
            responseStyle = "concise",
            defaultWorkspace = (string?)null,
        };
        using var preview = await MutationAsync(client, "/api/v1/setup/web/agent/preview", "preview-1", csrf, JsonContent.Create(agent));
        Assert.True(preview.StatusCode == HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        using var create = await MutationAsync(client, "/api/v1/setup/web/agent", "agent-1", csrf, JsonContent.Create(agent));
        Assert.True(create.StatusCode == HttpStatusCode.OK, await create.Content.ReadAsStringAsync());
        using var complete = await MutationAsync(client, "/api/v1/setup/web/complete", "complete-1", csrf, JsonContent.Create(new { }));
        Assert.True(complete.StatusCode == HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        Assert.Equal("Ready", await ReadPropertyAsync(complete, "state"));
        using var completeDocument = JsonDocument.Parse(await complete.Content.ReadAsByteArrayAsync());
        var installationId = new InstallationId(completeDocument.RootElement.GetProperty("installationId").GetGuid());
        using var completeReplay = await MutationAsync(client, "/api/v1/setup/web/complete", "complete-1", csrf, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, completeReplay.StatusCode);

        using var completedSession = await MutationAsync(client, "/api/v1/setup/web/begin", "after-complete", csrf, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Conflict, completedSession.StatusCode);
        using var completedSessionRead = await client.GetAsync("/api/v1/setup/web/session");
        Assert.Equal(HttpStatusCode.Conflict, completedSessionRead.StatusCode);
        using var completedSessionCreate = await client.PostAsJsonAsync("/api/v1/setup/web/session", new { });
        Assert.Equal(HttpStatusCode.Conflict, completedSessionCreate.StatusCode);
        using var status = await client.GetAsync("/api/v1/setup/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("\"ready\":true", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var foreignSessionRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new { }),
        };
        foreignSessionRequest.Headers.Add("Origin", "https://attacker.example");
        using var foreignSession = await client.SendAsync(foreignSessionRequest);
        Assert.Equal(HttpStatusCode.Forbidden, foreignSession.StatusCode);
        using var adminSession = await client.PostAsJsonAsync("/api/v1/admin/session", new { });
        Assert.Equal(HttpStatusCode.OK, adminSession.StatusCode);
        Assert.DoesNotContain("secret-", await adminSession.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var adminCsrf = await ReadPropertyAsync(adminSession, "csrfToken");
        using var appShell = await client.GetAsync("/");
        var appShellHtml = await appShell.Content.ReadAsStringAsync();
        Assert.Contains("id=\"refresh-after-setup\" class=\"setup-submit\" href=\"#overview\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"run-search\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"run-model-summary\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"run-token-limit\" type=\"number\" min=\"1\" max=\"262144\" step=\"1\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"#schedules\" data-view=\"schedules\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"schedule-form\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"#context\" data-view=\"context\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"memory-create-form\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"brave-config-form\" class=\"run-composer compact-composer integration-form\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"brave-config-key\" type=\"password\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"research-form\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"run-memory-query\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("class=\"gate-check\"><input id=\"memory-correction\" type=\"checkbox\"", appShellHtml, StringComparison.Ordinal);
        using var styleSheet = await client.GetAsync("/styles.css");
        var styleSheetCss = await styleSheet.Content.ReadAsStringAsync();
        Assert.Contains(".run-composer[hidden] { display: none; }", styleSheetCss, StringComparison.Ordinal);
        Assert.Contains(".compact-composer > .run-primary-grid", styleSheetCss, StringComparison.Ordinal);
        Assert.Contains(".preview-hash { display: block; min-width: 0; max-width: 100%;", styleSheetCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", styleSheetCss, StringComparison.Ordinal);
        Assert.Contains("href=\"#learning\" data-view=\"learning\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"learning-form\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"learning-proposal-form\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"learning-candidate-list\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"agent-editor\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"agent-discover-models\"", appShellHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"agent-edit-max-output\"", appShellHtml, StringComparison.Ordinal);
        using var agentList = await client.GetAsync("/api/v1/admin/agents");
        Assert.Equal(HttpStatusCode.OK, agentList.StatusCode);
        using var agentListDocument = JsonDocument.Parse(await agentList.Content.ReadAsByteArrayAsync());
        var agentId = agentListDocument.RootElement.GetProperty("agents")[0].GetProperty("id").GetGuid();
        Assert.Equal("web-agent", agentListDocument.RootElement.GetProperty("agents")[0].GetProperty("name").GetString());
        using var editDetails = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/edit");
        Assert.Equal(HttpStatusCode.OK, editDetails.StatusCode);
        using var editDocument = JsonDocument.Parse(await editDetails.Content.ReadAsByteArrayAsync());
        var installationVersion = editDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var providerVersion = editDocument.RootElement.GetProperty("provider").GetProperty("version").GetInt64();
        var agentVersion = editDocument.RootElement.GetProperty("agent").GetProperty("version").GetInt64();

        var profileCandidate = new
        {
            expectedInstallationVersion = installationVersion,
            expectedAgentVersion = agentVersion,
            name = "web-agent",
            expertise = "safe automation",
            mission = "test governed ready edit",
            preferredLanguage = "en",
            timeZone = "UTC",
            responseStyle = "evidence-backed",
            defaultWorkspace = (string?)null,
        };
        using var staleProfilePreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/profile/preview",
            "profile-preview-stale",
            adminCsrf,
            JsonContent.Create(profileCandidate));
        Assert.Equal(HttpStatusCode.OK, staleProfilePreview.StatusCode);
        var staleProfileHash = await ReadPropertyAsync(staleProfilePreview, "previewHash");

        using var discoveredModels = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/models/discover",
            "agent-models-1",
            adminCsrf,
            JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, discoveredModels.StatusCode);
        Assert.Contains("qwen3.8", await discoveredModels.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var modelPreviewWithoutCsrf = await client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{agentId:D}/model/preview",
            new { expectedInstallationVersion = installationVersion, expectedProviderVersion = providerVersion, model = "qwen3.8" });
        Assert.Equal(HttpStatusCode.Unauthorized, modelPreviewWithoutCsrf.StatusCode);
        using var modelPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/model/preview",
            "model-preview-1",
            adminCsrf,
            JsonContent.Create(new { expectedInstallationVersion = installationVersion, expectedProviderVersion = providerVersion, model = "qwen3.8" }));
        Assert.Equal(HttpStatusCode.OK, modelPreview.StatusCode);
        Assert.Contains("provider.model", await modelPreview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var modelPreviewHash = await ReadPropertyAsync(modelPreview, "previewHash");
        using var rejectedModelApply = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/model/apply",
            "model-apply-rejected",
            adminCsrf,
            JsonContent.Create(new { previewHash = "sha256:not-approved" }));
        Assert.Equal(HttpStatusCode.Forbidden, rejectedModelApply.StatusCode);
        using var modelApply = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/model/apply",
            "model-apply-1",
            adminCsrf,
            JsonContent.Create(new { previewHash = modelPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, modelApply.StatusCode);
        Assert.Contains("qwen3.8", await modelApply.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var modelApplyReplay = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/model/apply",
            "model-apply-1",
            adminCsrf,
            JsonContent.Create(new { previewHash = modelPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, modelApplyReplay.StatusCode);
        Assert.True(modelApplyReplay.Headers.Contains("Idempotent-Replay"));

        using var staleProfileApply = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/profile/apply",
            "profile-apply-stale",
            adminCsrf,
            JsonContent.Create(new { previewHash = staleProfileHash }));
        Assert.Equal(HttpStatusCode.Conflict, staleProfileApply.StatusCode);

        using var refreshedEdit = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/edit");
        using var refreshedEditDocument = JsonDocument.Parse(await refreshedEdit.Content.ReadAsByteArrayAsync());
        var refreshedProfileCandidate = new
        {
            expectedInstallationVersion = refreshedEditDocument.RootElement.GetProperty("installationVersion").GetInt64(),
            expectedAgentVersion = refreshedEditDocument.RootElement.GetProperty("agent").GetProperty("version").GetInt64(),
            profileCandidate.name,
            profileCandidate.expertise,
            profileCandidate.mission,
            profileCandidate.preferredLanguage,
            profileCandidate.timeZone,
            profileCandidate.responseStyle,
            profileCandidate.defaultWorkspace,
            maxOutputTokens = 32_768,
        };
        using var profilePreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/profile/preview",
            "profile-preview-2",
            adminCsrf,
            JsonContent.Create(refreshedProfileCandidate));
        Assert.Equal(HttpStatusCode.OK, profilePreview.StatusCode);
        Assert.Contains("agent.mission", await profilePreview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("agent.budget", await profilePreview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var profilePreviewHash = await ReadPropertyAsync(profilePreview, "previewHash");
        using var profileApply = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/profile/apply",
            "profile-apply-2",
            adminCsrf,
            JsonContent.Create(new { previewHash = profilePreviewHash }));
        Assert.Equal(HttpStatusCode.OK, profileApply.StatusCode);

        using var runOptions = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/run-options");
        Assert.Equal(HttpStatusCode.OK, runOptions.StatusCode);
        using var runOptionsDocument = JsonDocument.Parse(await runOptions.Content.ReadAsByteArrayAsync());
        Assert.Equal("qwen3.8", runOptionsDocument.RootElement.GetProperty("provider").GetProperty("model").GetString());
        Assert.Equal("Denied", runOptionsDocument.RootElement.GetProperty("restrictions").GetProperty("tools").GetString());
        Assert.Equal("detailed", runOptionsDocument.RootElement.GetProperty("responseDepths")[2].GetProperty("id").GetString());
        Assert.Equal(8_192, runOptionsDocument.RootElement.GetProperty("responseDepths")[2]
            .GetProperty("maximumOutputTokens").GetInt32());
        Assert.Equal("extended", runOptionsDocument.RootElement.GetProperty("responseDepths")[3]
            .GetProperty("id").GetString());
        Assert.Equal("maximum", runOptionsDocument.RootElement.GetProperty("responseDepths")[4]
            .GetProperty("id").GetString());
        Assert.Equal(32_768, runOptionsDocument.RootElement.GetProperty("maximumOutputTokens").GetInt32());

        using var scheduleEdit = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/edit");
        using var scheduleEditDocument = JsonDocument.Parse(await scheduleEdit.Content.ReadAsByteArrayAsync());
        var scheduleRequest = new
        {
            expectedInstallationVersion = scheduleEditDocument.RootElement.GetProperty("installationVersion").GetInt64(),
            expectedAgentVersion = scheduleEditDocument.RootElement.GetProperty("agent").GetProperty("version").GetInt64(),
            expectedProviderVersion = scheduleEditDocument.RootElement.GetProperty("provider").GetProperty("version").GetInt64(),
            agentId,
            name = "Scheduled local verification",
            prompt = "Run the scheduled local verification.",
            runInstructions = "Return one bounded sentence.",
            responseDepth = "balanced",
            maximumOutputTokens = 2_048,
            skillIds = Array.Empty<string>(),
            triggerKind = "OneShot",
            oneShotAt = DateTimeOffset.UtcNow.AddHours(1),
            intervalSeconds = (int?)null,
            cronExpression = (string?)null,
            calendarHour = (int?)null,
            calendarMinute = (int?)null,
            calendarDays = Array.Empty<string>(),
            calendarDayOfMonth = (int?)null,
            timeZoneId = "UTC",
            misfirePolicy = "Skip",
            overlapPolicy = "Skip",
            misfireGraceSeconds = 60,
            maximumCatchUp = 1,
            maximumParallelRuns = 1,
            maximumJitterSeconds = 0,
            maximumAttempts = 2,
            retryDelaySeconds = 0,
            maximumConsecutiveFailures = 3,
            expiresAt = (DateTimeOffset?)null,
        };
        using var schedulePreview = await MutationAsync(
            client, "/api/v1/admin/schedules/preview", "schedule-preview-1", adminCsrf,
            JsonContent.Create(scheduleRequest));
        Assert.Equal(HttpStatusCode.OK, schedulePreview.StatusCode);
        using var schedulePreviewDocument = JsonDocument.Parse(await schedulePreview.Content.ReadAsByteArrayAsync());
        var schedulePreviewHash = schedulePreviewDocument.RootElement.GetProperty("previewHash").GetString();
        var scheduleId = schedulePreviewDocument.RootElement.GetProperty("scheduleId").GetGuid();
        using var scheduleApply = await MutationAsync(
            client, "/api/v1/admin/schedules/apply", "schedule-apply-1", adminCsrf,
            JsonContent.Create(new { previewHash = schedulePreviewHash, schedule = scheduleRequest }));
        var scheduleApplyBody = await scheduleApply.Content.ReadAsByteArrayAsync();
        Assert.True(scheduleApply.StatusCode == HttpStatusCode.OK,
            $"Schedule apply failed: {System.Text.Encoding.UTF8.GetString(scheduleApplyBody)}");
        using var scheduleApplyDocument = JsonDocument.Parse(scheduleApplyBody);
        var scheduleVersion = scheduleApplyDocument.RootElement.GetProperty("schedule").GetProperty("version").GetInt64();
        using var scheduleApplyReplay = await MutationAsync(
            client, "/api/v1/admin/schedules/apply", "schedule-apply-1", adminCsrf,
            JsonContent.Create(new { previewHash = schedulePreviewHash, schedule = scheduleRequest }));
        Assert.Equal(HttpStatusCode.OK, scheduleApplyReplay.StatusCode);
        Assert.True(scheduleApplyReplay.Headers.Contains("Idempotent-Replay"));
        using var scheduleRunNow = await MutationAsync(
            client, $"/api/v1/admin/schedules/{scheduleId:D}/run-now", "schedule-run-now-1", adminCsrf,
            JsonContent.Create(new { expectedVersion = scheduleVersion }));
        Assert.Equal(HttpStatusCode.OK, scheduleRunNow.StatusCode);
        using var scheduleRunNowReplay = await MutationAsync(
            client, $"/api/v1/admin/schedules/{scheduleId:D}/run-now", "schedule-run-now-1", adminCsrf,
            JsonContent.Create(new { expectedVersion = scheduleVersion }));
        Assert.Equal(HttpStatusCode.OK, scheduleRunNowReplay.StatusCode);
        Assert.True(scheduleRunNowReplay.Headers.Contains("Idempotent-Replay"));
        var scheduledCompleted = false;
        for (var attempt = 0; attempt < 50 && !scheduledCompleted; attempt++)
        {
            await Task.Delay(100);
            await using var scheduledScope = _factory.Services.CreateAsyncScope();
            var scheduled = await scheduledScope.ServiceProvider.GetRequiredService<IScheduleSnapshotStore>()
                .FindLatestAsync(new AgentForge.Domain.Scheduling.ScheduleId(scheduleId), CancellationToken.None);
            scheduledCompleted = scheduled?.CompletedCount == 1;
        }
        Assert.True(scheduledCompleted, "The hosted worker did not complete the durable scheduled local-model run.");
        using var scheduledRuns = await client.GetAsync("/api/v1/admin/runs");
        Assert.Contains("Scheduled local verification", await scheduledRuns.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var memoryCreate = await MutationAsync(
            client, "/api/v1/admin/memory", "memory-create-1", adminCsrf,
            JsonContent.Create(new
            {
                expectedAgentVersion = scheduleRequest.expectedAgentVersion,
                agentId,
                kind = "User",
                content = "Operator preference: include a verification summary.",
                isCorrection = false,
                retentionDays = 30,
            }));
        Assert.Equal(HttpStatusCode.Created, memoryCreate.StatusCode);
        using var memoryCreateDocument = JsonDocument.Parse(await memoryCreate.Content.ReadAsByteArrayAsync());
        var memoryId = memoryCreateDocument.RootElement.GetProperty("memory").GetProperty("id").GetGuid();
        using var memoryCreateReplay = await MutationAsync(
            client, "/api/v1/admin/memory", "memory-create-1", adminCsrf,
            JsonContent.Create(new
            {
                expectedAgentVersion = scheduleRequest.expectedAgentVersion,
                agentId,
                kind = "User",
                content = "Operator preference: include a verification summary.",
                isCorrection = false,
                retentionDays = 30,
            }));
        Assert.Equal(HttpStatusCode.OK, memoryCreateReplay.StatusCode);
        Assert.True(memoryCreateReplay.Headers.Contains("Idempotent-Replay"));
        using var memorySearch = await client.GetAsync(
            $"/api/v1/admin/memory?agentId={agentId:D}&query=verification%20summary&maximumResults=8");
        Assert.Equal(HttpStatusCode.OK, memorySearch.StatusCode);
        Assert.Contains("Operator preference", await memorySearch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var braveStatus = await client.GetAsync("/api/v1/admin/research/providers/brave/configuration");
        Assert.Equal(HttpStatusCode.OK, braveStatus.StatusCode);
        Assert.Contains("\"configured\":false", await braveStatus.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var bravePreview = await MutationAsync(
            client, "/api/v1/admin/research/providers/brave/configuration/preview", "brave-preview-1", adminCsrf,
            JsonContent.Create(new
            {
                expectedVersion = (long?)null,
                isEnabled = true,
                safeSearch = "Strict",
                countryCode = "UA",
                searchLanguage = "en",
                apiKey = "fixture-brave-key-one",
            }));
        Assert.Equal(HttpStatusCode.OK, bravePreview.StatusCode);
        var bravePreviewHash = await ReadPropertyAsync(bravePreview, "previewHash");
        Assert.DoesNotContain("fixture-brave-key-one", await bravePreview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var braveApply = await MutationAsync(
            client, "/api/v1/admin/research/providers/brave/configuration/apply", "brave-apply-1", adminCsrf,
            JsonContent.Create(new { previewHash = bravePreviewHash, apiKey = "fixture-brave-key-one" }));
        Assert.Equal(HttpStatusCode.OK, braveApply.StatusCode);
        Assert.Contains("\"version\":0", await braveApply.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var staleBraveResearchPreview = await MutationAsync(
            client, "/api/v1/admin/research/preview", "brave-research-preview-stale", adminCsrf,
            JsonContent.Create(new
            {
                expectedAgentVersion = scheduleRequest.expectedAgentVersion,
                agentId,
                query = "AgentForge Brave configuration pinning",
                maximumResults = 3,
                providerIds = BraveProvider,
            }));
        Assert.Equal(HttpStatusCode.OK, staleBraveResearchPreview.StatusCode);
        var staleBraveResearchHash = await ReadPropertyAsync(staleBraveResearchPreview, "previewHash");
        using var braveRotationPreview = await MutationAsync(
            client, "/api/v1/admin/research/providers/brave/configuration/preview", "brave-preview-2", adminCsrf,
            JsonContent.Create(new
            {
                expectedVersion = 0,
                isEnabled = true,
                safeSearch = "Moderate",
                countryCode = "UA",
                searchLanguage = "en",
                apiKey = "fixture-brave-key-two",
            }));
        Assert.Equal(HttpStatusCode.OK, braveRotationPreview.StatusCode);
        var braveRotationHash = await ReadPropertyAsync(braveRotationPreview, "previewHash");
        using var braveRotation = await MutationAsync(
            client, "/api/v1/admin/research/providers/brave/configuration/apply", "brave-apply-2", adminCsrf,
            JsonContent.Create(new { previewHash = braveRotationHash, apiKey = "fixture-brave-key-two" }));
        Assert.Equal(HttpStatusCode.OK, braveRotation.StatusCode);
        Assert.Contains("\"version\":1", await braveRotation.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var staleBraveResearchApply = await MutationAsync(
            client, "/api/v1/admin/research/apply", "brave-research-apply-stale", adminCsrf,
            JsonContent.Create(new { previewHash = staleBraveResearchHash }));
        Assert.Equal(HttpStatusCode.Conflict, staleBraveResearchApply.StatusCode);

        using var researchProviders = await client.GetAsync("/api/v1/admin/research/providers");
        Assert.Contains("local-docs", await researchProviders.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("brave", await researchProviders.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var researchPreview = await MutationAsync(
            client, "/api/v1/admin/research/preview", "research-preview-1", adminCsrf,
            JsonContent.Create(new
            {
                expectedAgentVersion = scheduleRequest.expectedAgentVersion,
                agentId,
                query = "AgentForge local reference",
                maximumResults = 5,
                providerIds = LocalDocsProvider,
            }));
        Assert.Equal(HttpStatusCode.OK, researchPreview.StatusCode);
        var researchPreviewHash = await ReadPropertyAsync(researchPreview, "previewHash");
        using var researchApply = await MutationAsync(
            client, "/api/v1/admin/research/apply", "research-apply-1", adminCsrf,
            JsonContent.Create(new { previewHash = researchPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, researchApply.StatusCode);
        var researchReceiptHash = await ReadPropertyAsync(researchApply, "receiptHash");
        using var researchApplyReplay = await MutationAsync(
            client, "/api/v1/admin/research/apply", "research-apply-1", adminCsrf,
            JsonContent.Create(new { previewHash = researchPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, researchApplyReplay.StatusCode);
        Assert.True(researchApplyReplay.Headers.Contains("Idempotent-Replay"));
        using var contextRun = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-context-run",
            adminCsrf,
            "Use the explicitly attached context.",
            memoryQuery: "verification summary",
            researchReceiptHash: researchReceiptHash);
        var contextRunText = await contextRun.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, contextRun.StatusCode);
        Assert.Contains("\"memoryCount\":1", contextRunText, StringComparison.Ordinal);
        Assert.Contains("\"citationCount\":1", contextRunText, StringComparison.Ordinal);
        Assert.Contains("event: completed", contextRunText, StringComparison.Ordinal);
        using var memoryDelete = await MutationAsync(
            client, $"/api/v1/admin/memory/{memoryId:D}/delete", "memory-delete-1", adminCsrf,
            JsonContent.Create(new { expectedAgentVersion = scheduleRequest.expectedAgentVersion, agentId }));
        Assert.Equal(HttpStatusCode.OK, memoryDelete.StatusCode);
        using var memoryDeleteReplay = await MutationAsync(
            client, $"/api/v1/admin/memory/{memoryId:D}/delete", "memory-delete-1", adminCsrf,
            JsonContent.Create(new { expectedAgentVersion = scheduleRequest.expectedAgentVersion, agentId }));
        Assert.Equal(HttpStatusCode.OK, memoryDeleteReplay.StatusCode);
        Assert.True(memoryDeleteReplay.Headers.Contains("Idempotent-Replay"));

        using var runWithoutCsrf = await client.PostAsJsonAsync("/api/v1/admin/runs", new
        {
            agentId,
            name = "MVP planned run",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, runWithoutCsrf.StatusCode);
        using var createRun = await MutationAsync(client, "/api/v1/admin/runs", "mvp-run-1", adminCsrf, JsonContent.Create(new
        {
            agentId,
            name = "MVP planned run",
        }));
        Assert.Equal(HttpStatusCode.Created, createRun.StatusCode);
        using var runDocument = JsonDocument.Parse(await createRun.Content.ReadAsByteArrayAsync());
        var runId = runDocument.RootElement.GetProperty("taskId").GetGuid();
        Assert.Equal("Planned", runDocument.RootElement.GetProperty("state").GetString());
        using var createRunReplay = await MutationAsync(client, "/api/v1/admin/runs", "mvp-run-1", adminCsrf, JsonContent.Create(new
        {
            agentId,
            name = "MVP planned run",
        }));
        Assert.Equal(HttpStatusCode.Created, createRunReplay.StatusCode);
        Assert.True(createRunReplay.Headers.Contains("Idempotent-Replay"));
        using var replayDocument = JsonDocument.Parse(await createRunReplay.Content.ReadAsByteArrayAsync());
        Assert.Equal(runId, replayDocument.RootElement.GetProperty("taskId").GetGuid());
        using var createRunConflict = await MutationAsync(client, "/api/v1/admin/runs", "mvp-run-1", adminCsrf, JsonContent.Create(new
        {
            agentId,
            name = "Different run",
        }));
        Assert.Equal(HttpStatusCode.Conflict, createRunConflict.StatusCode);
        using var runList = await client.GetAsync("/api/v1/admin/runs");
        Assert.Equal(HttpStatusCode.OK, runList.StatusCode);
        Assert.Contains("MVP planned run", await runList.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var emptyLearning = await client.GetAsync("/api/v1/admin/learning/signals");
        Assert.Equal(HttpStatusCode.OK, emptyLearning.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await emptyLearning.Content.ReadAsByteArrayAsync())
            .RootElement.GetProperty("signals").GetArrayLength());
        using var emptyCandidates = await client.GetAsync("/api/v1/admin/learning/candidates");
        Assert.Equal(HttpStatusCode.OK, emptyCandidates.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await emptyCandidates.Content.ReadAsByteArrayAsync())
            .RootElement.GetProperty("candidates").GetArrayLength());
        using var nonterminalLearning = await MutationAsync(
            client, "/api/v1/admin/learning/signals", "learning-nonterminal", adminCsrf, JsonContent.Create(new
            {
                sourceTaskId = runId,
                kind = "Correction",
                summary = "The operator corrected a detail after reviewing this run.",
                occurrenceCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Conflict, nonterminalLearning.StatusCode);
        using var cancelRun = await MutationAsync(client, $"/api/v1/admin/runs/{runId:D}/cancel", "mvp-run-cancel-1", adminCsrf, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, cancelRun.StatusCode);
        Assert.Contains("Canceled", await cancelRun.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var learningWithoutCsrf = await client.PostAsJsonAsync("/api/v1/admin/learning/signals", new
        {
            sourceTaskId = runId,
            kind = "Correction",
            summary = "A bounded correction.",
            occurrenceCount = 1,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, learningWithoutCsrf.StatusCode);
        var correctionEvidence = new
        {
            sourceTaskId = runId,
            kind = "Correction",
            summary = "  The operator corrected a detail\n after reviewing this run.  ",
            occurrenceCount = 1,
        };
        using var capturedCorrection = await MutationAsync(
            client, "/api/v1/admin/learning/signals", "learning-correction", adminCsrf,
            JsonContent.Create(correctionEvidence));
        Assert.Equal(HttpStatusCode.Created, capturedCorrection.StatusCode);
        Assert.Contains("\"action\":\"Memory\"", await capturedCorrection.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("correction-without-revision-authority", await capturedCorrection.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var capturedCorrectionDocument = JsonDocument.Parse(
            await capturedCorrection.Content.ReadAsByteArrayAsync());
        var correctionSignalId = capturedCorrectionDocument.RootElement.GetProperty("id").GetGuid();
        using var correctionReplay = await MutationAsync(
            client, "/api/v1/admin/learning/signals", "learning-correction", adminCsrf,
            JsonContent.Create(correctionEvidence));
        Assert.Equal(HttpStatusCode.OK, correctionReplay.StatusCode);
        Assert.True(correctionReplay.Headers.Contains("Idempotent-Replay"));
        using var correctionConflict = await MutationAsync(
            client, "/api/v1/admin/learning/signals", "learning-correction", adminCsrf, JsonContent.Create(new
            {
                sourceTaskId = runId,
                kind = "MissingCapability",
                summary = "This is different evidence.",
                occurrenceCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Conflict, correctionConflict.StatusCode);
        using var capturedMissingCapability = await MutationAsync(
            client, "/api/v1/admin/learning/signals", "learning-missing-capability", adminCsrf, JsonContent.Create(new
            {
                sourceTaskId = runId,
                kind = "MissingCapability",
                summary = "The run lacked a governed capability required for the requested outcome.",
                occurrenceCount = 1,
            }));
        Assert.Equal(HttpStatusCode.Created, capturedMissingCapability.StatusCode);
        Assert.Contains("\"action\":\"NewSkill\"", await capturedMissingCapability.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var capturedMissingCapabilityDocument = JsonDocument.Parse(
            await capturedMissingCapability.Content.ReadAsByteArrayAsync());
        var missingCapabilitySignalId = capturedMissingCapabilityDocument.RootElement.GetProperty("id").GetGuid();
        using var rejectedSensitiveSummary = await MutationAsync(
            client, "/api/v1/admin/learning/signals", "learning-sensitive", adminCsrf, JsonContent.Create(new
            {
                sourceTaskId = runId,
                kind = "Correction",
                summary = "The api_key value must not become learning evidence.",
                occurrenceCount = 1,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedSensitiveSummary.StatusCode);
        using var learningList = await client.GetAsync("/api/v1/admin/learning/signals");
        Assert.Equal(HttpStatusCode.OK, learningList.StatusCode);
        using var learningListDocument = JsonDocument.Parse(await learningList.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, learningListDocument.RootElement.GetProperty("signals").GetArrayLength());
        Assert.Equal(runId.ToString("D"), learningListDocument.RootElement.GetProperty("signals")[0]
            .GetProperty("sourceRunId").GetString());

        var candidateBody = new
        {
            skillId = "skill:proposal.governed-capability",
            version = "0.1.0",
            description = "A proposed bounded capability from durable learning evidence.",
            requestedPermissions = new[] { "repository:read" },
        };
        using var proposalWithoutCsrf = await client.PostAsJsonAsync(
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates", candidateBody);
        Assert.Equal(HttpStatusCode.Unauthorized, proposalWithoutCsrf.StatusCode);
        using var rejectedMemoryProposal = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{correctionSignalId:D}/candidates",
            "learning-memory-candidate",
            adminCsrf,
            JsonContent.Create(candidateBody));
        Assert.Equal(HttpStatusCode.Forbidden, rejectedMemoryProposal.StatusCode);
        using var invalidProposal = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates",
            "learning-invalid-candidate",
            adminCsrf,
            JsonContent.Create(new
            {
                skillId = "not-a-skill-id",
                candidateBody.version,
                candidateBody.description,
                candidateBody.requestedPermissions,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidProposal.StatusCode);
        using var sensitiveGeneration = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates",
            "learning-sensitive-generation",
            adminCsrf,
            JsonContent.Create(new
            {
                candidateBody.skillId,
                candidateBody.version,
                candidateBody.description,
                candidateBody.requestedPermissions,
                generationGuidance = "Use password=not-for-model in the candidate.",
            }));
        Assert.Equal(HttpStatusCode.Forbidden, sensitiveGeneration.StatusCode);
        using var malformedGeneration = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates",
            "learning-malformed-generation",
            adminCsrf,
            JsonContent.Create(new
            {
                candidateBody.skillId,
                candidateBody.version,
                candidateBody.description,
                candidateBody.requestedPermissions,
                generationGuidance = "force-malformed",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, malformedGeneration.StatusCode);
        using var proposedCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates",
            "learning-new-skill-candidate",
            adminCsrf,
            JsonContent.Create(candidateBody));
        Assert.Equal(HttpStatusCode.Created, proposedCandidate.StatusCode);
        using var proposedCandidateDocument = JsonDocument.Parse(
            await proposedCandidate.Content.ReadAsByteArrayAsync());
        Assert.Equal("Proposed", proposedCandidateDocument.RootElement.GetProperty("state").GetString());
        Assert.Equal("deterministic-verification",
            proposedCandidateDocument.RootElement.GetProperty("nextGate").GetString());
        Assert.False(proposedCandidateDocument.RootElement.GetProperty("activeAuthority").GetBoolean());
        var generation = proposedCandidateDocument.RootElement.GetProperty("generation");
        Assert.Equal("qwen3.8", generation.GetProperty("model").GetString());
        Assert.Equal(agentId, generation.GetProperty("agentId").GetGuid());
        Assert.Equal("Stop", generation.GetProperty("finishReason").GetString());
        Assert.StartsWith("sha256:", generation.GetProperty("selectedMarkdownHash").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("repository:read", proposedCandidateDocument.RootElement
            .GetProperty("requestedPermissions")[0].GetString());
        var candidateId = proposedCandidateDocument.RootElement.GetProperty("id").GetGuid();
        var roleActors = proposedCandidateDocument.RootElement.GetProperty("roles").EnumerateObject()
            .Select(role => role.Value.GetString()).ToArray();
        Assert.Equal(5, roleActors.Distinct(StringComparer.Ordinal).Count());
        using var proposalReplay = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates",
            "learning-new-skill-candidate",
            adminCsrf,
            JsonContent.Create(candidateBody));
        Assert.Equal(HttpStatusCode.OK, proposalReplay.StatusCode);
        Assert.True(proposalReplay.Headers.Contains("Idempotent-Replay"));
        using var secondProposal = await MutationAsync(
            client,
            $"/api/v1/admin/learning/signals/{missingCapabilitySignalId:D}/candidates",
            "learning-new-skill-candidate-second",
            adminCsrf,
            JsonContent.Create(candidateBody));
        Assert.Equal(HttpStatusCode.Conflict, secondProposal.StatusCode);
        using var candidateList = await client.GetAsync("/api/v1/admin/learning/candidates");
        Assert.Equal(HttpStatusCode.OK, candidateList.StatusCode);
        using var candidateListDocument = JsonDocument.Parse(await candidateList.Content.ReadAsByteArrayAsync());
        var listedCandidate = Assert.Single(candidateListDocument.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.Equal(candidateId, listedCandidate.GetProperty("id").GetGuid());
        const string evaluationHash =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var rejectedManualVerification = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-manual-verify-rejected",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "verify",
                expectedVersion = 0,
                targetPassed = true,
                holdoutPassed = true,
                adversarialPassed = true,
                permissionDiffApproved = true,
                baselineMetric = 0,
                candidateMetric = 1,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedManualVerification.StatusCode);
        var evaluationBody = new { expectedVersion = 0 };
        using var verifiedCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/evaluate",
            "learning-evaluate-candidate",
            adminCsrf,
            JsonContent.Create(evaluationBody));
        Assert.Equal(HttpStatusCode.OK, verifiedCandidate.StatusCode);
        using var verifiedDocument = JsonDocument.Parse(await verifiedCandidate.Content.ReadAsByteArrayAsync());
        Assert.Equal("Verified", verifiedDocument.RootElement.GetProperty("candidate").GetProperty("state").GetString());
        var evaluationReceipt = verifiedDocument.RootElement.GetProperty("receipt");
        Assert.Equal("agentforge-managed-isolated-v1", evaluationReceipt.GetProperty("evaluator").GetString());
        Assert.Equal(6, evaluationReceipt.GetProperty("checks").GetArrayLength());
        Assert.All(evaluationReceipt.GetProperty("checks").EnumerateArray(),
            check => Assert.True(check.GetProperty("passed").GetBoolean(), check.GetProperty("summary").GetString()));
        Assert.Equal("application/vnd.agentforge.learning-evaluation+json",
            evaluationReceipt.GetProperty("evidence").GetProperty("mediaType").GetString());
        Assert.Equal(evaluationReceipt.GetProperty("evidence").GetProperty("contentHash").GetString(),
            evaluationReceipt.GetProperty("evaluation").GetProperty("evidenceHash").GetString());
        using var verifiedReplay = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/evaluate",
            "learning-evaluate-candidate",
            adminCsrf,
            JsonContent.Create(evaluationBody));
        Assert.Equal(HttpStatusCode.OK, verifiedReplay.StatusCode);
        Assert.True(verifiedReplay.Headers.Contains("Idempotent-Replay"));
        using var staleCritique = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-stale-critique",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "critique",
                expectedVersion = 0,
                passed = true,
                findingCodes = Array.Empty<string>(),
                evidenceHash = evaluationHash,
            }));
        Assert.Equal(HttpStatusCode.Conflict, staleCritique.StatusCode);
        using var critiquedCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-critique-candidate",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "critique",
                expectedVersion = 1,
                passed = true,
                findingCodes = PassingCritiqueFindings,
                evidenceHash = evaluationHash,
            }));
        Assert.Equal(HttpStatusCode.OK, critiquedCandidate.StatusCode);
        Assert.Contains("\"state\":\"Critiqued\"", await critiquedCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var approvedCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-approve-candidate",
            adminCsrf,
            JsonContent.Create(new { action = "approve", expectedVersion = 2 }));
        Assert.Equal(HttpStatusCode.OK, approvedCandidate.StatusCode);
        Assert.Contains("\"state\":\"Approved\"", await approvedCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var canaryCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-start-canary",
            adminCsrf,
            JsonContent.Create(new { action = "start-canary", expectedVersion = 3 }));
        Assert.Equal(HttpStatusCode.OK, canaryCandidate.StatusCode);
        Assert.Contains("\"state\":\"Canary\"", await canaryCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var promotedCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-finish-canary",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "finish-canary",
                expectedVersion = 4,
                passed = true,
                baselineMetric = 0,
                candidateMetric = 1,
                evidenceHash = evaluationHash,
            }));
        Assert.Equal(HttpStatusCode.OK, promotedCandidate.StatusCode);
        Assert.Contains("\"state\":\"Promoted\"", await promotedCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("\"activeAuthority\":true", await promotedCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var rolledBackCandidate = await MutationAsync(
            client,
            $"/api/v1/admin/learning/candidates/{candidateId:D}/transition",
            "learning-rollback-candidate",
            adminCsrf,
            JsonContent.Create(new { action = "rollback", expectedVersion = 5, evidenceHash = evaluationHash }));
        Assert.Equal(HttpStatusCode.OK, rolledBackCandidate.StatusCode);
        Assert.Contains("\"state\":\"RolledBack\"", await rolledBackCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("\"activeAuthority\":false", await rolledBackCandidate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var chat = await MutationAsync(client, $"/api/v1/admin/agents/{agentId:D}/test-chat", "mvp-chat-1", adminCsrf, JsonContent.Create(new
        {
            prompt = "Describe your role.",
        }));
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Contains("I am the bounded AgentForge test agent.", await chat.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Completed", await chat.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var chatReplay = await MutationAsync(client, $"/api/v1/admin/agents/{agentId:D}/test-chat", "mvp-chat-1", adminCsrf, JsonContent.Create(new
        {
            prompt = "Describe your role.",
        }));
        Assert.Equal(HttpStatusCode.OK, chatReplay.StatusCode);
        Assert.True(chatReplay.Headers.Contains("Idempotent-Replay"));
        using var chatConflict = await MutationAsync(client, $"/api/v1/admin/agents/{agentId:D}/test-chat", "mvp-chat-1", adminCsrf, JsonContent.Create(new
        {
            prompt = "Use a different prompt.",
        }));
        Assert.Equal(HttpStatusCode.Conflict, chatConflict.StatusCode);

        using var streamed = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-1",
            adminCsrf,
            "Stream a bounded answer.",
            name: "Configured local answer",
            runInstructions: "Use a short verification list.",
            responseDepth: "detailed",
            maximumOutputTokens: 12_000);
        var streamedText = await streamed.Content.ReadAsStringAsync();
        Assert.True(streamed.StatusCode == HttpStatusCode.OK, streamedText);
        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: run-started", streamedText, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Configured local answer\"", streamedText, StringComparison.Ordinal);
        Assert.Contains("\"responseDepth\":\"detailed\"", streamedText, StringComparison.Ordinal);
        Assert.Contains("\"maximumOutputTokens\":12000", streamedText, StringComparison.Ordinal);
        Assert.Contains("event: output-delta", streamedText, StringComparison.Ordinal);
        Assert.Contains("event: usage", streamedText, StringComparison.Ordinal);
        Assert.Contains("event: completed", streamedText, StringComparison.Ordinal);
        Assert.Contains("I am the bounded AgentForge test agent.", streamedText, StringComparison.Ordinal);
        var conversationStartedLine = streamedText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("data: ", StringComparison.Ordinal) &&
                line.Contains("\"conversationId\"", StringComparison.Ordinal));
        using var conversationStarted = JsonDocument.Parse(conversationStartedLine[6..]);
        var conversationId = conversationStarted.RootElement.GetProperty("conversationId").GetGuid();
        using var conversationDetails = await client.GetAsync($"/api/v1/admin/runs/{conversationId:D}");
        Assert.Equal(HttpStatusCode.OK, conversationDetails.StatusCode);
        using (var detailsJson = JsonDocument.Parse(await conversationDetails.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal(1, detailsJson.RootElement.GetProperty("turns").GetArrayLength());
            Assert.Equal("Stream a bounded answer.", detailsJson.RootElement.GetProperty("turns")[0]
                .GetProperty("prompt").GetString());
            Assert.Equal("I am the bounded AgentForge test agent.", detailsJson.RootElement
                .GetProperty("turns")[0].GetProperty("response").GetString());
        }
        using var followUpWithoutCsrf = await client.PostAsJsonAsync(
            $"/api/v1/admin/runs/{conversationId:D}/turns-stream",
            new { prompt = "Unsafe missing-CSRF follow-up.", responseDepth = "balanced", maximumOutputTokens = 2_048 });
        Assert.Equal(HttpStatusCode.Unauthorized, followUpWithoutCsrf.StatusCode);
        using var followUp = await StreamMutationAsync(
            client,
            $"/api/v1/admin/runs/{conversationId:D}/turns-stream",
            "mvp-conversation-turn-2",
            adminCsrf,
            "Use the first answer and add one check.",
            responseDepth: "balanced",
            maximumOutputTokens: 2_048);
        var followUpText = await followUp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
        Assert.Contains("\"turn\":2", followUpText, StringComparison.Ordinal);
        Assert.Contains("event: completed", followUpText, StringComparison.Ordinal);
        using var continuedDetails = await client.GetAsync($"/api/v1/admin/runs/{conversationId:D}");
        using (var detailsJson = JsonDocument.Parse(await continuedDetails.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal(2, detailsJson.RootElement.GetProperty("turns").GetArrayLength());
            Assert.Equal("Use the first answer and add one check.", detailsJson.RootElement
                .GetProperty("turns")[1].GetProperty("prompt").GetString());
        }
        using var interruptedStream = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-resume",
            adminCsrf,
            "Interrupt once.");
        var interruptedText = await interruptedStream.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, interruptedStream.StatusCode);
        Assert.Contains("event: failed", interruptedText, StringComparison.Ordinal);
        Assert.Contains("\"resumable\":true", interruptedText, StringComparison.Ordinal);
        var interruptedStartedLine = interruptedText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("data: ", StringComparison.Ordinal) &&
                line.Contains("\"conversationId\"", StringComparison.Ordinal));
        using var interruptedStarted = JsonDocument.Parse(interruptedStartedLine[6..]);
        var interruptedConversationId = interruptedStarted.RootElement.GetProperty("conversationId").GetGuid();
        var interruptedTurnId = interruptedStarted.RootElement.GetProperty("turnId").GetGuid();
        using var resumedStream = await StreamMutationAsync(
            client,
            $"/api/v1/admin/runs/{interruptedConversationId:D}/turns/{interruptedTurnId:D}/resume-stream",
            "mvp-stream-resume-attempt",
            adminCsrf,
            "Body ignored by resume endpoint.");
        var resumedText = await resumedStream.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resumedStream.StatusCode);
        Assert.Contains("\"resumed\":true", resumedText, StringComparison.Ordinal);
        Assert.Contains("event: completed", resumedText, StringComparison.Ordinal);
        using var resumedDetails = await client.GetAsync($"/api/v1/admin/runs/{interruptedConversationId:D}");
        using (var resumedJson = JsonDocument.Parse(await resumedDetails.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal("Completed", resumedJson.RootElement.GetProperty("run").GetProperty("state").GetString());
            Assert.Equal("I am the bounded AgentForge test agent.", resumedJson.RootElement
                .GetProperty("turns")[0].GetProperty("response").GetString());
        }
        using var streamReplay = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-1",
            adminCsrf,
            "Stream a bounded answer.",
            name: "Configured local answer",
            runInstructions: "Use a short verification list.",
            responseDepth: "detailed",
            maximumOutputTokens: 12_000);
        Assert.Equal(HttpStatusCode.Conflict, streamReplay.StatusCode);
        Assert.Contains("fresh idempotency key", await streamReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var excessiveOutput = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-output-over-budget",
            adminCsrf,
            "Attempt an excessive output budget.",
            responseDepth: "maximum",
            maximumOutputTokens: 40_000);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excessiveOutput.StatusCode);
        Assert.Contains("agent ceiling of 32,768", await excessiveOutput.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var cancelStream = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-cancel",
            adminCsrf,
            "Wait for cancellation.",
            completion: HttpCompletionOption.ResponseHeadersRead);
        await using var cancelBody = await cancelStream.Content.ReadAsStreamAsync();
        using var cancelReader = new StreamReader(cancelBody);
        var startedEvent = await ReadSseEventAsync(cancelReader);
        Assert.Contains("event: run-started", startedEvent, StringComparison.Ordinal);
        using var startedJson = JsonDocument.Parse(startedEvent.Split("data: ", StringSplitOptions.None)[1]);
        var activeTaskId = startedJson.RootElement.GetProperty("taskId").GetGuid();
        using var cancelActive = await MutationAsync(
            client,
            $"/api/v1/admin/runs/{activeTaskId:D}/cancel",
            "mvp-stream-cancel-request",
            adminCsrf,
            JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, cancelActive.StatusCode);
        Assert.Contains("Canceled", await cancelActive.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var canceledTail = await cancelReader.ReadToEndAsync();
        Assert.Contains("event: canceled", canceledTail, StringComparison.Ordinal);

        using var installSkill = await MutationAsync(client, "/api/v1/admin/skills/seed/csharp-review/install", "seed-skill-1", adminCsrf, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, installSkill.StatusCode);
        Assert.Contains("skill:csharp.review", await installSkill.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var skillList = await client.GetAsync("/api/v1/admin/skills");
        Assert.Equal(HttpStatusCode.OK, skillList.StatusCode);
        Assert.Contains("Installed", await skillList.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var skillRunOptions = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/run-options");
        using var skillRunOptionsDocument = JsonDocument.Parse(await skillRunOptions.Content.ReadAsByteArrayAsync());
        var installedSkillOption = skillRunOptionsDocument.RootElement.GetProperty("skills").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "skill:csharp.review");
        Assert.Equal("skill:csharp.review", installedSkillOption.GetProperty("id").GetString());
        Assert.False(installedSkillOption.GetProperty("selectable").GetBoolean());
        Assert.DoesNotContain(skillRunOptionsDocument.RootElement.GetProperty("skills").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "skill:proposal.governed-capability");
        using var deniedSkillStream = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-denied-skill",
            adminCsrf,
            "Use the ungranted skill.",
            skillIds: ["skill:csharp.review"]);
        Assert.Equal(HttpStatusCode.Forbidden, deniedSkillStream.StatusCode);
        Assert.Contains("must already be granted", await deniedSkillStream.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var proposedActivation = await MutationAsync(
            client,
            "/api/v1/admin/skills/proposals",
            "seed-skill-proposal",
            adminCsrf,
            JsonContent.Create(new { skillId = "skill:csharp.review", version = "1.0.0" }));
        Assert.Equal(HttpStatusCode.Created, proposedActivation.StatusCode);
        using var proposedActivationDocument = JsonDocument.Parse(await proposedActivation.Content.ReadAsByteArrayAsync());
        var activationId = proposedActivationDocument.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Proposed", proposedActivationDocument.RootElement.GetProperty("state").GetString());
        using var duplicateActivation = await MutationAsync(
            client,
            "/api/v1/admin/skills/proposals",
            "seed-skill-duplicate-proposal",
            adminCsrf,
            JsonContent.Create(new { skillId = "skill:csharp.review", version = "1.0.0" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicateActivation.StatusCode);
        using var missingEvaluationEvidence = await MutationAsync(
            client,
            $"/api/v1/admin/skills/proposals/{activationId:D}/transition",
            "seed-skill-evaluation-missing-evidence",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "evaluate",
                expectedVersion = 0,
                targetPassed = true,
                holdoutPassed = true,
                adversarialPassed = true,
                baselineMetric = 0,
                candidateMetric = 1,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, missingEvaluationEvidence.StatusCode);
        using var evaluatedActivation = await MutationAsync(
            client,
            $"/api/v1/admin/skills/proposals/{activationId:D}/transition",
            "seed-skill-evaluation",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "evaluate",
                expectedVersion = 0,
                targetPassed = true,
                holdoutPassed = true,
                adversarialPassed = true,
                baselineMetric = 0,
                candidateMetric = 1,
                evidenceHash = evaluationHash,
            }));
        Assert.Equal(HttpStatusCode.OK, evaluatedActivation.StatusCode);
        Assert.Contains("AwaitingApproval", await evaluatedActivation.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var staleActivationApproval = await MutationAsync(
            client,
            $"/api/v1/admin/skills/proposals/{activationId:D}/transition",
            "seed-skill-stale-approval",
            adminCsrf,
            JsonContent.Create(new { action = "approve", expectedVersion = 0 }));
        Assert.Equal(HttpStatusCode.Conflict, staleActivationApproval.StatusCode);
        using var approvedActivation = await MutationAsync(
            client,
            $"/api/v1/admin/skills/proposals/{activationId:D}/transition",
            "seed-skill-approval",
            adminCsrf,
            JsonContent.Create(new { action = "approve", expectedVersion = 1 }));
        Assert.Equal(HttpStatusCode.OK, approvedActivation.StatusCode);
        Assert.Contains("Approved", await approvedActivation.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var activationCanary = await MutationAsync(
            client,
            $"/api/v1/admin/skills/proposals/{activationId:D}/transition",
            "seed-skill-start-canary",
            adminCsrf,
            JsonContent.Create(new { action = "start-canary", expectedVersion = 2 }));
        Assert.Equal(HttpStatusCode.OK, activationCanary.StatusCode);
        using var promotedActivation = await MutationAsync(
            client,
            $"/api/v1/admin/skills/proposals/{activationId:D}/transition",
            "seed-skill-finish-canary",
            adminCsrf,
            JsonContent.Create(new
            {
                action = "finish-canary",
                expectedVersion = 3,
                passed = true,
                baselineMetric = 0,
                candidateMetric = 1,
                evidenceHash = evaluationHash,
            }));
        Assert.Equal(HttpStatusCode.OK, promotedActivation.StatusCode);
        Assert.Contains("\"state\":\"Promoted\"", await promotedActivation.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var promotedButUngrantedOptions = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/run-options");
        using var promotedButUngrantedDocument = JsonDocument.Parse(await promotedButUngrantedOptions.Content.ReadAsByteArrayAsync());
        var promotedButUngrantedSkill = promotedButUngrantedDocument.RootElement.GetProperty("skills").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "skill:csharp.review");
        Assert.Equal("Active", promotedButUngrantedSkill.GetProperty("status").GetString());
        Assert.False(promotedButUngrantedSkill.GetProperty("granted").GetBoolean());
        Assert.False(promotedButUngrantedSkill.GetProperty("selectable").GetBoolean());

        using var promotedSkills = await client.GetAsync("/api/v1/admin/skills");
        using var promotedSkillsDocument = JsonDocument.Parse(await promotedSkills.Content.ReadAsByteArrayAsync());
        var grantInstallationVersion = promotedSkillsDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var grantAgentVersion = promotedSkillsDocument.RootElement.GetProperty("agents")[0].GetProperty("version").GetInt64();
        using var missingGrantCsrfRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/admin/agents/{agentId:D}/skill-grants/preview")
        {
            Content = JsonContent.Create(new
            {
                expectedInstallationVersion = grantInstallationVersion,
                expectedAgentVersion = grantAgentVersion,
                skillId = "skill:csharp.review",
                grant = true,
            }),
        };
        missingGrantCsrfRequest.Headers.Add("Idempotency-Key", "seed-skill-grant-no-csrf");
        missingGrantCsrfRequest.Headers.Add("Origin", "http://localhost");
        using var missingGrantCsrf = await client.SendAsync(missingGrantCsrfRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, missingGrantCsrf.StatusCode);
        using var grantPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/preview",
            "seed-skill-grant-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = grantInstallationVersion,
                expectedAgentVersion = grantAgentVersion,
                skillId = "skill:csharp.review",
                grant = true,
            }));
        Assert.Equal(HttpStatusCode.OK, grantPreview.StatusCode);
        using var grantPreviewDocument = JsonDocument.Parse(await grantPreview.Content.ReadAsByteArrayAsync());
        var grantPreviewHash = grantPreviewDocument.RootElement.GetProperty("previewHash").GetString();
        Assert.NotNull(grantPreviewHash);
        Assert.Equal("agent.capabilityPolicy",
            grantPreviewDocument.RootElement.GetProperty("changes")[0].GetProperty("path").GetString());
        using var rejectedGrant = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/apply",
            "seed-skill-grant-wrong-preview",
            adminCsrf,
            JsonContent.Create(new { previewHash = new string('0', 64) }));
        Assert.Equal(HttpStatusCode.Forbidden, rejectedGrant.StatusCode);
        using var appliedGrant = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/apply",
            "seed-skill-grant-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = grantPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, appliedGrant.StatusCode);
        Assert.Contains("\"granted\":true", await appliedGrant.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var grantedOptions = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/run-options");
        using var grantedOptionsDocument = JsonDocument.Parse(await grantedOptions.Content.ReadAsByteArrayAsync());
        var grantedSkill = grantedOptionsDocument.RootElement.GetProperty("skills").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "skill:csharp.review");
        Assert.Equal("Active", grantedSkill.GetProperty("status").GetString());
        Assert.True(grantedSkill.GetProperty("granted").GetBoolean());
        Assert.True(grantedSkill.GetProperty("selectable").GetBoolean());
        using var skilledStream = await StreamMutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/test-chat-stream",
            "mvp-stream-granted-skill",
            adminCsrf,
            "Review a short C# method.",
            skillIds: ["skill:csharp.review"]);
        Assert.Equal(HttpStatusCode.OK, skilledStream.StatusCode);
        var skilledStreamText = await skilledStream.Content.ReadAsStringAsync();
        Assert.Contains("event: completed", skilledStreamText, StringComparison.Ordinal);
        Assert.Contains("skill:csharp.review", skilledStreamText, StringComparison.Ordinal);

        using var grantedSkills = await client.GetAsync("/api/v1/admin/skills");
        using var grantedSkillsDocument = JsonDocument.Parse(await grantedSkills.Content.ReadAsByteArrayAsync());
        var revokeInstallationVersion = grantedSkillsDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var revokeAgentVersion = grantedSkillsDocument.RootElement.GetProperty("agents")[0].GetProperty("version").GetInt64();
        using var revokePreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/preview",
            "seed-skill-revoke-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = revokeInstallationVersion,
                expectedAgentVersion = revokeAgentVersion,
                skillId = "skill:csharp.review",
                grant = false,
            }));
        Assert.Equal(HttpStatusCode.OK, revokePreview.StatusCode);
        using var revokePreviewDocument = JsonDocument.Parse(await revokePreview.Content.ReadAsByteArrayAsync());
        var revokePreviewHash = revokePreviewDocument.RootElement.GetProperty("previewHash").GetString();
        using var appliedRevoke = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/apply",
            "seed-skill-revoke-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = revokePreviewHash }));
        Assert.Equal(HttpStatusCode.OK, appliedRevoke.StatusCode);
        Assert.Contains("\"granted\":false", await appliedRevoke.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var revokedOptions = await client.GetAsync($"/api/v1/admin/agents/{agentId:D}/run-options");
        using var revokedOptionsDocument = JsonDocument.Parse(await revokedOptions.Content.ReadAsByteArrayAsync());
        var revokedSkill = revokedOptionsDocument.RootElement.GetProperty("skills").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "skill:csharp.review");
        Assert.False(revokedSkill.GetProperty("granted").GetBoolean());
        Assert.False(revokedSkill.GetProperty("selectable").GetBoolean());

        using var revokedSkills = await client.GetAsync("/api/v1/admin/skills");
        using var revokedSkillsDocument = JsonDocument.Parse(await revokedSkills.Content.ReadAsByteArrayAsync());
        var regrantInstallationVersion = revokedSkillsDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var regrantAgentVersion = revokedSkillsDocument.RootElement.GetProperty("agents")[0].GetProperty("version").GetInt64();
        using var regrantPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/preview",
            "seed-skill-regrant-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = regrantInstallationVersion,
                expectedAgentVersion = regrantAgentVersion,
                skillId = "skill:csharp.review",
                grant = true,
            }));
        Assert.Equal(HttpStatusCode.OK, regrantPreview.StatusCode);
        using var regrantPreviewDocument = JsonDocument.Parse(await regrantPreview.Content.ReadAsByteArrayAsync());
        var regrantPreviewHash = regrantPreviewDocument.RootElement.GetProperty("previewHash").GetString();
        using var appliedRegrant = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/skill-grants/apply",
            "seed-skill-regrant-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = regrantPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, appliedRegrant.StatusCode);
        Assert.Contains("\"granted\":true", await appliedRegrant.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var toolWorkspace = Path.GetFullPath(_directory);
        var toolTextPath = Path.Combine(toolWorkspace, "tool-smoke.txt");
        await File.WriteAllTextAsync(toolTextPath, "bounded workspace evidence");
        using var initialTools = await client.GetAsync("/api/v1/admin/tools");
        Assert.Equal(HttpStatusCode.OK, initialTools.StatusCode);
        using var initialToolsDocument = JsonDocument.Parse(await initialTools.Content.ReadAsByteArrayAsync());
        var initialToolIds = initialToolsDocument.RootElement.GetProperty("tools").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(4, initialToolIds.Length);
        Assert.Contains("tool:workspace.list", initialToolIds);
        Assert.Contains("tool:workspace.read-text", initialToolIds);
        Assert.Contains("tool:search.brave", initialToolIds);
        Assert.Contains("tool:http-api.get", initialToolIds);
        var toolInstallationVersion = initialToolsDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var toolAgentVersion = initialToolsDocument.RootElement.GetProperty("agents")[0].GetProperty("version").GetInt64();

        using var invalidToolDisposition = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/preview",
            "workspace-list-invalid-disposition",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = toolInstallationVersion,
                expectedAgentVersion = toolAgentVersion,
                toolId = "tool:workspace.list",
                toolVersion = "1.0.0",
                workspace = toolWorkspace,
                parameters = new { directory = toolWorkspace, maximumEntries = 20 },
                disposition = "maybe",
                approvalSeconds = 300,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidToolDisposition.StatusCode);

        using var deniedBeforeGrant = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/preview",
            "workspace-list-before-grant",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = toolInstallationVersion,
                expectedAgentVersion = toolAgentVersion,
                toolId = "tool:workspace.list",
                toolVersion = "1.0.0",
                workspace = toolWorkspace,
                parameters = new { directory = toolWorkspace, maximumEntries = 20 },
                disposition = "grant",
                approvalSeconds = 300,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, deniedBeforeGrant.StatusCode);

        using var toolGrantPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-grants/preview",
            "workspace-read-grant-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = toolInstallationVersion,
                expectedAgentVersion = toolAgentVersion,
                capabilityId = "tool:workspace.read",
                grant = true,
                maximumToolInvocations = 10,
            }));
        Assert.Equal(HttpStatusCode.OK, toolGrantPreview.StatusCode);
        using var toolGrantPreviewDocument = JsonDocument.Parse(await toolGrantPreview.Content.ReadAsByteArrayAsync());
        var toolGrantPreviewHash = toolGrantPreviewDocument.RootElement.GetProperty("previewHash").GetString();
        Assert.Equal(2, toolGrantPreviewDocument.RootElement.GetProperty("changes").GetArrayLength());
        using var toolGrant = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-grants/apply",
            "workspace-read-grant-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = toolGrantPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, toolGrant.StatusCode);
        Assert.Contains("\"maximumToolInvocations\":10", await toolGrant.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var grantedTools = await client.GetAsync("/api/v1/admin/tools");
        using var grantedToolsDocument = JsonDocument.Parse(await grantedTools.Content.ReadAsByteArrayAsync());
        var grantedToolInstallationVersion = grantedToolsDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var grantedToolAgentVersion = grantedToolsDocument.RootElement.GetProperty("agents")[0].GetProperty("version").GetInt64();
        Assert.Contains("tool:workspace.read", grantedToolsDocument.RootElement.GetProperty("agents")[0]
            .GetProperty("toolGrants").EnumerateArray().Select(item => item.GetString()));

        using var escapedTarget = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/preview",
            "workspace-list-escape",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = grantedToolInstallationVersion,
                expectedAgentVersion = grantedToolAgentVersion,
                toolId = "tool:workspace.list",
                toolVersion = "1.0.0",
                workspace = toolWorkspace,
                parameters = new { directory = Path.GetFullPath(Path.GetTempPath()), maximumEntries = 20 },
                disposition = "grant",
                approvalSeconds = 300,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, escapedTarget.StatusCode);

        using var invocationPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/preview",
            "workspace-list-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = grantedToolInstallationVersion,
                expectedAgentVersion = grantedToolAgentVersion,
                toolId = "tool:workspace.list",
                toolVersion = "1.0.0",
                workspace = toolWorkspace,
                parameters = new { directory = toolWorkspace, maximumEntries = 20 },
                disposition = "grant",
                approvalSeconds = 300,
            }));
        Assert.Equal(HttpStatusCode.OK, invocationPreview.StatusCode);
        using var invocationPreviewDocument = JsonDocument.Parse(await invocationPreview.Content.ReadAsByteArrayAsync());
        var invocationPreviewHash = invocationPreviewDocument.RootElement.GetProperty("previewHash").GetString();
        Assert.Equal("RequireApproval",
            invocationPreviewDocument.RootElement.GetProperty("policy").GetProperty("decision").GetString());
        Assert.Equal("BuiltIn", invocationPreviewDocument.RootElement.GetProperty("tool").GetProperty("sandbox").GetString());
        using var invocation = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/apply",
            "workspace-list-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = invocationPreviewHash }));
        Assert.Equal(HttpStatusCode.OK, invocation.StatusCode);
        var invocationBody = await invocation.Content.ReadAsStringAsync();
        Assert.Contains("tool-smoke.txt", invocationBody, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"BuiltIn\"", invocationBody, StringComparison.Ordinal);
        using var consumedPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/apply",
            "workspace-list-consumed-preview",
            adminCsrf,
            JsonContent.Create(new { previewHash = invocationPreviewHash }));
        Assert.Equal(HttpStatusCode.Forbidden, consumedPreview.StatusCode);

        using var denialPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/preview",
            "workspace-read-denial-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = grantedToolInstallationVersion,
                expectedAgentVersion = grantedToolAgentVersion,
                toolId = "tool:workspace.read-text",
                toolVersion = "1.0.0",
                workspace = toolWorkspace,
                parameters = new { path = toolTextPath, maximumBytes = 1024 },
                disposition = "deny",
                approvalSeconds = 300,
            }));
        Assert.Equal(HttpStatusCode.OK, denialPreview.StatusCode);
        using var denialPreviewDocument = JsonDocument.Parse(await denialPreview.Content.ReadAsByteArrayAsync());
        using var denial = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-invocations/apply",
            "workspace-read-denial-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = denialPreviewDocument.RootElement.GetProperty("previewHash").GetString() }));
        Assert.Equal(HttpStatusCode.OK, denial.StatusCode);
        Assert.Contains("\"executed\":false", await denial.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var finalTools = await client.GetAsync("/api/v1/admin/tools");
        using var finalToolsDocument = JsonDocument.Parse(await finalTools.Content.ReadAsByteArrayAsync());
        var toolRevokeInstallationVersion = finalToolsDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var toolRevokeAgentVersion = finalToolsDocument.RootElement.GetProperty("agents")[0].GetProperty("version").GetInt64();
        using var toolRevokePreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-grants/preview",
            "workspace-read-revoke-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = toolRevokeInstallationVersion,
                expectedAgentVersion = toolRevokeAgentVersion,
                capabilityId = "tool:workspace.read",
                grant = false,
                maximumToolInvocations = 10,
            }));
        Assert.Equal(HttpStatusCode.OK, toolRevokePreview.StatusCode);
        using var toolRevokePreviewDocument = JsonDocument.Parse(await toolRevokePreview.Content.ReadAsByteArrayAsync());
        using var toolRevoke = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{agentId:D}/tool-grants/apply",
            "workspace-read-revoke-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = toolRevokePreviewDocument.RootElement.GetProperty("previewHash").GetString() }));
        Assert.Equal(HttpStatusCode.OK, toolRevoke.StatusCode);
        Assert.Contains("\"maximumToolInvocations\":0", await toolRevoke.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var configuredAgent = Assert.Single(await verificationScope.ServiceProvider
            .GetRequiredService<IAgentIdentityRepository>().ListAsync(installationId, CancellationToken.None));
        Assert.Equal(AgentForge.Domain.Agents.AgentMemoryScope.Agent, configuredAgent.MemoryPolicy.Scope);
        Assert.Equal(30, configuredAgent.MemoryPolicy.RetentionDays);
        Assert.Equal(64, configuredAgent.Budget.MaxTurns);
        Assert.Equal(32_768, configuredAgent.Budget.MaxOutputTokens);
        Assert.Equal(0, configuredAgent.Budget.MaxToolInvocations);
        Assert.Equal(0, configuredAgent.ChildLimits.MaxChildren);
        Assert.Equal(AgentForge.Domain.Agents.NetworkPosture.Denied, configuredAgent.CapabilityPolicy.NetworkPosture);
        Assert.Equal(["skill:csharp.review"], configuredAgent.CapabilityPolicy.SkillGrants);
        Assert.Equal(AgentForge.Domain.Agents.LearningMode.Propose, configuredAgent.LearningPolicy.Mode);
        Assert.Equal("test governed ready edit", configuredAgent.Mission);
        Assert.Equal("evidence-backed", configuredAgent.ResponseStyle);
        var provider = Assert.Single(await verificationScope.ServiceProvider
            .GetRequiredService<IProviderProfileRepository>().ListAsync(installationId, CancellationToken.None));
        Assert.Equal("qwen3.8", provider.Model);
        Assert.True(provider.SecretReference.IsNoCredential);
        var learningCandidate = Assert.Single(await verificationScope.ServiceProvider
            .GetRequiredService<ILearningRepository>().ListCandidatesAsync(
                installationId, 10, CancellationToken.None));
        Assert.Equal(candidateId, learningCandidate.Id.Value);
        Assert.Equal(LearningCandidateState.RolledBack, learningCandidate.State);
        Assert.Equal("application/vnd.agentforge.learning-workspace+tar",
            learningCandidate.ProposalWorkspace.MediaType);
        await using var proposalWorkspace = await verificationScope.ServiceProvider
            .GetRequiredService<IArtifactStore>().OpenReadAsync(
                learningCandidate.ProposalWorkspace, CancellationToken.None);
        Assert.True(proposalWorkspace.Length > 0);
        var proposedSkill = await verificationScope.ServiceProvider
            .GetRequiredService<ISkillRegistryRepository>().FindAsync(
                installationId,
                new SkillId("skill:proposal.governed-capability"),
                new SkillVersion("0.1.0"),
                CancellationToken.None);
        Assert.NotNull(proposedSkill);
        Assert.Equal(SkillPackageStatus.Quarantined, proposedSkill.Status);
        Assert.Equal(SkillPackageProvenance.AgentProposal, proposedSkill.Provenance);

        using var providerCatalog = await client.GetAsync("/api/v1/admin/providers");
        Assert.Equal(HttpStatusCode.OK, providerCatalog.StatusCode);
        using var providerCatalogDocument = JsonDocument.Parse(await providerCatalog.Content.ReadAsByteArrayAsync());
        var createProviderInstallationVersion = providerCatalogDocument.RootElement
            .GetProperty("installationVersion").GetInt64();
        using var providerCreatePreview = await MutationAsync(
            client,
            "/api/v1/admin/providers/create/preview",
            "provider-create-preview",
            adminCsrf,
            JsonContent.Create(new
            {
                expectedInstallationVersion = createProviderInstallationVersion,
                name = "secondary",
                providerType = "openai-compatible",
                endpoint = "http://192.168.1.89:8000/v1",
                model = "qwen3.8",
                credential = "fixture-provider-key",
            }));
        Assert.Equal(HttpStatusCode.OK, providerCreatePreview.StatusCode);
        using var providerCreatePreviewDocument = JsonDocument.Parse(
            await providerCreatePreview.Content.ReadAsByteArrayAsync());
        var providerCreateHash = providerCreatePreviewDocument.RootElement.GetProperty("previewHash").GetString();
        Assert.Equal("OS-backed secret", providerCreatePreviewDocument.RootElement.GetProperty("authentication").GetString());
        using var wrongProviderCredential = await MutationAsync(
            client,
            "/api/v1/admin/providers/create/apply",
            "provider-create-wrong-credential",
            adminCsrf,
            JsonContent.Create(new { previewHash = providerCreateHash, credential = "wrong-fixture-key" }));
        Assert.Equal(HttpStatusCode.Forbidden, wrongProviderCredential.StatusCode);
        using var providerCreate = await MutationAsync(
            client,
            "/api/v1/admin/providers/create/apply",
            "provider-create-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = providerCreateHash, credential = "fixture-provider-key" }));
        Assert.Equal(HttpStatusCode.OK, providerCreate.StatusCode);
        using var providerCreateDocument = JsonDocument.Parse(await providerCreate.Content.ReadAsByteArrayAsync());
        var secondaryProviderId = providerCreateDocument.RootElement.GetProperty("provider").GetProperty("id").GetGuid();
        var agentCreateInstallationVersion = providerCreateDocument.RootElement.GetProperty("installationVersion").GetInt64();
        var secondaryProviderVersion = providerCreateDocument.RootElement.GetProperty("provider").GetProperty("version").GetInt64();

        var createAgentPolicy = new
        {
            expectedInstallationVersion = agentCreateInstallationVersion,
            expectedProviderVersion = secondaryProviderVersion,
            name = "secondary-agent",
            expertise = "durable local execution",
            mission = "exercise complete policy administration",
            preferredLanguage = "en",
            timeZone = "UTC",
            responseStyle = "bounded",
            defaultWorkspace = (string?)null,
            primaryProviderId = secondaryProviderId,
            dataLocality = "LocalOnly",
            allowFallback = false,
            memoryScope = "Task",
            retentionDays = 0,
            networkPosture = "Denied",
            toolGrants = Array.Empty<string>(),
            skillGrants = Array.Empty<string>(),
            maxTurns = 32,
            maxToolInvocations = 0,
            maxInputTokens = 16_000,
            maxOutputTokens = 8_192,
            maxWallClockSeconds = 270,
            maxChildDepth = 0,
            maxChildren = 0,
            maxChildConcurrency = 0,
            maxChildTokens = 0,
            learningMode = "Off",
            mutableSkillScope = "None",
        };
        using var agentCreatePreview = await MutationAsync(
            client,
            "/api/v1/admin/agents/create/preview",
            "agent-create-preview",
            adminCsrf,
            JsonContent.Create(createAgentPolicy));
        Assert.Equal(HttpStatusCode.OK, agentCreatePreview.StatusCode);
        using var agentCreatePreviewDocument = JsonDocument.Parse(
            await agentCreatePreview.Content.ReadAsByteArrayAsync());
        var agentCreateHash = agentCreatePreviewDocument.RootElement.GetProperty("previewHash").GetString();
        using var agentCreate = await MutationAsync(
            client,
            "/api/v1/admin/agents/create/apply",
            "agent-create-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = agentCreateHash }));
        Assert.Equal(HttpStatusCode.OK, agentCreate.StatusCode);
        using var agentCreateDocument = JsonDocument.Parse(await agentCreate.Content.ReadAsByteArrayAsync());
        var secondaryAgentId = agentCreateDocument.RootElement.GetProperty("agent").GetProperty("id").GetGuid();
        var editInstallationVersion = agentCreateDocument.RootElement.GetProperty("installationVersion").GetInt64();

        using var createdAgentEdit = await client.GetAsync($"/api/v1/admin/agents/{secondaryAgentId:D}/edit");
        Assert.Equal(HttpStatusCode.OK, createdAgentEdit.StatusCode);
        using var createdAgentEditDocument = JsonDocument.Parse(await createdAgentEdit.Content.ReadAsByteArrayAsync());
        var createdAgentVersion = createdAgentEditDocument.RootElement.GetProperty("agent").GetProperty("version").GetInt64();
        var fullPolicyEdit = new
        {
            expectedInstallationVersion = editInstallationVersion,
            expectedAgentVersion = createdAgentVersion,
            name = "secondary-agent",
            expertise = "durable local execution",
            mission = "exercise complete governed policy editing",
            preferredLanguage = "en",
            timeZone = "UTC",
            responseStyle = "evidence-first",
            defaultWorkspace = toolWorkspace,
            maxOutputTokens = 16_384,
            primaryProviderId = secondaryProviderId,
            dataLocality = "LocalOnly",
            allowFallback = false,
            memoryScope = "Agent",
            retentionDays = 60,
            networkPosture = "LoopbackOnly",
            toolGrants = new[] { "tool:workspace.read" },
            skillGrants = new[] { "skill:csharp.review" },
            maxTurns = 48,
            maxToolInvocations = 5,
            maxInputTokens = 32_000,
            maxWallClockSeconds = 600,
            maxChildDepth = 1,
            maxChildren = 1,
            maxChildConcurrency = 1,
            maxChildTokens = 8_000,
            learningMode = "Propose",
            mutableSkillScope = "ProposalWorkspaceOnly",
            expectedPrimaryProviderVersion = secondaryProviderVersion,
        };
        using var fullPolicyPreview = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{secondaryAgentId:D}/profile/preview",
            "full-policy-preview",
            adminCsrf,
            JsonContent.Create(fullPolicyEdit));
        Assert.Equal(HttpStatusCode.OK, fullPolicyPreview.StatusCode);
        using var fullPolicyPreviewDocument = JsonDocument.Parse(await fullPolicyPreview.Content.ReadAsByteArrayAsync());
        Assert.True(fullPolicyPreviewDocument.RootElement.GetProperty("impact")
            .GetProperty("invalidatesConversationContinuation").GetBoolean());
        var fullPolicyHash = fullPolicyPreviewDocument.RootElement.GetProperty("previewHash").GetString();
        using var fullPolicyApply = await MutationAsync(
            client,
            $"/api/v1/admin/agents/{secondaryAgentId:D}/profile/apply",
            "full-policy-apply",
            adminCsrf,
            JsonContent.Create(new { previewHash = fullPolicyHash }));
        Assert.True(fullPolicyApply.StatusCode == HttpStatusCode.OK,
            $"Full policy apply failed: {await fullPolicyApply.Content.ReadAsStringAsync()}");

        await using var administrationVerification = _factory.Services.CreateAsyncScope();
        var createdAgents = await administrationVerification.ServiceProvider
            .GetRequiredService<IAgentIdentityRepository>().ListAsync(installationId, CancellationToken.None);
        Assert.Equal(2, createdAgents.Count);
        var secondaryAgent = Assert.Single(createdAgents, item => item.Id.Value == secondaryAgentId);
        Assert.Equal(AgentForge.Domain.Agents.NetworkPosture.LoopbackOnly,
            secondaryAgent.CapabilityPolicy.NetworkPosture);
        Assert.Equal(["tool:workspace.read"], secondaryAgent.CapabilityPolicy.ToolGrants);
        Assert.Equal(["skill:csharp.review"], secondaryAgent.CapabilityPolicy.SkillGrants);
        Assert.Equal(5, secondaryAgent.Budget.MaxToolInvocations);
        Assert.Equal(1, secondaryAgent.ChildLimits.MaxChildren);
        Assert.Equal(AgentForge.Domain.Agents.AgentMemoryScope.Agent, secondaryAgent.MemoryPolicy.Scope);
        Assert.Equal(AgentForge.Domain.Agents.LearningMode.Propose, secondaryAgent.LearningPolicy.Mode);
        var createdProviders = await administrationVerification.ServiceProvider
            .GetRequiredService<IProviderProfileRepository>().ListAsync(installationId, CancellationToken.None);
        Assert.Equal(2, createdProviders.Count);
        Assert.False(Assert.Single(createdProviders, item => item.Id.Value == secondaryProviderId)
            .SecretReference.IsNoCredential);
    }

    [Fact]
    public async Task Loopback_wizard_accepts_an_explicit_manual_model_but_still_requires_selection_and_probe()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = true,
        });
        using var sessionResponse = await client.PostAsJsonAsync("/api/v1/setup/web/session", new { });
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var csrf = await ReadPropertyAsync(sessionResponse, "csrfToken");
        var provider = new
        {
            name = "primary",
            providerType = "openai-compatible",
            endpoint = "http://127.0.0.1:8000/v1",
        };
        using var discovery = await MutationAsync(client, "/api/v1/setup/web/provider/discover", "manual-discover", csrf, JsonContent.Create(provider));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        using var manual = await MutationAsync(client, "/api/v1/setup/web/provider/model/manual", "manual-model", csrf, JsonContent.Create(new
        {
            provider.name,
            provider.providerType,
            provider.endpoint,
            model = "qwen3.6-manual",
        }));
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        Assert.Contains("manual-entry", await manual.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var resumed = await client.GetAsync("/api/v1/setup/web/session");
        Assert.Equal("2", await ReadRawPropertyAsync(resumed, "currentStep"));
        using var rejectedProbe = await MutationAsync(client, "/api/v1/setup/web/provider/test", "manual-probe-before-select", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedProbe.StatusCode);
        using var selected = await MutationAsync(client, "/api/v1/setup/web/provider/select", "manual-select", csrf, JsonContent.Create(new
        {
            provider.name,
            provider.providerType,
            provider.endpoint,
            model = "qwen3.6-manual",
        }));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var tested = await MutationAsync(client, "/api/v1/setup/web/provider/test", "manual-probe", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
    }

    private static async Task<HttpResponseMessage> MutationAsync(
        HttpClient client, string path, string key, string csrf, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> StreamMutationAsync(
        HttpClient client,
        string path,
        string idempotencyKey,
        string csrf,
        string prompt,
        string? name = null,
        string? runInstructions = null,
        string? responseDepth = null,
        int? maximumOutputTokens = null,
        IReadOnlyList<string>? skillIds = null,
        string? memoryQuery = null,
        string? researchReceiptHash = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new
            {
                prompt,
                name,
                runInstructions,
                responseDepth,
                maximumOutputTokens,
                skillIds,
                memoryQuery,
                researchReceiptHash,
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, completion);
    }

    private static async Task<string> ReadSseEventAsync(StreamReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                break;
            }
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    private static async Task<string> ReadPropertyAsync(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static async Task<string> ReadRawPropertyAsync(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.GetProperty(property).GetRawText();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class WebFakeSecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, char[]> _values = new(StringComparer.Ordinal);
        public string StoreName => "web-fake";
        public SecretStoreCapability GetCapability() => new(StoreName, true, null);
        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
        {
            var key = $"secret-{Guid.NewGuid():N}";
            _values[key] = secret.ToArray();
            return Task.FromResult(DomainResult.Success(new SecretReference(StoreName, key)));
        }
        public Task<DomainResult<SecretLease>> MaterializeAsync(SecretReference secretReference, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(secretReference.Key, out var value)
                ? DomainResult.Success(new SecretLease(value.ToArray()))
                : DomainResult.Fail<SecretLease>(new DomainFailure(FailureCode.UnsupportedCapability, "Secret unavailable.")));
        public Task<DomainResult<bool>> DeleteAsync(SecretReference secretReference, CancellationToken cancellationToken)
        {
            var removed = _values.TryRemove(secretReference.Key, out var value);
            if (value is not null) Array.Clear(value);
            return Task.FromResult(DomainResult.Success(removed));
        }
    }

    private sealed class WebFakeModelDiscovery : IModelCatalogDiscoveryService
    {
        public Task<DomainResult<ModelCatalogDiscoveryResult>> DiscoverAsync(
            ModelCatalogDiscoveryRequest request,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Success(new ModelCatalogDiscoveryResult(
                [
                    new ModelCatalogEntry("qwen3.6", "vllm", 131_072),
                    new ModelCatalogEntry("qwen3.8", "vllm", 131_072),
                ],
                new Uri(request.BaseEndpoint, request.BaseEndpoint.AbsolutePath.TrimEnd('/') + "/models"),
                DateTimeOffset.UnixEpoch)));

        public Task<DomainResult<ModelConnectionProbeResult>> ProbeAsync(
            ModelConnectionProbeRequest request,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Success(new ModelConnectionProbeResult(
                request.Model,
                new Uri(request.BaseEndpoint, request.BaseEndpoint.AbsolutePath.TrimEnd('/') + "/chat/completions"),
                TimeSpan.FromMilliseconds(12),
            "web-fake-probe")));
    }

    private sealed class WebFakeBraveSearchProbe : IBraveSearchConnectivityProbe
    {
        public Task<DomainResult<BraveSearchProbeEvidence>> ProbeAsync(
            ReadOnlyMemory<char> credential,
            BraveSearchConfigurationCandidate candidate,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Success(
                new BraveSearchProbeEvidence(
                    1,
                    TimeSpan.FromMilliseconds(9),
                    $"sha256:{new string('b', 64)}")));
    }

    private sealed class WebFakeLocalInteraction : ILocalModelInteractionService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _streamAttempts = new();

        public Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
            LocalModelInteractionRequest request,
            CancellationToken cancellationToken)
        {
            var output = request.SystemInstruction.Contains(
                "bounded local skill-candidate author", StringComparison.Ordinal)
                ? request.Prompt.Contains("force-malformed", StringComparison.Ordinal)
                    ? "This is not a JSON candidate."
                    : JsonSerializer.Serialize(new
                    {
                        markdown = """
## Purpose

Provide a bounded procedure for addressing the classified missing capability without acquiring authority.

## Inputs

- The operator-approved task goal.
- The declared read-only permission boundary.
- Observable input constraints supplied at invocation time.

## Procedure

1. Restate the intended outcome and all known constraints.
2. Identify the smallest repeatable read-only steps that could produce the outcome.
3. Stop and report an unknown whenever required input or authority is absent.
4. Return the proposed result together with evidence a separate verifier can inspect.

## Verification

Confirm that every stated input maps to one bounded step, every output has observable evidence, and no undeclared action is claimed.

## Failure conditions

Fail when input is missing, evidence is ambiguous, the requested operation exceeds the declaration, or verification cannot be completed.

## Permission boundary

The procedure may describe only repository:read behavior. It receives no execution, network, credential, messaging, device, or approval authority.
""",
                    })
                : "I am the bounded AgentForge test agent.";
            return Task.FromResult(DomainResult.Success(new LocalModelInteractionResult(
                request.RequestId,
                output,
                new ModelUsage(12, 9, 0, null, null),
                ModelFinishReason.Stop,
                0,
                4,
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        }

        public async Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
            LocalModelInteractionRequest request,
            ILocalModelInteractionObserver observer,
            CancellationToken cancellationToken)
        {
            await observer.OnProgressAsync(new LocalModelInteractionProgress(
                request.RequestId,
                LocalModelInteractionProgressKind.Started), cancellationToken);
            if (string.Equals(request.Prompt, "Interrupt once.", StringComparison.Ordinal) &&
                _streamAttempts.AddOrUpdate(request.RequestId.Value, 1, (_, count) => count + 1) == 1)
            {
                return DomainResult.Fail<LocalModelInteractionResult>(new DomainFailure(
                    FailureCode.RecoverableExternalFailure,
                    "Deterministic first-attempt interruption.",
                    IsRetryable: true));
            }
            if (string.Equals(request.Prompt, "Wait for cancellation.", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            await observer.OnProgressAsync(new LocalModelInteractionProgress(
                request.RequestId,
                LocalModelInteractionProgressKind.TextDelta,
                TextDelta: "I am the bounded AgentForge test agent."), cancellationToken);
            var usage = new ModelUsage(12, 9, 0, null, null);
            await observer.OnProgressAsync(new LocalModelInteractionProgress(
                request.RequestId,
                LocalModelInteractionProgressKind.Usage,
                Usage: usage), cancellationToken);
            return DomainResult.Success(new LocalModelInteractionResult(
                request.RequestId,
                "I am the bounded AgentForge test agent.",
                usage,
                ModelFinishReason.Stop,
                0,
                4,
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }
}
