# AgentForge Architecture

## Product shape

AgentForge starts as a local-first modular monolith. One ASP.NET Core host composes
feature modules, background workers, health, and the control plane. A separate CLI
uses the control plane and may compose setup/recovery services in-process when the
normal host is unavailable.

The root loopback page is currently a read-only diagnostic adapter over existing
same-origin health, setup-status, and sandbox-capability GET endpoints. It contains no
form, secret input, session, or mutation path. This gives operators an early visual
first-run surface without claiming the Milestone 7 authenticated setup wizard; the full
wizard must reuse setup application services after its nonce/session/CSRF/idempotency
gate passes.

```mermaid
flowchart LR
  Operator["Local operator"] --> CLI["agentforge CLI/TUI"]
  Operator --> Web["Loopback setup UI (M7)"]
  CLI --> API["Authenticated /api/v1 + event stream"]
  Web --> API
  API --> Setup["Setup application services"]
  API --> Runtime["Agent runtime"]
  Runtime --> Policy["Policy and approvals"]
  Runtime --> Orchestration["Durable task orchestration"]
  Runtime --> Catalogs["Tool and skill catalogs"]
  Catalogs --> Sandbox["Restricted execution adapters"]
  Orchestration --> Persistence["Repository contracts"]
  Setup --> Persistence
  Policy --> Persistence
  API --> Audit["Audit application service"]
  Audit --> Redaction["Structured redaction"]
  Audit --> Persistence
  Persistence --> SQLite["SQLite / PostgreSQL"]
  Persistence --> Artifacts["Content-addressed artifacts"]
```

## Dependency rule

`Domain` depends only on the BCL. `Abstractions` depends on Domain. Feature modules
depend on Domain and Abstractions, not other concrete feature modules. Host and CLI
are composition roots. Cross-feature work is coordinated through application
contracts and durable events, not direct implementation references.

## Startup and setup flow

```mermaid
stateDiagram-v2
  [*] --> Uninitialized
  Uninitialized --> Configuring: begin setup
  Configuring --> Validating: submit candidate profile
  Validating --> Ready: deterministic gates pass
  Validating --> RecoveryRequired: validation fails
  Configuring --> RecoveryRequired: state cannot be persisted safely
  Ready --> RecoveryRequired: startup invariant fails
  RecoveryRequired --> Configuring: authorized repair
```

In M0, absence of state means `Uninitialized`; unreadable state means
`RecoveryRequired`. Liveness remains healthy, readiness is unhealthy, and runtime
endpoints return a typed 503 until `Ready`. M1 moves state transitions into a
transactional setup service and emits audit/outbox records in the same transaction.

The first M1 command now performs `Uninitialized → Configuring` through
`ISetupApplicationService`. Interactive prompts and explicit headless options are
input adapters only; the same validation, transition, redaction, repository, audit,
and unit-of-work path handles both. Provider and agent validation remain closed, so
this command cannot reach `Ready`.

Provider setup persists endpoint/model metadata and observed deterministic capability
evidence, but credential material is represented only by `SecretReference`. Windows
uses current-user DPAPI-protected files; Linux invokes the known `secret-tool` binary
with argument arrays, bounded streams, an environment allowlist, timeout, and
process-tree cleanup. Validators materialize a `SecretLease` for one call and clear its
character buffer on disposal. An unavailable OS facility is a typed failure, never a
plaintext fallback.

Agent identity is a versioned aggregate separate from provider identity. Its model
policy points to a provider profile without copying credential material or making the
provider part of the agent's identity. The setup evaluator normalizes the candidate,
checks provider evidence and locality, then renders explicit `Allow`, `Deny`, or
`RequireApproval` decisions. Unknown or not-yet-configured authority is absent and
therefore denied. Preview performs no writes; create repeats evaluation and commits
the exact bounded definition with one redacted audit event.

The headless CLI composes the same setup service for agent preview/create. Its secure
defaults are local-only routing, denied network access, no tool/skill grants, bounded
budgets, no children unless all child bounds are supplied, and `Propose` learning
with proposal-workspace-only mutation. These records are policy inputs; M2 adds the
general per-invocation authorization evaluator and approval binding.

Setup completion is the only application path to `Ready`. It verifies the current
audit chain, a same-installation named agent, an observed text provider, and live
materialization of each usable provider reference. It then creates a 256-bit random
administrator credential, stores the client value in the selected OS secret store,
and persists only its reference plus PBKDF2-SHA256 verifier. The two pure state
transitions and administrator/audit rows commit atomically; failed commits delete the
new external credential. Ready runtime requests require fixed-time administrator
authentication.

