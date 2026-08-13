# Gate POST-R1-STREAMING-MVP-20260813

Decision: **Pass**

## Scope

- AF-ADMIN-001, AF-HOST-003, AF-MODEL-001, AF-RUN-001, AF-AUD-001, and AF-SEC-003..004.
- Stream normalized model start, text-delta, and usage progress to the authenticated Ready Runs workspace.
- Bind active operator cancellation to the exact installation, Ready session, and durable orchestration task.
- Require durable Completed, Failed, or Canceled evidence while retaining no prompt or output text.
- Keep tools, fallback, browsing, files, messaging, skill promotion, and device access unavailable.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet build AgentForge.slnx --no-restore -c Release` | Pass; 0 warnings, 0 errors |
| Focused `LocalModelInteractionServiceTests` | Pass; 4/4 including ordered start/delta/usage observation, private-route restriction, tool-call denial, and output bounds |
| Focused `WebSetupWizardTests` | Pass; 2/2 including authenticated stream success, usage/completion events, active cancellation, and durable Canceled receipt |
| Complete Release product suites plus framework spike | Pass; 403 product tests plus 2 spike tests; 4 named live/equipped skips; 0 failures |
| `dotnet format AgentForge.slnx --no-restore --verify-no-changes` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `scripts/verify-no-secrets.ps1` | Pass across 740 tracked and untracked files |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| Live streamed completion | Pass; `qwen3.6` returned exact `STREAMING_OK` and `DURABLE_RECEIPT_OK`, reported 94 input/11 output tokens, and persisted Completed snapshot v2 |
| Live operator cancellation | Pass; a long `qwen3.6` response was actively canceled and persisted Canceled snapshot v2 |
| Persisted-data byte scan | Pass; neither live prompt nor response markers appeared under the installation data directory |
| Browser DOM and console | Pass; progressive output, usage, terminal state, cancel-control hiding, and refreshed receipts rendered with no warnings or errors |
| Ready health after live tests | Pass; installation state Ready, version 3 |

The complete suite was run with the installed .NET 10.0.400 feature band selected temporarily so the nested
MSBuild/Roslyn workspace fixture could load. The repository's checked-in `global.json` was restored unchanged
to 10.0.302. The four skips remain the Docker-equipped sandbox, credential-gated compatible-provider stream,
and two live PostgreSQL cases.

## Security and portability review

The stream mutation retains the existing Ready administrator session, exact origin, CSRF, rate, request-size,
installation-scope, and fresh idempotency checks. Agent and provider authority are exact and versioned. The
interaction remains local-only, no-fallback, no-tools, text-only, credential-free, and bound to one literal
loopback/private compatible endpoint through the existing socket destination policy.

Only normalized `Started`, text-delta, and usage records cross the observer contract. Prompt text, response text,
and provider error bodies do not enter the orchestration definition, database, audit, artifacts, or replay cache.
The response has `text/event-stream`, `no-store`, disabled proxy buffering, fixed event names, server-owned JSON,
and existing output/token/event/time limits. A reused idempotency key is denied because transient raw output
cannot be reconstructed safely.

The in-memory active registry contains only task identity, installation identity, a session hash, an
operator-cancellation bit, and the linked cancellation source. Cancellation first commits the durable terminal
snapshot, then signals only an exact registry match. Completion racing after cancellation cannot replace the
durable Canceled state. Client disconnect cancels provider work without falsely claiming operator authority.
The browser requires a terminal SSE receipt, renders deltas with `textContent`, and hides the cancel action after
Completed, Failed, or Canceled.

The observer, registry, endpoint, and browser stream reader use platform-neutral .NET/HTTP/browser primitives.
No database schema, protected secret, provider-profile, or artifact format changed.

## Evidence SHA-256

- `src/AgentForge.Abstractions/Models/ILocalModelInteractionService.cs`: `f6fb031fbea7c1539b05b52a5719ab10012798578fcccd9e8aeb390dbc8d0d46`
- `src/AgentForge.Models/LocalModelInteractionService.cs`: `2f2a59ded90751cc639a31bac2b90dfc15fe3f1a0d70060b82f33bebb426302d`
- `src/AgentForge.Host/Http/ReadyActiveInteractionRegistry.cs`: `043967d208872ea5926b86a1cefddbd28c3393bc076c582b89fde22ea37b11d1`
- `src/AgentForge.Host/Http/ReadyAdminEndpoints.cs`: `8fca32332bd4c957320aa6e3fbce668bdf131dee19ddd00b7c19cf5b4ed7087e`
- `src/AgentForge.Host/Program.cs`: `d978a53d481cf535017a25d51aba77de06256264c6e5cac16124662f088c9dd2`
- `src/AgentForge.Host/wwwroot/index.html`: `93d4dc78c68afbd1b67193e74d29e4846490e99ca07714deb5390da212e383f4`
- `src/AgentForge.Host/wwwroot/app.js`: `885c46a17265d9d570da719a2728a75abd28716103933207fac208678608e783`
- `src/AgentForge.Host/wwwroot/styles.css`: `c8a82a06bc103f254922e2f8f0ef21aae0ae910c4c0671f3159617bd25399b72`
- `tests/AgentForge.UnitTests/LocalModelInteractionServiceTests.cs`: `3c818e100e8585662472a2b2befc0d05572b1274d671f3ab23a119f70382e1e1`
- `tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs`: `8b474be0cd836edefe4ee517605a1cc28df89d7aeaf98f6a008d503553789611`
- Baseline commit: `a667ee659be6d91c339fea82c658c3e9fda58bef`

## Rollback

Stop the host to clear the transient active-interaction registry, then revert this slice as one commit. No schema,
secret, or artifact migration is required. Existing streaming-test snapshots remain valid hash-only orchestration
evidence and must not be deleted or rewritten. A rollback intentionally returns the Runs page to whole-response
delivery and removes active invocation cancellation.
