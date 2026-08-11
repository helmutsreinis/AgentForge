# Development Runbook

## Schedule recovery

Schedules require the exact configured timezone on the host. Use preview before activation and
after timezone database or OS upgrades. A paused schedule retains its next base/due instant;
resuming applies configured misfire policy when the dispatcher evaluates it. `Skip`, `FireOnce`,
and bounded `CatchUp` are deliberately different operator choices.

If a worker exits, its occurrence remains `Running` until the five-minute-or-shorter lease expires.
Recovery requeues within the attempt bound or increments failure/dead-letter evidence. Never edit
SQLite due times, occurrence JSON, or hashes. Stop the service and restore the complete SQLite/WAL/
SHM backup if chain validation fails.
## Session start

1. Read `PROJECT_STATE.md`, `REQUIREMENTS.md`, `TRACEABILITY.md`, open threat findings, and the latest gate report.
2. Inspect `git status --short --branch` and never overwrite unrelated changes.
3. Verify `dotnet --info`; check WSL/container availability when the slice needs it.
4. Resume the highest-priority unblocked requirement and record its exit gate.

## Standard verification

```text
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
dotnet format --verify-no-changes --no-restore
```

For Linux verification from this Windows workstation:

```text
wsl.exe -d Ubuntu-24.04 -- bash -lc "cd '<repository mounted path>' && dotnet --info && dotnet restore --locked-mode && dotnet build --no-restore --configuration Release && dotnet test --no-build --configuration Release"
```

Run `scripts/verify-linux-smoke.sh` after the Linux Release build. Set `DOTNET_BIN`
when using a repository-local SDK.

Run `pwsh -File scripts/verify-windows-smoke.ps1` after the Windows Release build.
Both smoke scripts use validated temporary data directories and disable SQLite
connection pooling only for deterministic cleanup.

Windows and WSL builds share this checkout and therefore share generated `obj`
assets. After switching operating systems, run that platform's locked restore before
build, format, package inspection, or design-time tooling; package-cache paths are
platform-specific even when lock files are identical.

## Local smoke

Start `dotnet run --project src/AgentForge.Host` and open `http://127.0.0.1:5047/`.
The read-only preview shows installation, host, runtime-readiness, and sandbox evidence
from same-origin GET endpoints. Confirm `/health/live` is 200, `/health/ready` is 503
on a clean installation, `/api/v1/setup/status` is available, and
`/api/v1/runtime/ping` is 503. The CLI returns exit code 2 for setup-required.

The preview intentionally has no form or mutation route. Complete setup through the CLI;
do not add browser credential entry until the one-time nonce, authenticated session, CSRF,
rate-limit, audit, and exact-idempotency controls pass their web-wizard gate.

Begin a deterministic offline setup transaction with:

```text
agentforge setup begin --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id> --installation-id <guid>
```

Use `agentforge setup begin --interactive` for prompts backed by the same service.
Success returns one JSON object and exit code 0. Validation/state failures return
JSON and exit code 1; a retryable concurrency conflict returns exit code 3; Ctrl+C
returns 130. The command stops at `Configuring` so provider and agent setup can run.

Configure a provider without placing its credential in shell history or the process
argument list. For automation, send exactly one bounded line through standard input:

```text
<secret-producing-command> | agentforge setup provider configure --data-directory <absolute-path> --name primary --type deterministic --endpoint http://127.0.0.1:9000/v1 --model deterministic-text-v1 --credential-stdin --actor <actor-id> --correlation <correlation-id>
```

Use `--credential-prompt` instead of `--credential-stdin` at an interactive console.
The prompt does not echo characters. The CLI clears its input buffer after the shared
setup service has stored the value through the OS secret adapter. Do not provide a
secret-producing command that exposes its value in its own command line or logs.

## Secret-store diagnostics

On Windows, AgentForge stores provider credentials as current-user DPAPI-protected
files under the configured data directory's `secrets` folder. Copying these files to
another user does not make them decryptable. On Linux, install `secret-tool` and run
AgentForge inside a working Secret Service/DBus session. `agentforge doctor` reports
the store as unavailable when this facility is absent; it never falls back to
plaintext. Never place provider secrets in CLI arguments, configuration, SQLite,
migration fixtures, logs, audit, exports, or gate reports.

Backups preserve provider database rows and OS secret references together. Restoring
only SQLite can leave valid-looking but non-materializable references; validate every
reference before proceeding beyond setup.