## Stable seams

- Provider, model, tool, skill, scheduler, channel, device, artifact, secret, audit,
  and persistence implementations stay behind AgentForge-owned interfaces.
- Framework and vendor SDK types never cross Domain or public control-plane contracts.
- Every run pins hashes/versions of policy, provider routing, environment, tools,
  skills, and artifacts.
- External content is evidence. It is never interpreted as policy or system-level instruction.

## Restricted execution boundary

`AgentForge.Tools` implements `ISandbox` without referencing another feature
implementation. Its restricted-host adapter starts only a fully qualified, existing,
non-link executable with `ProcessStartInfo.ArgumentList`, clears the child environment,
contains the working directory, and bounds output, time, cancellation, and tree cleanup.
Capability flags describe only controls the selected OS adapter enforces. Unsupported
container, network, filesystem, or resource isolation fails typed.

The kernel is deliberately not an authorization service and has no invocation endpoint.
The tool application service builds an authorization context from its immutable
descriptor, re-reads current agent policy, atomically consumes exact approval evidence and
appends audit state, then calls `ISandbox`. Inventory paths or model-supplied tool identity
can never cross that boundary directly.

## Authoritative tool catalog

`AgentForge.Tools` also implements `IToolCatalog` as an immutable map keyed by exact
`(tool ID, version)`. A descriptor owns the callable executable path, typed parameter-to-
argument bindings, capability and risk, target mapping, side effects, provenance,
sandbox/network requirements, and time/output bounds. Admission validates and snapshots
the complete definition before producing its canonical descriptor hash.

Discovery is deliberately progressive. Search returns `ToolSummary` records without
process paths, fixed arguments, bindings, or environment names. Full description requires
an exact normalized ID and SemVer 2 version and never selects a nearby or latest version.
Neither operation checks executable availability or starts a process. The later invocation
service accepts values for the descriptor's parameters, not caller-authored executable,
risk, capability, or isolation fields, and pins the descriptor hash into authorization,
audit, and run evidence.

## Policy-bound tool invocation

`IToolInvocationService` is the only application contract permitted to connect a catalog
descriptor to `ISandbox`. Its request contains exact catalog identity, typed parameter
values, current installation/agent versions, workspace, correlation, and idempotency. It
derives every capability and process field from the descriptor, canonicalizes the values,
builds authorization identity including the descriptor hash, intersects current agent
network policy, and evaluates policy plus exact approval.

The first EF unit of work consumes a single-use grant, inserts an `Authorized` invocation,
and appends authorization audit. The sandbox is called only after that commit and a second
policy/version read. A second unit of work persists terminal state and completion audit.
Durable rows retain output SHA-256/length evidence, not raw bytes. An exact terminal retry
returns recorded status without execution; an uncertain `Authorized` row or changed input
under the same idempotency key fails closed.

The service has no control-plane or CLI mutation surface in this slice. Production
composition has an empty catalog, and unavailable network/container isolation remains a
typed `UnsupportedCapability` instead of falling back to restricted host.

## Safe availability probes

Availability is an explicit descriptor operation, never a side effect of inventory,
search, catalog admission, or exact description. `AvailabilityProbe` admission requires
the exact `tool:availability.probe` inventory capability, no target, parameters, side
effects, inherited environment, or network, and a fixed/literal version or help argument.
It caps execution at 30 seconds and 64 KiB and requires a sandbox reporting network
isolation.

`IToolAvailabilityProbeService` delegates the empty typed invocation to the same immutable
descriptor, current policy, exact approval, durable idempotency, audit, and `ISandbox`
boundary. A successful immediate result may expose only the first nonempty printable
strict-UTF-8 line, after full-line credential redaction, capped at 512 characters. Invalid
encoding, sensitive output, and durable replay expose no observed text. Production still
has no public probe route and composes an empty catalog.

## Durability strategy

SQLite with WAL is the zero-dependency store. Aggregate version columns provide
optimistic concurrency; leases guard work ownership; unique idempotency constraints
and inbox/outbox tables prevent duplicate external effects. Large immutable data is
content-addressed outside relational rows. PostgreSQL later implements identical
repository contracts.

## Provider-neutral model boundary

`AgentForge.Domain.Models` owns typed messages, artifact-backed attachments, response
formats, tool definitions/results, capability evidence, budgets, usage/cost, provider
errors, and sequenced stream events. `AgentForge.Abstractions.Models` exposes only
`IModelProvider` and the exact-profile `IModelProviderCatalog`. Domain has no SDK/package
dependency, and vendor records cannot cross this boundary.

