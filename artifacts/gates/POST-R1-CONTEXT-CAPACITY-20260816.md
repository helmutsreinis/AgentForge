# POST-R1 Context Capacity and Compression Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Use an endpoint-reported model context length as a provenance-bearing ceiling, let an operator version a lower
per-agent override, and make context occupancy and compression behavior visible during Ready runs. This slice does
not allow an override above the endpoint ceiling, alter the immutable full transcript, introduce model tools, or
grant model network authority.

Requirements: `AF-ADMIN-008`, `AF-RUN-002`, `AF-MODEL-001`, `AF-DATA-001`.

## Security, durability, and portability disposition

- Compatible catalog discovery already parses bounded vendor `max_model_len`; the Ready session binds each
  observation to exact provider ID/version and model. Durable policy records both discovered value/model and an
  optional override. Domain validation permits only a lower override and bounds thresholds and protected turns.
- Migration 0030 adds an optional one-to-one `agent_context_policies` table. Missing rows map to conservative
  80%/50%/four-turn defaults, so every historical agent and prior-schema upgrade remains valid.
- Multi-turn preparation uses the effective ceiling, reserves the requested output, keeps the configured recent
  tail intact, and replaces only the older active prefix with a bounded deterministic extractive summary. Original
  redacted prompt/answer artifacts and their snapshot hash chain remain untouched and inspectable.
- SSE and authenticated run details expose capacity, estimate, occupancy, provenance, threshold, target, protected
  count, and whether compression occurred. Estimates use a conservative portable character heuristic; provider usage
  is retained as authoritative post-call evidence.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx -c Release --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build --no-restore
PASS — 424 product tests + 2 framework-spike tests;
4 expected environment-gated skips

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Release \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — legacy request bodies receive the new conservative context defaults

dotnet ef migrations has-pending-model-changes --project src/AgentForge.Persistence \
  --startup-project src/AgentForge.Persistence --configuration Release --no-build
PASS — no pending changes

node --check src/AgentForge.Host/wwwroot/app.js
PASS

./scripts/verify-no-secrets.ps1
PASS — 791 repository candidates

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable packages
```

## Live endpoint and browser evidence

- Credential-free private vLLM profile `qwen3.8` returned `max_model_len: 262144` through the bounded `/models`
  discovery adapter.
- Opening `Edit agent` automatically rendered `262,144 tokens · qwen3.8` with source `endpoint discovery`.
- Browser validation rejected `300000` with the exact discovered-ceiling message and accepted `131072`, immediately
  rendering `Effective context: 131,072 tokens · operator override`. No policy was applied during this smoke test.
- Release host restarted against the existing Ready data directory and `/health/ready` returned Healthy.

## Evidence hashes

```text
232fe35d481bb4c2a31b48ba3061daedc59fe199b54128d92442c8fab9fa88c5  AgentRecords.cs
265c15ee419861a2d201d2e2554ae81717de57d756f483a60dfcb54d5d9916ff  ReadyAdminConversationEndpoints.cs
8dfec03b653b4669a8521e0c69a22de649a69ed12b9078184af826b6279ef98b  20260816193431_AgentContextCapacityPolicy.cs
401c0e088ba1d8fff58a3cc72147d6637b40e595b2205990fd4d656c930ed6b4  app.js
a1b6a90097a446e8420221788c313918ad003c57868b1ca01080643a6949aebd  AgentDefinitionEvaluatorTests.cs
```

## Rollback and recovery

Before downgrade, stop the host and back up the complete data directory. Migration 0030 drops only optional context
policy rows; agent identities and all conversation artifacts remain intact. After downgrade, agents fall back to the
existing combined input/output token budget and older bounded-history behavior. If endpoint discovery is unavailable, no discovered
ceiling is fabricated; the operator may retain the last model-bound observation or use a conservative manual value.