## Agent policy preview and creation

After a provider profile has been validated, preview a conservative named-agent
definition without writing state:

```text
agentforge setup agent preview --data-directory <absolute-path> --name <name> --provider-id <guid> --actor <actor-id> --correlation <correlation-id>
```

Use `setup agent create` with the same options to persist the previewed defaults.
Optional flags configure model locality/fallback, memory scope/retention, network
posture, budgets, child bounds, learning mode, and mutable-skill scope; `--help`
lists the command shape. Always run preview first and inspect every capability
decision. `Deny` is the default for external network, credentials, messages, device
writes, privileged execution, and learning promotion. Exact tool/skill grants are
accepted with `--tool-grant` or `--skill-grant` and remain approval-gated. The current
CLI accepts one grant of each type per profile command; use authenticated profile edit
for later changes. A configured grant is not permission to invoke a process.

Creation is allowed only while installation state is `Configuring`, requires a same-
installation provider with observed text capability, and returns JSON. Exit codes are
0 for success, 1 for validation/policy/state failure, 3 for a retryable write conflict,
and 130 for cancellation. Agent creation does not transition the installation to
`Ready`; minimum viability and administrator bootstrap remain separate gates.

Complete minimum-viability validation with:

```text
agentforge setup complete --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id>
```

The command verifies the audit chain, usable text provider and secret reference, and
named agent before it creates the local administrator and reaches `Ready`. Output
contains only the OS secret reference, never credential material. Keep the data
directory and OS secret-store account together; losing the client reference requires
the later authorized recovery flow. On Linux, a working Secret Service session is a
live prerequisite. The deterministic application-service fixture remains the
credential-free acceptance path in CI.

Ready runtime calls require `Authorization: Bearer <credential>`. Retrieve the value
through the OS secret reference for one invocation and clear it immediately. Never
copy it into shell history, process arguments, configuration, logs, or reports.

## Capability approval preview and apply

An approval is available only for an exact capability listed in the current agent
profile and only while the installation is `Ready`. First prepare a bounded JSON object
containing the exact proposed parameters. Pipe it through standard input; never place
request parameters or credentials in process arguments. Preview with the current agent
version and the exact tool, target, workspace, expiry, actor, and correlation values:

```text
<bounded-parameter-source> | agentforge policy approval preview --data-directory <absolute-path> --agent-id <guid> --agent-version <n> --request-actor <actor-id> --capability <capability-id> --risk <risk-class> --tool-id <tool-id> --tool-version <version> --tool-descriptor-hash <sha256> --target-kind FileSystemPath --target <absolute-target> --workspace <absolute-workspace> --disposition Grant --expires-at <ISO-8601-with-offset> --actor <administrator-actor> --correlation <approval-correlation> --invocation-correlation <invocation-correlation> --parameters-stdin
```

The CLI materializes the administrator credential through its OS reference for one
invocation. Output contains the request/preview hashes plus redacted parameters, target,
and workspace. It never prints the credential or unredacted credential-shaped values.
Missing/ambiguous policy, stale agent versions, unconfigured capabilities, wrong
credentials, and expirations beyond the configured lifetime fail closed.

After reviewing the exact preview, repeat every input and apply its returned hash with a
new installation-scoped idempotency key:

```text
<same-bounded-parameter-source> | agentforge policy approval apply <same-exact-options> --preview-hash <returned-sha256> --idempotency-key <unique-key>
```

An exact authenticated retry returns the original record; reusing the key with changed
input returns exit code 3. Store neither parameter sources nor preview output in shared
logs when they contain local path or recipient metadata. Tool ID, version, and descriptor
hash must be supplied together. A `Deny` disposition creates the same exact, expiring
durable evidence. Approval creation itself never executes a tool. The internal invocation
boundary sources risk and tool identity from the exact catalog descriptor, re-evaluates
policy, atomically consumes a matching grant, commits authorization audit, and then calls
the requested sandbox without weakening its controls.

## Restricted sandbox diagnostics

Inspect the controls the current host can actually enforce:

```text
agentforge sandbox capabilities
```

The command is read-only and calls the loopback host. `RestrictedHost` currently means
direct fully qualified executable paths, argument arrays, a cleared/allowlisted
environment, non-link working-directory containment, bounded combined output, a wall-
clock timeout, and process-tree termination. Windows additionally reports Job Object
kill-on-close. Treat every absent feature as unavailable: this adapter does not provide
filesystem, network, credential, privilege, CPU, memory, or process-count isolation.