`AgentForge.Models` validates and snapshots requests before asynchronous enumeration.
JSON schemas, tool results, tool arguments, and structured output are bounded, parsed with
fixed depth, reject duplicate object keys, and normalize before use. Attachment content is
an immutable artifact hash/media type/length/modality reference; the input fingerprint
includes that reference so an image or document cannot be silently omitted without
changing evidence. Capability evidence records source, availability, observation time, and
optional expiry; any unavailable, unknown, temporarily failing, future, or expired evidence
fails closed for that capability. The started event pins separate normalized input and
sorted capability-evidence SHA-256 values.

The deterministic provider owns immutable scripts and emits started, text delta, tool-call
delta/completion, structured output, usage, typed error, and completion events with strict
sequence numbers. It intersects the request with provider evidence and token/tool/event/
time limits, honors cancellation, and never emits completion after a scripted failure.

The OpenAI-compatible adapter translates prepared normalized requests to an exact
chat-completions endpoint. HTTPS is required unless its composition explicitly opts into
plaintext HTTP for a local/LAN endpoint. Endpoint credentials, query/fragment data,
redirects, caller-owned HTTP headers, cookies/proxies, and declared media
capabilities are refused. Production construction owns a fixed no-cookie, no-proxy,
no-redirect transport; only the unit-test assembly can inject a fake message handler.
The adapter bounds serialized requests, total response bytes, individual SSE lines/events,
strict UTF-8, JSON depth/duplicates, tool argument accumulation, event count, output usage,
and wall time. Structured output is accumulated and validated before one atomic event;
provider error bodies never become application messages. Tool results preserve their error
bit in a normalized JSON envelope and returned tool calls must match an exact request tool.

`IModelContextPreparer` now snapshots every external request before adapter validation or
serialization. It applies the shared structured redactor to message text, attachment names,
tool arguments/results, and descriptions; sensitive identifiers and JSON contracts fail
because rewriting them could change routing or schema authority. The prepared record carries
the exact `agentforge-context-redaction-v1` policy and count, and the started event plus input
hash describe the prepared request rather than the caller's mutable raw object.

Hosted compatible construction is a separate exact-profile path. It requires matching
profile/descriptor ID, type, model, capability posture, HTTPS endpoint, version, and secret
store/reference. The adapter retains only that reference. After emitting started evidence,
it materializes the secret into a clearable lease, accepts only a bounded visible-ASCII
bearer value, sends headers, then removes `Authorization` and clears the lease in `finally`
before translating any response event. Missing/malformed material or profile substitution
fails without a transport call. The BCL necessarily creates a transient managed header
string; removing the header immediately minimizes its lifetime, while logs, state, events,
and request bodies never receive it.

Production DI still contains an empty immutable provider catalog and no invocation route.
The compatible adapter is composed only by tests or an explicit credential-free live gate.
Hosted adapters may use vendor SDKs or `Microsoft.Extensions.AI` only inside the feature
boundary and must still add current-profile re-read, locality/policy routing, destination
controls, audit, and durable run snapshots before exposure.

## Model routing boundary

`IModelRouter` consumes the immutable provider catalog plus a trusted effective
`AgentModelPolicy`; it never accepts an endpoint, credential, provider adapter, or policy
rule from model content. Descriptors add bounded routing evidence for data location, policy
approval, context/output windows, reliability, cost, latency, and evidence lifetime. Routing
filters in a stable order: exact model and attempt exclusions, current modality capability,
data locality, current policy approval, context/output capacity, and tool support.

A viable exact primary profile wins. Only an explicitly enabled fallback is considered, and
its ordering is deterministic by reliability, known combined cost, latency, and profile ID.
Media parts produce required capabilities and remain in the normalized request; no route can
strip an attachment to become eligible. The result snapshots the required capability set and
a SHA-256 of the decision inputs plus selected provider evidence.

This component is selection logic, not a runtime authorization service. Production still
registers an empty catalog and exposes no invocation route. The later boundary must re-read
the durable agent/profile and current health, enforce destination policy, audit the selection,
reserve cumulative budget, persist a run snapshot, then resolve the exact adapter.

## Health-aware route planning

`IModelRoutePlanner` is the scoped bridge from durable authority to pure routing. It first
uses the versioned context preparer, so route/request hashes never bind the caller's raw
secret-shaped payload. Persistence implements `IModelRouteAuthoritySnapshotReader` with a
serializable read of the one installation, exact agent, and installation provider profiles.
The planner requires caller versions, `Ready`, the exact model named by the agent's durable
primary provider, and per-request limits within the current agent budget.

