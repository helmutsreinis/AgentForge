# Gate POST-R1-READY-MVP-20260812

Decision: **Pass**

## Scope

- AF-ADMIN-001, AF-HOST-002..003, AF-ID-001, AF-TASK-001, AF-SKILL-001..002, and AF-SEC-003.
- Replace the disabled Agents, Runs, and Skills placeholders with a testable loopback Ready workspace.
- Keep the local administrator bearer credential out of JavaScript and browser storage.
- Reuse durable agent, orchestration, artifact, audit, and skill-registry boundaries without adding a
  browser-only authority path.
- Preserve the closed model-execution, tool-invocation, skill-promotion, messaging, and device-write gates.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet build AgentForge.slnx --no-restore -c Release` | Pass; 0 warnings, 0 errors |
| Focused `WebSetupWizardTests` | Pass; 2/2 including complete Ready workspace journey |
| Complete Release product suites plus framework spike | Pass; 398 product tests plus 2 spike tests; 4 named live/equipped skips; 0 product failures after SDK-aligned rerun |
| `dotnet format AgentForge.slnx --no-restore --verify-no-changes` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| Live loopback Agents page | Pass; persisted `local-agent` and exact conservative policy rendered |
| Live loopback Runs journey | Pass; `MVP browser smoke test` persisted Planned v0 then Canceled v1 with changed hash |
| Live loopback Skills journey | Pass; `skill:csharp.review` 1.0.0 installed from Seed and remained non-Active |
| Browser console/DOM | Pass; all navigation and controls present with no observed runtime error |

The workstation now exposes .NET SDK 10.0.400 while the repository intentionally pins 10.0.302. The outer
Release suites initially ran from a temporary directory so 397 product cases passed, while the sole Roslyn
workspace case could not resolve the nested repo SDK. That exact test passed when rerun from the repository
with the installed 10.0.400 feature band selected temporarily. The checked-in `global.json` was restored
unchanged to 10.0.302. This was toolchain resolution, not a product failure.

The four skipped cases are the existing credential-gated compatible-provider stream, two live PostgreSQL
cases, and the Docker-equipped sandbox. None is newly required by this loopback administration slice.

## Security and portability review

Session creation and every workspace request require loopback plus exact same origin. The server reads the
exact Ready installation and local-administrator record, materializes the current user's OS-protected secret,
validates it through the existing fixed-time authenticator, and disposes the clearable lease before issuing
one random 30-minute HttpOnly SameSite=Strict cookie. The browser receives only CSRF, actor, installation, and
expiry metadata. Sessions are memory-only, single-installation, rate-limited, and revalidate exact Ready scope.

Mutations require independent CSRF and bounded idempotency keys. Run creation looks up an installation-owned
exact agent/version and pins policy, budget, child, and skill-grant snapshot hashes before the existing
orchestrator commits its audit and snapshot. The UI cannot claim a node or call a model/tool. Cancellation uses
the latest optimistic-concurrency version. Latest-run listing uses one provider-neutral repository contract
implemented as an EF query compatible with SQLite and PostgreSQL.

The C# review seed is copied into host/package output and resolved only from the fixed application directory.
Installation uses the existing package loader, dependency/signature rules, content-addressed artifact store,
audit recorder, and unit of work. It cannot set Active; governed evaluation, separate approval, canary, and
promotion remain mandatory. No migration or credential format changed.

## Evidence SHA-256

- `src/AgentForge.Host/Http/ReadyAdminEndpoints.cs`: `51f0c9b70dd33c9500b035c47396845ec1e430c511f2ffad0767d3f303438107`
- `src/AgentForge.Host/Http/ReadyAdminSessionManager.cs`: `79f0d81961475e5553b77210b9377c431272ecf9777599ba7dd68affb10e85ff`
- `src/AgentForge.Host/wwwroot/index.html`: `0135f2bc476de33718d2c3f06e24ed847eef24723efc69b5812b587cff81d6ea`
- `src/AgentForge.Host/wwwroot/app.js`: `56631b08681387f8426013a0539dae6b92242211d4bd9a433b66675f96dad39e`
- `src/AgentForge.Host/wwwroot/styles.css`: `4c43c72d085696133af1a1386e91b109219ff2cfdd413a25c0c5fec4822e0c30`
- `src/AgentForge.Persistence/SqliteTaskSnapshotStore.cs`: `f1e260bf3e04c10efbce99a1f6c3cba5f72ea067e61acdbb411a510d0e958976`
- `tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs`: `23d78780327a33e598fa4da233a890faced56c687dcb27a094cfcbbb8bc479a6`
- Baseline commit: `504fc24d56b524a240274354a45566fdb2ce1766`

## Rollback

Stop the host to invalidate in-memory Ready sessions, then revert this slice as one commit. No schema rollback
is required. Existing orchestration snapshots and the installed seed are valid R1 data and may remain; use the
normal skill archive or verified full-installation restore if the operator explicitly wants to remove them.
Never delete or rewrite the active data directory as part of code rollback.