Requests needing `Container`, denied/loopback-only network, filesystem isolation, or
resource isolation return `UnsupportedCapability`; operators must not weaken the request
to make it run. There is intentionally no generic execution CLI/API. If a process starts
outside the authoritative catalog/policy/approval/audit service, stop the host and treat
that as a security defect.

The authoritative catalog and policy-bound invocation service currently have no operator
or model invocation surface. Catalog
admission records a fully qualified process path but deliberately does not check that the
file exists and never runs version/help. Search results are summaries; an exact description
requires both normalized tool ID and exact SemVer version. Do not infer that inventory,
catalog membership, a descriptor hash, or an approval row makes a tool callable. Any
process start attributed solely to catalog discovery is a security defect and the host
should be stopped for evidence preservation.

If durable diagnostics show a tool invocation in `Authorized` after an interruption, do
not rerun it or reset its approval. Treat completion as uncertain, preserve the database,
audit chain, descriptor hash, and process diagnostics, and require a new reviewed request
with new approval and idempotency only after ruling out surviving effects. Terminal retry
returns metadata and hashes only; raw stdout/stderr is intentionally unavailable on replay.

Availability probes are a separate admitted descriptor operation, not an inventory flag.
Operators must require capability `tool:availability.probe`, risk `Inventory`, no target or
parameters, denied network, empty environment, a container sandbox reporting network
isolation, and bounded fixed `--version`/help-style arguments. The probe still requires an
exact descriptor-hash-bound approval and invocation idempotency key. Its immediate result
may contain one redacted printable line; replay returns availability status without text.
There is currently no CLI/API probe command and the production catalog is empty. Do not
compose a live probe until the container/namespace capabilities are verified on that host.

## Passive environment inventory

Capture Windows/Linux, distribution, WSL/isolation, filesystem, privilege, manager,
accelerator, and PATH executable metadata with:

```text
agentforge environment inspect --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id>
```

This command is valid before setup so diagnostics can describe an uninitialized
host. It writes a redacted content-addressed profile and one correlated audit event.
Executable details are omitted from stdout unless `--include-executables true` is
explicitly supplied. The capture reads bounded filesystem, proc/sysfs, runtime, and
Windows registry/token metadata only. It does not run candidates, query versions,
load plugins, open network connections, or grant invocation authority.

Inventory bounds are configured under `AgentForge:EnvironmentInventory` with
`MaximumPathDirectories`, `MaximumFilesPerDirectory`, and `MaximumExecutables`.
Truncation is explicit. An oversized redaction/artifact payload fails typed instead
of persisting unreviewed evidence. Treat stored paths as local operational metadata;
do not publish the artifact without a separate export authorization and review.

Inspect a configured installation without exposing credentials:

```text
agentforge doctor --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id>
```

The command exits 0 when every required check passes and 2 when diagnosis succeeds
but one or more checks fail. Create a redacted report and rollback profile using the
exact version returned by doctor:

```text
agentforge setup export --data-directory <absolute-path> --expected-version <n> --actor <actor-id> --correlation <correlation-id>
```

The output identifies content-addressed artifacts; it does not print the JSON or any
credential. Before configuration maintenance, enter recovery with an explicit reason:

```text
agentforge setup recovery enter --data-directory <absolute-path> --expected-version <n> --reason <text> --actor <actor-id> --correlation <correlation-id>
agentforge setup recovery resume --data-directory <absolute-path> --expected-version <n+1> --actor <actor-id> --correlation <correlation-id>
```

Entry automatically captures another pre-recovery rollback profile. Resume returns
to `Configuring`; inspect the exact installation and entity versions before editing.
Provider edit preview/apply uses the same profile fields and correlation ID in both
commands:

```text
agentforge setup provider edit preview --data-directory <absolute-path> --provider-id <guid> --expected-installation-version <n> --expected-provider-version <n> --name <name> --type <type> --endpoint <uri> --model <model> --actor <actor-id> --correlation <correlation-id>
agentforge setup provider edit apply <same-options> --preview-hash <returned-sha256>
```

Agent edits use all normal `setup agent` policy options plus the edit binding:

```text
agentforge setup agent edit preview <agent-options> --agent-id <guid> --expected-installation-version <n> --expected-agent-version <n>
agentforge setup agent edit apply <same-options> --preview-hash <returned-sha256>
```

