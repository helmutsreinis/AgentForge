# POST-R1 Governed Model Search and Primary-Agent UX Gate — 2026-08-17

Result: **Pass**

## Objective and scope

Connect one model-facing capability—Brave web search—to AgentForge's existing exact policy, approval, credential,
tool, audit, and durable-conversation boundaries, and make the primary-agent editor understandable without removing
advanced controls. This slice does not grant a network client or credential to the model, permit another endpoint,
expose workspace tools to the model, add file writes or shell execution, or make approval optional.

Requirements: `AF-ADMIN-010`, `AF-RUN-002`, `AF-TOOL-002`, `AF-SEARCH-001`, `AF-SEC-001..005`.

## Security and durability disposition

- The model sees only `search_web` with a strict query/result-count schema when the current agent has exact
  `tool:search.web`, `ApprovedEndpointsOnly`, a positive tool ceiling, and a compatible transport or observed tool
  support. Compatible vLLM/OpenAI-compatible transport support is labeled `Overridden`, never fabricated as probed.
- The catalog exposes `tool:search.brave@1.0.0` with Credential risk, network-read/credential side effects, one fixed
  official HTTPS endpoint, a 1–10 result limit, bounded output/time, BuiltIn execution, and immutable provenance.
- Model arguments are parsed again by the host, canonicalized by the invocation planner, and rejected on extra,
  duplicate, malformed, oversized, or unknown fields. An unknown/ungranted tool durably fails the task and turn.
- A valid call becomes a hash-chained durable conversation checkpoint and `ApprovalRequired` task failure. Resume is
  impossible while the call is unresolved. The authenticated preview binds actor, installation/agent versions,
  descriptor, official endpoint, normalized parameters, workspace, expiration, correlation, and request hash.
- Grant consumes one exact approval before the managed Brave handler invokes the already-versioned research profile.
  Denial performs no network request. The OS-backed key remains inside the search adapter; it is absent from model
  context, approval display, conversation state, logs, audit, and exports.
- Result JSON is bounded and hash-chained. Continuation supplies one typed assistant tool call plus one typed tool
  result to the same durable turn. Restart fixtures prove a pending call and resolved result survive process restart.
- The primary-agent editor now leads with profile, two friendly capability switches, response/context sizing, and
  memory/learning. Raw routing/grants, secondary budgets, child policy, and model replacement remain available under
  advanced disclosures. Typed IDs/enums are emitted as valid string form values and exact previews show names rather
  than numeric enum wire values.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore
PASS

dotnet build AgentForge.slnx --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx --no-build --no-restore
PASS — 432 product tests and 2 Agent Framework spike tests
SKIP — 5 named equipped/live tests (Docker, Brave env, compatible-provider env, PostgreSQL backup/restore)

node --check src/AgentForge.Host/wwwroot/app.js
PASS

HTML ID uniqueness scan
PASS — 332 IDs, 0 duplicates

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable direct or transitive package

./scripts/verify-no-secrets.ps1
PASS

git diff --check
PASS
```

Unit coverage proves strict tool admission, buffered tool-call streaming, typed continuation, compatible-transport
override evidence, fixed-endpoint descriptor rules, agent-policy intersection, and malformed/unlisted-call denial.
SQLite integration proves pending-approval restart, exact denial/result resolution, continuation, completion, and
conversation hash-chain integrity. The complete architecture, security, cross-platform, integration, and end-to-end
suites pass together.

## Live browser evidence

- The existing `local-agent` was edited through the simplified controls. The exact preview changed only network
  posture from `Denied` to `ApprovedEndpointsOnly` and added `tool:search.web`; policy version 4 was persisted.
- Context reported Brave `READY` with its existing OS-backed secret and one available provider. No credential was
  read or copied into the repository or gate transcript.
- The Release host advertised `Brave Search proposals only` and `Fixed endpoint · exact approval` while files,
  messaging, devices, fallback, and every other network route remained denied.
- Live `qwen3.8` emitted `search_web` for `.NET 10 support end date official Microsoft`. Conversation
  `1468cf38-57c9-2add-b980-f457435e3336` durably paused as `NeedsResume` before network access.
- The UI displayed the exact query, five-result ceiling, official Brave endpoint, OS-backed credential boundary,
  expiration, and request hash. One explicit approval executed Brave, resumed the same turn, and completed with cited
  Microsoft Learn and .NET Blog sources. The terminal conversation is version 5 with snapshot
  `sha256:dd43db8e688…`.

## Content evidence

```text
5e2464f0143cfb6b8ea89bd74295990c7395587a5c6658255f465343aa316f7e  src/AgentForge.Host/Http/ReadyAdminConversationEndpoints.cs
72264188e832305508ca7cb3b4880889368e6ac66c88f6f12150df12e9417766  src/AgentForge.Host/Http/ReadyAdminEndpoints.cs
ecd0d0cd2dba015807ca8eb32ba88394f335ada40870634b02ee8daa370d337c  src/AgentForge.Domain/Runtime/RunConversationRecords.cs
ce6cff13ddf0ec5255143cb3034448e9219b83391fa946408c400d2ac2193ca0  src/AgentForge.Models/LocalModelInteractionService.cs
be942d9f295f7ec367c4fb562cc2001bcee9f84cb77ec93cf742a61c8979d4e7  src/AgentForge.Search/BraveSearchBuiltInToolHandler.cs
d29ce0ef405e972719a6674e41532460032942bfd4ce7892e9e840224fe19c25  src/AgentForge.Tools/ServiceCollectionExtensions.cs
a0f03a0c5513652a32cba58ef3cf0e0933834c20b873c70af1576eef29841e4c  src/AgentForge.Host/wwwroot/index.html
39ef3b5a71e9db72af6ac749e0bf4b770968edba41370759d2ed23441d11db43  src/AgentForge.Host/wwwroot/app.js
dfe431f7bd567c6402acd98275ff052a56990a9cda197f3a7d06a1c22ddd4212  src/AgentForge.Host/wwwroot/styles.css
15d5749216f2dcbb5fd75a7f7b5bbc9ee338d84dab9edd6e5864aaf6df1f46bc  tests/AgentForge.IntegrationTests/RunConversationPersistenceTests.cs
```

## Rollback and recovery

Uncheck **Search the web with Brave** in the agent editor, review, and apply. This removes
`tool:search.web`, returns fixed-endpoint network posture to `Denied`, and prevents new model search proposals.
Outstanding approvals are version-bound and cannot survive that policy change. Disable or rotate Brave separately in
Context when required; rotation invalidates stale search evidence without exposing the old key. Historical
conversation, approval, invocation, and audit hashes remain append-only. Code rollback needs no schema migration
because tool-call checkpoints use the existing JSON conversation snapshot artifact.
