# POST-R1 Scheduling, Memory, and Research Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Complete the final item in the operator's ordered MVP sequence: durable local-model schedules, scoped memory retrieval,
and explicitly approved cited research attached to the existing real run lifecycle. This slice does not grant the model
network access, dynamically change policy, execute tools, or permit a scheduled run to inherit later agent, provider,
skill, or budget changes.

Requirements: `AF-ADMIN-007`, `AF-SCHED-001..003`, `AF-MEM-001..004`, `AF-SEARCH-001..003`,
`AF-SEC-003..005`.

## Security, durability, and portability disposition

- A scheduled run stores an immutable, content-addressed template pinned to the installation, agent, provider, policy,
  capability, budget, and selected Active-skill versions. The worker verifies versions and artifact hashes before model
  invocation and fails closed on drift.
- Schedule creation and controls require the authenticated Ready operator session, origin/CSRF enforcement, exact
  hash-bound preview/apply, optimistic versions, and idempotency. Claims, leases, retries, misfires, overlap, and
  run-now use the existing deterministic scheduling state machine.
- Scheduled executions use durable tasks, conversations, turns, snapshots, evidence, and terminal receipts. A resumed
  Ready conversation replays its completed model result instead of invoking the provider a second time.
- Memory is constrained to the selected agent's configured scope. UI-created memory is limited to User or Procedural
  records, bounded retention, explicit correction evidence, and authenticated idempotent create/delete operations.
- Research is a separate operator context-acquisition action. Exact providers, query, result limit, agent version, and
  expiry are approved before a provider is called. Only a bounded same-agent citation receipt can be attached to a run.
- Retrieved memory and citations are labelled untrusted reference data in the system context. They cannot grant tools,
  network, policy, credentials, messaging, filesystem, or device authority. Real runs continue to send zero tools.
- Empty research-provider configuration is a typed unavailable state and does not weaken memory or run behavior.
  Deterministic providers cover CI; live provider credentials remain an independently gated configuration concern.
- The schema addition is provider-neutral EF Core. SQLite is verified locally; the repository and service contracts do
  not depend on a SQLite implementation detail.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx -c Release --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build --no-restore
PASS — 422 product tests + 2 framework-spike tests; 4 expected environment-gated skips

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Release \
  --filter FullyQualifiedName~Loopback_wizard
PASS — 2 tests; exact schedule/create/run-now, worker completion, memory CRUD, research approval, and attached-context run

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable direct or transitive packages

./scripts/verify-no-secrets.ps1
PASS — 786 tracked and untracked candidate files

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS

dotnet ef migrations has-pending-model-changes --project src/AgentForge.Persistence \
  --startup-project src/AgentForge.Persistence --configuration Release --no-build
PASS — no model changes
```

## Live Ready-workspace evidence

- The Release host is healthy at `http://127.0.0.1:5047/` against the existing Ready data directory and pinned private
  `qwen3.8` provider.
- The Schedules view loaded the existing agent and exposed interval, one-shot, cron, calendar, timezone, overlap,
  misfire, retry, jitter, and output-budget controls without creating a persistent schedule during the smoke check.
- The Context view loaded scoped memory controls. It reported that no search provider is configured in this specific
  installation, so live research remained fail-closed while memory remained usable.
- The Runs composer exposes optional memory retrieval and approved research-receipt attachment; context counts and
  the immutable context snapshot hash are emitted in the run-started event.

## Evidence hashes

```text
840f57e52566770424c781fb0f067075765271541dbe09429ae15b29ece2bb26  ScheduledAgentRunRecords.cs
c1bd1434b5eae72b4de3ea50455ead12b50979f563cbfbd8b3216ff299d38df2  IScheduledAgentRuns.cs
f5c452d3a076609bf406e88a4dc7e53eaa7176f0afd845bdaba94b09fbf070ff  ScheduledAgentRunService.cs
2374c00fd4745ea4ee7af3325b92cd8321eb1fdc7083f8f75430290b72d370ba  ScheduledAgentRunWorker.cs
638e0681f9e2764cedfe5ace05b95abc5a54a84410ccce8149920a7154fa08aa  ReadyAdminScheduleEndpoints.cs
e004e2b751bb48f6f9d15d021173ce05260e6e224600bc11fda5522762d12b55  ReadyAdminContextEndpoints.cs
476e074469114d728fa70510c70e9b9a79050059d62cb359838bbb14fa205047  ReadyAdminEndpoints.cs
2e1a26b912fbe2e73935f024b01f3d22c5be887b98bd2415fc3e838e9783dfe4  app.js
15c50d309f29d6cf049cc48fc512ed25ffa9ff70baa3b8c1907e19aefa120258  index.html
e83e0533ea4522e28ad7e8748fe83e35ea90451fc42448d676a9eb824dd577ef  WebSetupWizardTests.cs
f440bf3114a70a9641a9dbf2426d3640328423a0f89a5b7382da44c0526eaff0  20260816182925_ScheduledAgentRuns.cs
```

## Rollback and recovery

Stop the host and take a verified database/artifact backup before rollback. Code may be rolled back to the preceding
gate after scheduled executions finish or are paused. If the schema must also be removed, first export any schedule
templates and receipts, then migrate the isolated/restored database to `20260816165307_DurableRunConversations`; do
not hand-edit SQLite. Existing tasks, conversations, model receipts, memory, research citations, and audit evidence are
historical records and must not be deleted merely to undo the UI or worker. A failed scheduled occurrence should be
recovered through its durable retry/dead-letter state, never by manually marking it complete.