Preview performs no write. Apply re-evaluates the complete candidate and requires the
same actor, correlation, target, versions, and normalized parameters. Each successful
apply increments both the entity version and global installation version, so refresh
before another edit. Then run `setup complete` to revalidate and return to `Ready`
using the existing administrator identity. Do not modify SQLite directly.

To restore a rollback snapshot, keep the installation in `Configuring` and use the
snapshot ID returned by `setup export` or recovery entry:

```text
agentforge setup restore preview --data-directory <absolute-path> --snapshot-id <guid> --expected-version <n> --actor <actor-id> --correlation <correlation-id>
agentforge setup restore apply <same-options> --preview-hash <returned-sha256>
```

Use exactly the same correlation and inputs for apply. Restore verifies the artifact
hash and audit provenance, requires the same provider/agent topology, materializes
every referenced provider secret, and re-evaluates capabilities and policy. It cannot
add or remove identities. After apply, run doctor and `setup complete`; never edit
the artifact, snapshot metadata, secret references, or SQLite manually.

## Model runtime diagnostics

The host registers an empty exact-profile model-provider catalog and exposes no CLI/API
model invocation. The `deterministic` runtime adapter exists for automated fixtures and
in-process composition only. Its scripts are trusted operator/test inputs, not raw provider
responses; never copy an external error body or credential into a script.

Every request must retain its exact model, correlation, typed messages, tool schemas,
artifact attachment hashes, and token/tool/event/time limits. Duplicate-key JSON,
filesystem-shaped attachment names, expired or opposed capability evidence, unlisted tool
calls, and unsupported media fail typed. A started event's input hash should change when
any attachment reference or other normalized input changes. Cancellation should terminate
enumeration, and an error stream must not contain a later completion event.

The credential-free OpenAI-compatible live adapter has passed deterministic translation and
the operator-authorized `qwen3.6` LAN gate. It requires HTTPS by default; plaintext HTTP is
an explicit per-composition opt-in. It rejects endpoint credentials/query/fragment,
redirects, caller-owned HTTP clients/headers, cookies/proxies, and media capability claims.
Every external call now requires the registered `agentforge-context-redaction-v1` preparation
policy; do not substitute a pass-through preparer. A live diagnostic must use a
fixed non-secret prompt, exact endpoint/model, bounded tokens/events/time, and
`DisableThinking`; record only typed results and hashes, never raw model context or remote
error bodies.

Hosted compatible construction exists for deterministic security verification but remains
unregistered. It requires an exact persisted HTTPS profile and store/reference; it accepts
no raw credential or header dictionary. Credential materialization and bearer-header lifetime
are internal to one send. Do not call this factory from the host/CLI until routing re-reads the
current profile and enforces locality/policy/destination controls, audit, budgets, and durable
snapshots.

The internal model router is also registered, but its production catalog is empty. It expects
a trusted current agent model policy and immutable descriptors with current policy-approved
routing evidence. It filters exact model, attempt exclusions, required media/structured
capabilities, locality, approval, context/output bounds, and tool support. A local-only request
must never select a cloud descriptor; do not work around a typed routing failure by removing
an attachment, changing the requested model, or enabling fallback. Selection hashes are
diagnostic evidence, not authorization tokens.

Do not populate the production provider catalog or add a CLI/API model call yet. The internal
execution service re-reads durable authority and bounded health, reserves shared budget, commits a
start lease plus redacted audit, and resolves the exact selected profile. Provider health is now a
scoped durable source, initially empty. Production composition still requires hosted destination/
DNS controls and bounded retry/failover. On a later retry, pass only stable profile IDs that failed
this attempt as exclusions and preserve the original request and policy.

The internal route planner now performs the read-only portion of that sequence. It prepares
context, reads serializable installation/agent/provider authority, requires exact versions and
agent budgets, filters against current bounded health, and repeats both authority and health
reads before returning a plan. Plans expire in at most five seconds. Missing health is not
healthy; do not fabricate `Healthy`, extend evidence beyond 15 minutes, clear attempt history,
or reuse an expired plan to force a route.

Route plans contain hashes and versions but are not bearer capabilities or invocation receipts.
There is intentionally no CLI/API command to create or consume one. Admission may consume only a
current exact plan; execution then independently re-plans and compares it to the reservation
before resolving a catalog adapter. Leave both production catalogs empty. A later retry may add
only the exact profile that produced a typed retryable failure, up to eight unique attempts,
while retaining the original prepared request and expected policy versions.

