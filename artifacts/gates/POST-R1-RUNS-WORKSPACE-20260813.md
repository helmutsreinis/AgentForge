# Gate POST-R1-RUNS-WORKSPACE-20260813

Decision: **Pass**

## Scope

- AF-ADMIN-001, AF-HOST-003, AF-MODEL-001, AF-RUN-001, AF-SKILL-001, and AF-SEC-003..004.
- Make the completed-setup action navigate to the visible Overview view.
- Replace the single prompt box with a named, depth-bounded run composer that exposes base system context and
  exact runtime restrictions.
- Expose only Active immutable skill versions already granted to the selected agent and bind their exact snapshot
  to the run before invocation.
- Keep the latest bounded durable history navigable through local search, state filters, page size, and pagination.
- Move the repository and CI SDK pin to the installed stable .NET 10.0.400 feature band.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet restore AgentForge.slnx --locked-mode` | Pass |
| `dotnet build AgentForge.slnx -c Release --no-restore` | Pass; 0 warnings, 0 errors |
| Focused `WebSetupWizardTests` | Pass; 2/2 including status-link markup, run-option projection, named/detailed stream configuration, and ungranted-skill denial |
| Complete Release product suites plus framework spike | Pass; 403 product tests plus 2 spike tests; 4 named live/equipped skips; 0 failures |
| `dotnet format AgentForge.slnx --verify-no-changes --no-restore` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `scripts/verify-no-secrets.ps1` | Pass across 741 tracked and untracked files |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| Setup completion navigation | Pass; **View system status** changed the visible route to `#overview` |
| Browser history navigation | Pass; 12 existing receipts rendered as 8 items on page 1 and 4 on page 2; search reduced the view to 2 matching Canceled receipts |
| Browser governed-skill state | Pass; Installed/unapproved `skill:csharp.review@1.0.0` was visibly disabled and remained disabled after form busy-state reset |
| Live configured model runs | Pass; `qwen3.6` returned exact `AGENTFORGE_UI_OK` and `AGENTFORGE_POLICY_OK` through titled concise runs with run-level guidance |
| Ready health after live tests | Pass; installation state Ready, version 3 |

The four ordinary-suite skips remain the Docker-equipped sandbox, credential-gated compatible-provider stream,
and two live PostgreSQL cases. The operator-authorized model endpoint was exercised separately by the browser
checks above.

## Security and portability review

The new run-options read is protected by the existing Ready administrator session and exact installation scope.
It exposes only the already persisted non-secret agent instruction, pinned provider metadata, response bounds,
skill metadata, and explicit denial posture. The stream mutation retains exact origin, CSRF, request-size,
fresh-idempotency, session serialization, local-only model-route, no-fallback, and zero-tool-budget gates.

Run title and response depth are bound into durable orchestration metadata and budget hashes. Run guidance is
bounded and represented durably only by a hash. Selected skill IDs must already exist in the agent's immutable
grant list; the skill service resolves only the active version and exact dependencies, persists an immutable
snapshot, rechecks every dependency grant, verifies artifact integrity, and supplies the redacted bodies only to
the transient model invocation. A hostile client that submits an Installed but ungranted skill receives 403.
Prompt, run guidance, skill body, and model output remain absent from durable run responses.

History rendering uses `textContent`, retains the server's latest-100 bound, and renders only one 5/8/12-item
page at a time. Search and status filters operate over already authorized receipt metadata and cause no new
server request. Busy-state restoration preserves policy-disabled controls, including controls recreated while a
stream is active.

The implementation uses platform-neutral ASP.NET Core, browser, and existing skill-snapshot contracts. There is
no database schema, secret format, API authentication, provider profile, or artifact-format change. The SDK pin
and both GitHub Actions workflows now select the same installed stable .NET 10.0.400 feature band.

## Evidence SHA-256

- `global.json`: `b54f95ccc7a0a2199f7f2ce71cef88c853e633b5f6d0c492095cd16bbf4d8506`
- `.github/workflows/ci.yml`: `d296bf1b3d0889f861aec51ff3cb9afd18f8fa03aac861beab1aa3680e91adb4`
- `.github/workflows/release.yml`: `0f09981912d974e1b27bb7d144e9987bb95603b9298b05dbd12b9e910b2194ef`
- `src/AgentForge.Host/Http/ReadyAdminEndpoints.cs`: `3457c11b6d317e22e0a5a8db06187e5f1ba43c69a500f51e66e4405b9e8b0e9a`
- `src/AgentForge.Host/wwwroot/index.html`: `cc367c1ec5d6cd04857bc92ace90090ccfae1160eeaf10de1ea6191bfcce6c66`
- `src/AgentForge.Host/wwwroot/app.js`: `4fb7c1f3961322c6adcd7dfc8a2460c14fc962ca099e8e96f0f60f507451116f`
- `src/AgentForge.Host/wwwroot/styles.css`: `9043ebc5398f8e7140647687c084fad6206c6a79a29d86b126d8173caaabba14`
- `src/AgentForge.Models/LocalModelInteractionService.cs`: `9f2ff9333f5915b0460b1f76924310a806bd2f1e444d488c110b7de2870ab16c`
- `tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs`: `035a7ee573ceede9759da9d89e963eefb67f9f863fa2bdfb198577b8ce272f2c`
- Baseline commit: `98ff68cc59c69addfa3776439dbae03cc488ec02`

## Rollback

Stop the host and revert this slice as one commit. No migration, protected-secret, or artifact rollback is
required. Existing configured-run and skill-snapshot receipts remain valid hash-bound evidence and must not be
deleted or rewritten. Rollback intentionally restores the fixed stream title/output limit, removes optional run
guidance and governed skill selection, returns history to its unpaginated list, and restores the previous SDK
feature-band pin.