`IModelProviderHealthSource` supplies bounded immutable evidence rather than remote error
bodies. Each exact profile has at most one normalized record with typed status/source,
consecutive-failure count, safe evidence code, observation/expiry, and optional retry time.
Evidence lifetime is at most 15 minutes. Missing, future, expired, unknown, temporarily
unavailable, or already-attempted profiles become exact exclusions; attempt history is unique
and capped at eight.

After selection, the planner re-reads authority and health and repeats eligibility. Route-
relevant durable changes fail with `ConcurrencyConflict`; health changes fail retryably. The
result contains no endpoint, secret, or request content. It pins prepared input, context policy,
installation/agent/provider versions, route/health evidence, and expires after at most five
seconds. It is diagnostic planning evidence, not permission to call a provider.

Production provider and health catalogs are both empty. The admission boundary below now
persists run/attempt reservation and audit, while exact adapter resolution and provider start
remain disabled. No public model route exists.

## Durable model run admission

`IModelRunAdmissionService` consumes a caller request only through the existing context preparer
and route planner. Context preparation supplies a redacted effective-input hash for idempotency;
the planner supplies exact versioned route, prepared-input, health, and short-lived plan evidence.
Admission verifies identity/version/reservation agreement and uses the pure
`ModelRunStateMachine` to create one `Reserved` run and one `Planned` attempt.

Persistence maps the aggregate to `model_runs` and `model_run_attempts`. A unique
installation/idempotency key plus fixed-time admission-hash comparison distinguishes exact
replay from conflicting reuse. The two rows and one redacted `model.run-reserved` audit event
share an EF unit of work. Durable fields are limited to IDs, versions, bounded typed route and
capability evidence, hashes, reservations, usage, state, timestamps, actor/correlation, and
terminal classification; request content and provider connection material have no schema path.

Run transitions are pure domain code: reservation can start once; a started attempt can succeed
or fail only within its input/output/tool/time reservation; observed overage becomes
`BudgetExceeded`; reserved or running work can cancel once. Versions advance on every transition.
Admission deliberately stops before start. Production catalogs remain empty and no host/CLI
model mutation exists, so a durable reservation cannot yet cause provider egress.

## Durable model attempt execution

`IModelRunExecutionService` is the internal bridge from admission to one exact provider attempt.
It reconstructs current planning from the caller request plus persisted attempt history, requires
all route/input/health/reservation evidence to match the reserved aggregate, re-reads Ready
installation/agent authority, and resolves the exact selected catalog entry. No caller can pass an
adapter, endpoint, credential, budget, route, or policy object into this boundary.

The start transaction advances both run records, stores only a random lease-token hash, reserves
the exact run dimensions in a versioned `model_budget_ledgers` row, and appends redacted start
audit. The provider is enumerated only after commit. A stream accumulator checks identity, route,
prepared-input hash, contiguous sequence, timestamps, typed bounds, usage cardinality, terminal
ordering, and the deliberate tool-call denial. It incrementally hashes canonical events; content
is available only to the optional in-process observer and is not returned by replay or persisted.

One terminal transaction writes safe usage/state/stream evidence, reconciles the exact active
reservation into cumulative consumption, and appends terminal audit. Malformed streams become
retryable failure, overages become `BudgetExceeded`, and caller cancellation becomes durable
`Canceled` evidence before cancellation propagates. Optimistic ledger/run versions make a race
fail atomically rather than partially starting or releasing work.

This slice executes only when an embedding application deliberately populates the provider and
health catalogs. Production composition leaves both empty and has no API/CLI model mutation.
Expired-lease recovery, heartbeat/takeover, health writes, additional attempts/failover,
cross-attempt cost accounting, and destination/DNS revalidation remain later boundaries.

Audit callers submit typed metadata plus raw structured payloads to the Audit module.
The Security module canonicalizes and redacts those payloads before the Persistence
journal can receive them. Hash fields are length-prefixed before SHA-256 processing,
and a separate verifier streams the global chain to identify the first broken event.

## Framework spike decision

Microsoft Agent Framework is optional adapter material, not the orchestration source
of truth. The pinned spike verifies typed handlers, streamed workflow events, and
cancellation-token propagation. AgentForge retains tasks, leases, retries,
compensation, audit, approvals, snapshots, and promotion because those invariants
span more than a framework workflow execution.
