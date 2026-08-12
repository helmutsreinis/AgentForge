# Gate POST-R1-INTERACTIVE-MVP-20260812

Decision: **Pass**

## Scope

- AF-ADMIN-001, AF-HOST-003, AF-MODEL-001..002, AF-RUN-001, AF-AUD-001, and AF-SEC-003..004.
- Make the Ready-state Runs page useful for an explicit single-turn test against the selected agent's pinned
  local model.
- Keep arbitrary tool network denied while treating the exact model destination as a separate, narrower route.
- Persist a durable orchestration receipt without persisting raw prompt or response text.
- Keep tools, fallback, browsing, files, messaging, skill promotion, and device access unavailable.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet build AgentForge.slnx --no-restore -c Release` | Pass; 0 warnings, 0 errors |
| Focused `LocalModelInteractionServiceTests` | Pass; 4/4 including private-route restriction, tool-call denial, and output bound |
| Focused `WebSetupWizardTests` | Pass; 2/2 including success, exact replay, conflicting replay, and durable Completed receipt |
| Complete Release product suites plus framework spike | Pass; 402 product tests plus 2 spike tests; 4 named live/equipped skips; 0 failures |
| `dotnet format AgentForge.slnx --no-restore --verify-no-changes` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| Live loopback/private prompt | Pass; `qwen3.6` returned exact `AGENTFORGE_UI_OK` with 87 input and 6 output tokens |
| Live durable receipt | Pass; `Interactive local model test` reached Completed snapshot v2 |
| Persisted-data byte scan | Pass; neither the live raw prompt nor response appeared under the installation data directory |
| Browser DOM and console | Pass; prompt, response, usage, provider/model, policy explanation, and durable receipt rendered without observed runtime error |

The complete suite was run with the installed .NET 10.0.400 feature band selected temporarily so the nested
MSBuild/Roslyn workspace fixture could load. The repository's checked-in `global.json` was restored unchanged
to 10.0.302. The four skips remain the Docker-equipped sandbox, credential-gated compatible-provider stream,
and two live PostgreSQL cases.

## Security and portability review

The Ready session, exact-origin, loopback, CSRF, rate-limit, and idempotency controls remain unchanged. The new
endpoint resolves an installation-owned exact agent and provider, then requires `LocalOnly`, no fallback, a
zero tool budget, a credential-free vLLM or generic compatible profile, and a literal loopback/private HTTP(S)
destination. Existing destination-policy socket handling still pins the resolved address and disables proxy,
cookies, and redirects.

The interaction contains only system and user text, passes through the existing context preparer, requests no
tools, and rejects any emitted tool-call or structured-output event. Output tokens are capped at 2,048,
provider stream events at 4,096, wall time at 120 seconds, prompt text at 16,384 characters, and assembled
response text at 32,768 characters. Only 32 raw success responses can remain in the short-lived in-memory
operator-session replay cache.

The orchestrator owns the single-node lifecycle and records exact policy/budget/skill hashes. Completion stores
the interaction evidence hash; failure stores a typed failure evidence hash. Raw prompt and response text are
not placed in the definition, snapshot, audit, artifact store, or database. A lost session cannot regenerate a
terminal response from the model: durable replay returns a typed conflict instead.

This slice uses the existing compatible adapter and is portable across Windows and Linux. Credentialed hosted
profiles intentionally remain unsupported by the interactive MVP endpoint even if they would otherwise satisfy
model capabilities.

## Evidence SHA-256

- `src/AgentForge.Abstractions/Models/ILocalModelInteractionService.cs`: `54c840d8780c70bfbb26b18d7e0e49b7e6fe22f518825b63fe8cf2f596dcee41`
- `src/AgentForge.Models/LocalModelInteractionService.cs`: `3b804b0289402fa162b2b2f4f705503367356bba40b7e0edc3cf50ef54943f39`
- `src/AgentForge.Models/ServiceCollectionExtensions.cs`: `045ed0b8e5c172fb26b1c8fe9f4cf591a8cabd87c6ef9d9fc23a4a4b86510893`
- `src/AgentForge.Host/Http/ReadyAdminEndpoints.cs`: `c915ca900a53ab3323086c029b42d90ccffd3abeb033e8bdcfde4bc156d56511`
- `src/AgentForge.Host/Http/ReadyAdminSessionManager.cs`: `77b56d2fe2d97af2b9bdacae65cf9a2cfe4b5af1f38ecb882dd095271fb3efe1`
- `src/AgentForge.Host/wwwroot/index.html`: `bcdddf1e90b11cb2a60ea5ca43b8e010d28fe3c6412172cf712cc1786bf614b2`
- `src/AgentForge.Host/wwwroot/app.js`: `79d89a42e55c71c9df99da672701b4cdae1ec43b9c88c825011f4286d8e32db5`
- `src/AgentForge.Host/wwwroot/styles.css`: `234a508df60c62fc88aa7578f5694b28061665252e84565abca0f004b9cd5fed`
- `tests/AgentForge.UnitTests/LocalModelInteractionServiceTests.cs`: `6acec3e9d1b6ae17eabc68e86016cfd26c0d5d18ce4c7c676fb96ee7ce8acd92`
- `tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs`: `6446f68822d630f09e159e5dd8c145530f3f0202c7694d0e5761bbd13c95a74b`
- Baseline commit: `cd0b78b`

## Rollback

Stop the host to clear transient Ready sessions, then revert this slice as one commit. No schema or secret
format changed. Existing interactive task snapshots remain valid hash-only orchestration evidence and need not
be deleted. Never remove or rewrite the active installation data directory as part of code rollback.