Admission now accepts a bounded maximum attempt count, but that number cannot enable fallback. It is
hashed into idempotency and multiplied against per-attempt token/tool/time reservations; the total
must fit current agent limits. On retry, preserve every prior attempt row and let the internal service
append only the exact failed profile to the exclusion list. Never edit attempted IDs, reset a failed
attempt to planned, change locality, or reuse an earlier profile. A policy-denied or unavailable
fallback leaves the durable failed attempt as the final result.

Inspect retry evidence through the run repository: the aggregate returns the latest attempt and the
ordered history returns all attempts. The run usage/cost/event/wall fields are cumulative; each
attempt field and ledger reconciliation is per-attempt. A mismatch is corruption, not a reason to
recalculate or delete state.

Model run admission now persists a `Reserved` run and `Planned` first attempt together with one
redacted audit event. It is still an internal service with no CLI/API route. Exact retries must
reuse the installation-scoped idempotency key and all original authority, request, actor, and
correlation inputs; a changed request must use a new key. Never repair a conflicting retry by
editing hashes, deleting the existing row, extending a plan lifetime, or reconstructing context
inside SQLite.

A `Reserved` run proves only durable admission. Do not manually mark it `Running`, resolve its
profile, materialize its secret, or call a provider; only the internal execution service may
atomically acquire its random hash-only lease and shared reservation before enumeration. A
terminal run retains usage and stream hashes but not response content, and an exact replay must
not call the adapter again.

If a process exits after `model.run-started` without terminal audit, preserve the database, WAL,
audit chain, lease timestamps, run/attempt versions, and ledger. Do not delete the run, reset it to
`Reserved`, clear the lease, decrement the ledger, or reuse its idempotency key. The internal
recovery service may act only at/after exact expiry with exact versions; it records retryable failure,
releases the reservation, writes temporary provider-health evidence, and audits atomically. There
is no background scanner or operator command yet. Caller cancellation during a live in-process
attempt should produce durable `Canceled` evidence and release the ledger before propagating.

An embedding worker may heartbeat only with the exact raw token it received in memory at start.
Heartbeats must move forward, cannot pass or extend expiry, and do not change attempt version. Never
store the raw token in worker state, logs, audit, exports, or an operator script.

Run the explicit live integration gate by setting process-scoped variables, then remove
them after the test process exits:

```text
AGENTFORGE_LIVE_OPENAI_COMPATIBLE_ENDPOINT=<exact-chat-completions-uri>
AGENTFORGE_LIVE_OPENAI_COMPATIBLE_MODEL=<exact-model-name>
dotnet test tests/AgentForge.IntegrationTests --filter FullyQualifiedName~OpenAiCompatibleLiveIntegrationTests
```

The test is skipped when either variable is absent. These variables are endpoint/model
metadata only; this credential-free gate accepts no API key or authorization header.

Before public runtime enablement, add automatic expired-lease and task scanning plus a governed step
executor that binds model/tool artifacts to loop evidence. Provider DNS/IP policy is enforced on
each new production socket connection and must not be replaced with a default HTTP handler.
Setup profile acceptance alone does not compose an adapter.

For a provider destination denial, inspect only the configured endpoint, declared data-location
class, and bounded policy result. Do not log resolved address lists in ordinary audit, change Cloud
to PrivateNetwork to make a request pass, enable an ambient proxy, or retry through a default
handler. Correct DNS/profile policy, issue fresh health evidence, and retry through normal routing.

Provider type is operational identity: use `openai`, `deepseek`, `vllm`, or `openai-compatible`
exactly. Do not relabel a failing profile to bypass endpoint rules. A newly configured profile has
configured-unprobed text/streaming evidence only; run a bounded capability gate before approving
tools or media. CLI credential input must remain redirected stdin or hidden prompt input, never an
argument, environment variable, report, or log.

The internal typed loop writes one immutable snapshot and audit event per accepted phase. A worker
restart must call the same request with the same loop ID, installation/agent version, budget,
initial-state hash, actor, correlation, and idempotency key. It resumes from the latest durable
phase; any mismatch is a concurrency conflict. A terminal replay is read-only.

