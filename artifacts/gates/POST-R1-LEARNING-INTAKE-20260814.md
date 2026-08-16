# Gate POST-R1-LEARNING-INTAKE-20260814

Decision: **Pass**

## Scope

- AF-LEARN-001..003, AF-ADMIN-001, AF-HOST-003, AF-AUD-001, and AF-SEC-003..005.
- Expose a protected operator Learning inbox over the existing deterministic recursive-learning classifier.
- Bind every accepted signal to an exact terminal durable run receipt and installation-scoped snapshot hash.
- Preserve classification as evidence only: intake cannot create a package, candidate, permission, policy, tool call,
  active skill, promotion, or repeated-chain claim.
- Hand terminal receipts from paginated Runs history into the Learning workspace without making run history additive
  on one unbounded page.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet restore AgentForge.slnx --locked-mode` | Pass |
| `dotnet build AgentForge.slnx -c Release --no-restore -p:UseSharedCompilation=false` | Pass; 0 warnings, 0 errors |
| Focused `WebSetupWizardTests` | Pass; 2/2 including Learning markup and the protected Ready journey |
| Focused `RecursiveLearningPersistenceTests` | Pass; 1/1 restart journey with installation-scoped signal/classification readback |
| Complete Release suites with `dotnet test AgentForge.slnx -c Release --no-build --no-restore -m:1` | Pass; 403 product tests plus 2 framework-spike tests; 4 named live/equipped skips; 0 failures |
| `dotnet format AgentForge.slnx --verify-no-changes --no-restore` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `scripts/verify-no-secrets.ps1` | Pass across 741 tracked and untracked files |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| Actual LAN HTTPS root | Pass; `https://192.168.1.100:5443/` returned 200 and contained the Learning form |
| Remote session without access code | Pass; denied with 403 |
| Remote session with exact temporary access code | Pass; returned 200 |
| Protected live Learning query | Pass; returned 200, installation `Ready`, initially 0 signals |

The four ordinary-suite skips remain the Docker-equipped sandbox, credential-gated compatible-provider stream,
and two credential/tool-gated live PostgreSQL cases.

An initial default-parallel aggregate run completed build and most tests but produced five unrelated timeout/
resource-contention failures in CLI subprocess and MCP fixtures (four EndToEnd, one Integration). No modified
learning assertion failed. The repository's established local post-R1 gate setting (`-m:1`) reran every project
without changing fixture timeouts and passed all 403 product tests plus both spike tests, confirming contention
rather than a product regression.

The browser-control runtime could not attach in this sandbox because it was denied access to its own local profile
path. Visual automation was therefore not claimed. The same rendered shell is covered by deterministic DOM/HTTP
end-to-end assertions, JavaScript syntax validation, and the protected live LAN root/session/query smoke above.

## Security and portability review

Capture requires the existing Ready administrator session, exact origin, CSRF token, bounded request body,
serialized session mutation, and an idempotency key. The endpoint resolves the exact task from durable state,
requires a Completed, Failed, Canceled, or DeadLettered receipt from the same installation, and binds the signal's
source evidence to that snapshot hash. It never accepts prompt or response text from durable task storage.

Summaries are normalized to a bounded single line, rendered with `textContent`, and passed through the existing
domain classifier, which rejects credential-shaped material. `RepeatedSkillChain` is excluded because this UI
does not possess exact multi-skill usage receipts. Correction intake supplies no revision authorization or usage
receipt, so it becomes Memory; a MissingCapability may classify as NewSkill, but that result remains inert data.

Signal IDs and correlations are stable per installation/operation/idempotency key. Exact replay returns the same
classification; changed evidence under the key conflicts. Durable insertion and `learning.signal-classified` audit
record share the existing learning unit of work. Listing is installation scoped, newest first, and bounded to 100;
stored signal and classification hashes are reverified during materialization.

The implementation uses platform-neutral ASP.NET Core, browser, EF Core, Domain, and Abstractions contracts. There
is no migration, secret format, model-provider, artifact-format, or active-skill change. Default networking remains
loopback only; the retained LAN host is an explicitly enabled HTTPS test instance with a temporary process-memory
access code and a seven-day self-signed certificate.

## Evidence SHA-256

- `src/AgentForge.Abstractions/Learning/ILearningGovernance.cs`: `3003cae811096b5bb6dcb45048da21b019bcb92ebdf9c8b56f825e55317b866a`
- `src/AgentForge.Persistence/SqliteLearningRepository.cs`: `d36cf502cffa029ebd8757873e375e7b8969cacb8d38c018a1ee25fb7e73b6fe`
- `src/AgentForge.Host/Http/ReadyAdminEndpoints.cs`: `83a9e919cfed5849557d6d32c8336c6f973ce88ec9f1bf8d66ff83e611e448a2`
- `src/AgentForge.Host/wwwroot/index.html`: `a3da8662ecb40251c8dbb7bbb770af93eab5f1d76d24503f569b6809c0bf14df`
- `src/AgentForge.Host/wwwroot/app.js`: `bdbae54668863a2fa49296b5f3d2a88229eae750081c245ab03baac6d6d9b9a0`
- `src/AgentForge.Host/wwwroot/styles.css`: `e3899008bdd838c7420b4b2b544787fc47f0557796c92a0b0f6e2df98734f127`
- `tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs`: `884ba513148af665733d7c01cec9f6309ac1d39eeaed78d572ff586a72a33653`
- `tests/AgentForge.IntegrationTests/RecursiveLearningPersistenceTests.cs`: `4bba3dc82438ce951dbe4a0796758a26e23957c9f7b32b0fd736f432d34bf399`
- Baseline commit: `1b1389da3c6fb47a450fe5bfa8b83b68b057ada5`

## Rollback

Stop the host and revert this slice as one commit. No database migration, protected-secret, provider, package, or
artifact rollback is required. Existing learning rows are immutable evidence and must not be deleted or rewritten;
an older host can safely ignore them. Rollback removes only the Ready capture/list API and UI/query method while
preserving the underlying Milestone 9 learning governance engine.
