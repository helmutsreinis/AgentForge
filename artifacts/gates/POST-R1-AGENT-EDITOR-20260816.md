# POST-R1 Agent Editor Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Expose a usable Ready-state agent editor without creating a second configuration authority. The slice adds
identity/instruction editing and model selection on the existing pinned provider. It deliberately excludes provider
endpoint/type/credential changes and every capability, budget, memory, child-agent, tool/skill, and learning-policy
change.

Requirements: `AF-ADMIN-002`, `AF-HOST-003`, `AF-SEC-003`.

## Security and portability disposition

- Every mutation requires the protected Ready operator session, CSRF, origin policy, and a bounded idempotency key.
- The existing authenticated `ISetupProfileEditor` performs normalization, policy evaluation, optimistic concurrency,
  request-hash binding, transactional provider/agent plus installation update, and redacted audit append.
- `Ready → Ready` configuration changes increment installation version. Ready provider edits are service-enforced as
  model-only; Ready agent edits are service-enforced as identity/instruction-only.
- Administrator and provider credentials are materialized only for one invocation and are absent from previews,
  browser state, responses, logs, tests, and this report.
- Model selection preserves endpoint/type/secret topology and performs a bounded thinking-disabled chat probe before
  preview and again immediately before apply.
- The implementation is platform-neutral. The live gate used the configured Windows DPAPI store; deterministic tests
  use an invocation-scoped fake secret store and run under the Windows/Ubuntu CI matrix.

## Verification evidence

Commands and results:

```text
dotnet build src/AgentForge.Host/AgentForge.Host.csproj -c Release --no-restore -p:UseSharedCompilation=false
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
PASS — 404 product tests and 2 Agent Framework spike tests; 4 expected equipped/live tests skipped

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Release --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~WebSetupWizardTests.Loopback_wizard_hides
PASS — exact Ready editor journey after final policy-boundary changes

dotnet test tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj -c Release --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~InstallationStateMachineTests|FullyQualifiedName~OpenAiCompatibleModelDiscoveryServiceTests
PASS — 8 tests

dotnet format AgentForge.slnx --verify-no-changes --no-restore
PASS

node --check src/AgentForge.Host/wwwroot/app.js
PASS

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no vulnerable packages

pwsh -NoProfile -File scripts/verify-no-secrets.ps1
PASS — 743 tracked and untracked files scanned

git diff --check
PASS
```

The deterministic end-to-end journey proves editor assets, exact installation/agent/provider versions, endpoint model
discovery, missing-CSRF denial, unapproved-preview denial, live-probe receipt, exact model apply/replay, stale profile
preview conflict after the installation version changes, refreshed identity preview/apply, immutable policy preservation,
and durable model/profile readback.

Operator-authorized live smoke against `http://192.168.1.89:8000/v1`:

```text
Configured before: qwen3.6, provider v0, installation v3
Discovered: qwen3.8
Preview: provider.model only; probe 357 ms; affected agent local-agent
Applied: qwen3.8, provider v1, installation v4 (state remained Ready)
AgentForge completion: provider primary; model qwen3.8; state Completed; exact output QWEN38 READY
```

## Content evidence

```text
fb127bbff55cc288b3bf66ee6d1036707e061b2536582084bb926e918893cb59  src/AgentForge.Host/Http/ReadyAdminAgentEditEndpoints.cs
eff5d0ef4f13a60b1eefc5930564be82c4fe63596acfd23da8d3f193d50b1f93  src/AgentForge.Setup/SetupProfileEditor.cs
1f804875dbbcb15cd36500ecbbe0259c79155cd55c120cfccc830df744ec18e5  src/AgentForge.Models/OpenAiCompatibleModelDiscoveryService.cs
1596d462e36b147cb31e130366aec8ad3b9e43f70c775ee4f2b7fb7ac32f674e  src/AgentForge.Host/wwwroot/app.js
ceaa198c9ac73bb54443175d1f773116c2426444adbcf21b88efa2b0754ce729  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

Code rollback is the inverse of the single feature commit. No database migration was introduced. Existing stored
profiles remain readable because only ordinary provider, agent, installation, and audit records are used.

Configuration rollback must use the same editor: discover the existing endpoint, select the prior model, preview,
probe, and apply. Do not rewrite provider or audit rows. If the endpoint is unavailable, keep the current profile,
preserve the database/WAL/artifacts, and retry or enter the documented recovery flow. A stale preview is expected to
fail; reload current versions and create a new preview.