If a worker exits between snapshots, preserve the database and retry the exact request. Never edit
phase, turn, counters, evidence hashes, sequence, or prior/current hashes; never delete a snapshot
to repeat an action. `NoProgress`, `BudgetExceeded`, `Failed`, and `Canceled` are terminal evidence,
not operator invitations to rewrite state. A new authorized objective requires a new loop ID and
idempotency key.

## SQLite migration and cold backup

The host applies checked-in forward migrations before it starts listening. Before a
manual cold backup, stop AgentForge and confirm no host process is using the data
directory. Copy `agentforge.db` and the content-addressed `artifacts` directory as one
backup set; preserve their relative layout and record SHA-256 hashes. Restore into a
new data directory, start in setup/recovery mode, and verify installation state and
the complete audit chain before permitting normal mode. Never replace a live WAL
database by copying only its main file.

Migration 0001 creates a new store and has no data-preserving down migration. On a
failed first install, retain diagnostics and restore the pre-migration directory. Do
not delete or overwrite a populated database to simulate rollback.

Migration 0003 creates agent identities and foreign-key binds their installation and
primary provider. Before upgrading, stop AgentForge and back up the complete SQLite,
artifact, and secret-reference set. Its generated down migration drops identities;
restore the pre-upgrade backup instead of applying down to operator state.

Migration 0004 creates the single local-administrator row. Its down migration drops
authentication state and is destructive; restore the full pre-0004 backup instead.

Migration 0005 creates setup-profile snapshot metadata and foreign-key binds each row
to its installation and content-addressed artifact. Its down migration discards
recovery evidence; restore the full pre-0005 SQLite/artifact/OS-reference backup set
instead of applying it to operator state.

Migration 0006 creates exact capability approval/denial evidence. Its down migration
drops security decisions and is destructive; restore the full pre-0006 SQLite, artifact,
and OS-reference backup instead of applying down to operator state.

Migration 0007 adds the exact descriptor hash to capability approvals. Existing tool
approvals retain a null hash and intentionally cannot authorize a descriptor-bound request.
Do not backfill or guess hashes; issue a new preview and approval for the exact catalog
descriptor. Its down migration removes this binding and must not be used to regain authority.

Migration 0008 creates durable tool invocation/idempotency records and optional approval
provenance. Its down migration destroys execution evidence. Stop the host and restore the
complete pre-0008 backup rather than applying down to operator state.

Migration 0009 creates durable model-run and first-attempt reservation evidence. Its down
migration destroys idempotency, route provenance, budget reservations, and terminal history.
Stop the host and restore the complete pre-0009 backup rather than applying down to operator
state. Never delete a reserved run to make an uncertain retry appear new.

Migration 0010 adds model start leases, attempted-profile history, stream evidence, event
reservations, and the shared agent budget ledger. Its down migration destroys execution and
accounting evidence. Restore the full pre-0010 backup rather than applying down. Never clear a
running lease or ledger reservation manually; preserve it for typed expired-lease recovery.

Migration 0011 creates the versioned provider-health table with exact installation/profile/run/
attempt provenance and bounded circuit-breaker timestamps. It fabricates no evidence for existing
runs. Its down migration destroys health provenance. Restore the full pre-0011 backup rather than
applying down; never edit health rows to force routing.

Migration 0012 adds total attempt limits/cumulative wall evidence and per-attempt reservations. It
backfills existing single attempts from their parent run and assigns maximum one. Its down migration
destroys retry accounting. Restore the full pre-0012 backup rather than applying down; never delete
attempt history to force failover.

Migration 0013 creates append-only agent-loop snapshots with exact authority, budgets, phase,
progress, and hash-chain evidence. It fabricates no loops for existing state. Its down migration
deletes recovery and completion evidence. Restore the full pre-0013 backup rather than applying
down; never edit or resequence snapshots to force resume.

Milestone 1 cold-restore evidence copies the stopped database (including any WAL/SHM
members), artifacts, and OS-protected secret files as one directory tree, records a
SHA-256 for every file, compares the restored set, initializes migrations, verifies
the audit chain through doctor, and materializes the restored provider/admin
references. Use the same OS user; DPAPI data is intentionally not portable to a
different Windows identity.

## Gate and recovery rules

- Record every command and result in `artifacts/gates/<gate-id>.md`.
- A failed deterministic test makes the gate `Revise` or `Block`.
- Do not delete state during recovery. Back up the database and artifact metadata,
  validate hashes, then use a documented repair transition.
- Never place secrets in command lines, configuration, gate reports, or trajectories.
