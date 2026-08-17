# AgentForge Threat Model

## Security objective

Model output and external content may propose actions but cannot authorize them.
Deterministic policy, typed contracts, approval binding, containment, and verification
must mediate every significant effect.

## Assets

- Operator identity, credentials, provider secrets, and approval decisions.
- Workspaces, source repositories, device access, messages, schedules, and network reachability.
- Task state, audit trails, trajectories, skill/plugin packages, evaluators, and active-version pointers.
- Budgets, model routing, policies, configuration, and release artifacts.

## Trust boundaries

1. Operator/CLI/browser to the local control plane.
2. Control plane to application/domain services.
3. Runtime to models, tools, plugins, MCP servers, search, and channels.
4. Safe host to sandboxed processes and coding worktrees.
5. Device discovery to serial open/read/write operations.
6. Relational state to content-addressed artifacts, backups, and exports.
7. Active immutable versions to proposal/canary workspaces.

## Threats and required controls

| Threat | Required controls | First verification |
|---|---|---|
| Direct/indirect prompt injection | Content boundaries, no policy from content, capability checks outside model, output schemas | M2 security fixtures |
| Tool-description poisoning | Signed/provenance descriptors, description treated as data, progressive disclosure | M2 |
| Shell/argument injection | Direct executable plus argument list, no interpolated shell, allowlisted environment | M2 |
| Path traversal and symlink escape | Canonical containment, no untrusted symlink following, hash-bound paths | M2/M5 |
| Secret exfiltration | References, invocation scope, egress policy, redaction before every sink | M1/M2 |
| SSRF/network pivot | Destination policy, DNS/IP recheck, local/metadata address denial, proxy controls | M2/M7 |
| Malicious skills/plugins | Immutable packages, static scan, permission diff, sandbox, signatures, canary/rollback | M5/M10 |
| Supply-chain compromise | Central locks, signatures/hashes, vulnerability and secret scans, SBOM | M0/M10 |
| Privilege escalation | Least-privilege host, explicit privileged capability, no self-grant | M2 |
| Cross-agent/workspace leakage | Scope IDs on records, context minimization, repository filters, authorization tests | M1-M4 |
| Audit tampering | Append-only sequence, chained hashes, external export verification | M1 |
| Provider compromise/outage | Schema validation, bounded retries, health evidence, locality-aware fallback | M3 |
| Runaway loop/cost exhaustion | Budgets, cancellation, repeated-call and no-progress detection | M3/M4 |
| Unsafe self-modification/evaluator capture | Isolated proposals, role separation, deterministic veto, operator approval for core | M5/M9 |
| Webhook replay/identity spoofing | Signature verification, inbox idempotency, stable identity mapping | M7 |
| Hostile media/attachments | Content-type verification, size/decompression bounds, isolated parsing | M3/M7 |
| Hostile device bytes/physical effects | Passive discovery, separate read/write grants, bounded capture, no device text as instructions | M8 |

## Read-only web-preview update

The loopback root is a diagnostic display, not a setup authority. It can read only the
already-public installation status, liveness/readiness, and sandbox-capability endpoints.
It contains no form, password field, cookie, session, credential material, mutation route,
or model-controlled content. Static JavaScript assigns API values through `textContent`,
all assets are same-origin, and responses deny framing, MIME sniffing, external scripts,
forms, ambient device APIs, caching, and referrers through explicit headers.

This does not close the web setup trust boundary. Remote binding remains opt-in and unsafe
without the later TLS/authentication/network-policy gate. The Milestone 7 wizard must add a
one-time setup nonce, authenticated session, CSRF and origin controls, rate limits, exact
idempotency, audit, and shared-service profile equivalence before any browser mutation or
credential entry is enabled.

## M0 controls and limitations

Implemented now: loopback default, constrained correlation IDs, fail-closed startup,
unreadable-state recovery, immutable domain transitions, and dependency enforcement.
No tool execution, model calls, secrets, plugins, channels, schedules, or devices are
enabled, so their attack surfaces remain closed.

Open risks are tracked in `PROJECT_STATE.md`; unrestricted execution cannot be enabled
until M2 controls and security tests pass.

## M1 durable-foundation update

SQLite is initialized through checked-in migrations in WAL mode. Installation writes
use numeric optimistic concurrency, artifacts use canonical SHA-256 paths, and audit
events carry a global sequence plus previous/current hash. File and directory names
are configuration-validated, test databases disable connection pooling for reliable
cold backup and cleanup, and the host migrates before accepting requests.

The Linux gate detected the high-severity SQLitePCLRaw 2.1.11 advisory. AgentForge
now directly pins stable bundle 2.1.12 and fails restore on vulnerability warnings.
At the M1-01 gate, the structured redactor was not yet connected, so only the
explicit `RedactedData` type could cross the audit-journal boundary.

## M1 audit/redaction update

The Audit application service is now the raw-payload entry point. It uses the
Security module to recursively redact sensitive property names and common credential
shapes, sorts JSON object properties for deterministic evidence, rejects oversized or
over-deep payloads, and passes only `RedactedData` to persistence. Hash inputs use
length prefixes, preventing field-boundary ambiguity, and verification reports the
first non-contiguous, relinked, or content-tampered event.

This control currently covers the audit sink. Provider secrets remain disabled until
secret-reference storage and OS-backed invocation materialization pass their gate;
model-context and export redaction must reuse this boundary before those sinks open.

## M1 setup-service update

Setup mutations now pass through one application service. It rejects empty IDs,
oversized identifiers, control characters, mismatched durable installation IDs, and
invalid state transitions before committing. Installation state and its redacted
audit event share one EF unit of work; stale commits return a typed retryable conflict.
CLI options use exact names, reject duplicates and unknown arguments, use direct .NET
APIs rather than a shell, and report storage failures without echoing exception data.

This is not authorization: local administrator credentials and authenticated control
plane mutations are still absent, and the host cannot enter normal mode.

## M1 secrets/provider update

Durable provider rows contain secret store/key references only. Windows credential
bytes are UTF-8 encoded into a temporary buffer, protected with current-user DPAPI,
written atomically, and cleared; materialization decrypts into a disposable character
lease and clears intermediate bytes. Linux Secret Service uses an absolute known
executable, argument arrays, allowlisted DBus environment, redirected stdin, bounded
output, a ten-second timeout, and process-tree termination. Missing tooling returns
`UnsupportedCapability` without degradation.

Provider endpoints reject user info, query strings, and fragments so credentials
cannot be smuggled into configuration. Profiles are installation-scoped, uniquely
named, versioned, and foreign-key bound. Tests scan the database, audit payloads, and
the protected file for the exact plaintext fixture.

## M1 agent-policy update

Agent candidates are normalized and structurally validated before durable state is
read. A selected provider must belong to the same installation and carry observed
text capability. `LocalOnly` permits only in-process, loopback, or explicitly private-network
provider transport and forbids cloud fallback.
Child depth, count, concurrency, and token allocation are jointly bounded and cannot
exceed the parent bootstrap token budget. Learning mode and mutable-skill scope must
match; bootstrap never grants direct credential access, external messaging, device
write, privileged execution, external network, or autonomous promotion.

Effective previews enumerate explicit `Allow`, `Deny`, and `RequireApproval`
decisions outside model control. Exact tool/skill identifiers remain approval-gated
because their catalogs are not available yet. Preview is read-only, while create
re-evaluates and atomically appends a redacted audit event. M2 must still implement
request-bound authorization, inheritance/intersection, approval expiration, and the
global missing-policy-denies evaluator used at invocation time.

## M1 administrator/completion update

AgentForge generates administrator credentials from 256 random bits and encodes them
without creating a managed plaintext string. The client credential is stored through
the OS adapter; SQLite stores only store/key reference, random salt, work factor, and
PBKDF2-SHA256 verifier. Verification uses fixed-time comparison. Tests materialize
the client reference for one disposable lease and prove exact credential bytes are
absent from SQLite.

Completion fails closed unless storage migrations have initialized, the global audit
chain verifies, a text provider's secret can materialize, and a named agent exists.
External credential creation precedes the relational commit, so any unsuccessful
commit triggers exact-reference deletion. A Ready runtime endpoint returns 401
without a valid bounded bearer credential. Rate limits, lockout/session policy,
request idempotency, CSRF/browser authentication, and remote TLS remain open and must
pass before exposing mutation or remote surfaces.

## M1 maintenance/export update

Doctor exposes bounded status summaries only and never materialized values. Setup
export and recovery mutations require an exact installation version plus the local
administrator credential; the CLI materializes that credential from its OS reference
for one disposable invocation instead of accepting it as an argument. Report and
profile payloads cross the structured redaction boundary before content-addressed
storage. Tests prove that administrator verifiers and materialized credentials are
absent while reference store/key metadata remains available for recovery.

Entering recovery atomically stores the pre-transition rollback profile, changes the
installation state, and appends its correlated audit event. Recovery is deliberately
unhealthy and cannot enter normal runtime. Actor/correlation IDs and operator-supplied
provider/agent metadata are rejected when they resemble credentials, closing sinks
that sit outside audit payload redaction.

Provider onboarding never accepts credential material as a process argument. The CLI
reads a bounded character buffer from redirected stdin or a hidden console prompt,
passes it to the shared setup service, then clears it. Any failed relational profile
commit deletes the exact newly created OS secret reference.

Provider and agent changes are available only in `Configuring` after administrator
authentication. Preview is read-only; apply repeats normalization and capability
validation, requires exact installation/entity versions, and compares a lowercase
SHA-256 request hash bound to installation, actor, correlation, target, versions,
effective parameters, and provider evidence. The profile update, global installation
version increment, and redacted audit event share one transaction. Snapshot restore
is a separate authenticated preview/apply path. It reads at most 2 MiB, verifies
recorded length, media type, SHA-256 content, schema, installation/administrator
binding, and an intact audit event carrying the rollback hash. It rejects entity
topology changes, re-materializes every provider reference, re-probes capabilities,
and re-evaluates every agent policy before calculating the restore hash. Apply repeats
all checks and commits changed entities, one installation version, and one redacted
audit event atomically. Restore cannot create authority or start runtime work.

## M2 passive-environment update

Environment discovery is an information boundary, not tool authorization. A hostile
PATH directory may contain crafted names, links, huge directories, raced entries, or
network locations. Capture therefore bounds directories, entries, executable count,
strings, and artifact size; skips Windows UNC entries; records link/provenance/trust
metadata; catches inaccessible/raced entries; and reports truncation. Paths outside
known OS directories remain `Unknown` or user-directory trust and gain no authority.

The inventory implementation contains no process-start primitive. It reads only
runtime/native metadata, `/etc/os-release`, bounded proc/sysfs files, Windows registry
and token metadata, service marker directories, and top-level PATH entries. Kali is
recognized only from the exact normalized distribution ID, never by executing a
binary. Version/help probing and invocation remain closed until the restricted
executor, policy, approval, containment, output, timeout, cancellation, and process-
tree gates pass.

Profiles cross structured redaction before content-addressed persistence and append a
correlated hash-chain audit event in the same relational commit. CLI output withholds
executable details unless explicitly requested. Remaining risks are local path/privacy
exposure, mutable host evidence between captures, missing network/shell/package-
database detail, symlink races, and lack of invocation authorization; later M2 slices
must not reinterpret inventory presence as permission or capability proof.

## M2 capability-policy and approval update

Authorization identity is constructed outside model control from typed installation/version,
agent/version, request actor, capability/risk, tool/version, normalized JSON parameters,
target kind/value, and workspace. JSON object properties are deterministically ordered,
duplicates and excessive depth/size are rejected, paths are made fully qualified, URI
userinfo is forbidden, and only SHA-256 hashes cross the durable approval boundary.
Changing any bound field produces a different request hash.

The evaluator requires exactly one same-installation, same-agent-version policy rule.
Missing, ambiguous, cross-scope, or stale policy denies. Parent/child intersection uses
the union of rule keys and denies every omission; otherwise it selects the most
restrictive decision. An approval can affect only a `RequireApproval` rule and must be
active, unexpired, and an exact field-by-field match. Exact active denials take precedence
within the approval-gated path. Grants have a pure single-consumption transition for the
restricted executor; no invocation path is enabled in this slice.

Only the authenticated local administrator can preview or apply a decision on the
current durable agent version. Preview binds disposition, expiry, approver, correlation,
request hash, and policy fingerprint. Apply repeats validation, compares the preview in
fixed time, uses an installation-scoped unique idempotency key, atomically stores the
hash-only record plus redacted chained audit event, and returns an existing record only
for an authenticated exact retry. Conflicting reuse fails typed. Raw parameter, target,
and workspace values are neither approval columns nor audit evidence; the CLI accepts
parameter JSON only through bounded redirected stdin.

The remaining boundary is deliberate: callers currently supply risk and tool identity
to the contract, but cannot invoke anything. The restricted executor must source those
fields from an authoritative immutable tool descriptor, enforce workspace and isolation
policy, fetch the current exact decision, and atomically consume a grant before process
start. Inventory presence and an approval row alone never authorize execution.

## M2 restricted-host execution update

The restricted executor treats executable paths, arguments, environment, workspace, and
output as hostile. It refuses relative, missing, linked, reparse-point, or parent-link
executable paths and refuses working directories outside the exact existing non-link
workspace. Child launch uses `ProcessStartInfo.ArgumentList`, never a constructed shell
string or `UseShellExecute`. The child environment is cleared, then rebuilt from bounded
administrator-configured inheritance and invocation allowlists; standard input is closed.

Standard output and error share one byte budget and one ordered observer boundary. A wall-
clock timeout, caller cancellation, output overflow, or observer failure terminates the
process tree and drains boundedly. Windows attaches the child to a kill-on-close Job
Object, enumerates the native process snapshot, and adds descendants that started before
the parent attachment; later children inherit the Job. Linux uses managed descendant termination. Tests use hostile shell metacharacters,
output floods, stalled observers, path escapes, symlinks, and delayed child sentinels.

Capability flags are security claims. The restricted-host adapter does not claim or
silently emulate filesystem, network, CPU, memory, process-count, credential, privilege,
or container isolation. A request for any unavailable control returns
`UnsupportedCapability` before start. The control plane exposes only read-only capability
diagnostics; it exposes no generic invocation surface.

Residual risks remain explicit. Local filesystem entries can be replaced between path
validation and process start, Windows descendant capture is a bounded snapshot/recheck,
and managed descendant discovery has OS timing limits. Therefore restricted-host execution is not a
high-risk sandbox. Immutable authoritative descriptors, policy reconstruction, atomic
approval consumption, invocation audit, and the container/namespace adapter remain closed
gates before model-selected or high-risk tools can run.

## M2 authoritative-tool-catalog update

Catalog membership is a configuration trust decision, not inventory, availability, or
invocation proof. Only typed definitions from built-in, operator-approved, or later
signature-verified sources may be admitted. Source kind and trust level must form an
allowed pair and carry exact source version plus lowercase SHA-256 evidence. Duplicate
tool/version identities, malformed SemVer, unknown flags, understated side-effect risk,
ambiguous targets, unbound parameters, relative executable paths, unsafe environment
names, and unbounded execution settings fail admission.

Admitted records snapshot mutable input and receive a hash over their normalized full
descriptor. Progressive search exposes only identity, summary, capability, risk, target
kind, side effects, provenance classification, and descriptor hash. Executable paths,
fixed arguments, parameter bindings, and environment names remain behind exact ID/version
description. Search and admission do not touch or execute the candidate path, so a hostile
PATH entry cannot turn inventory into a probe.

Descriptions remain untrusted model context even when their catalog provenance is valid.
The invocation boundary accepts parameter values only, reconstructs all security fields
from the exact descriptor, validates and canonicalizes those values, binds the descriptor
hash into authorization, consumes approval and appends start evidence transactionally,
then uses the requested sandbox. Safe version/help probing traverses that same boundary
with a separate inventory capability and bounded output; the catalog itself never probes.

## M2 policy-bound invocation update

The invocation boundary accepts no caller-authored executable, arguments, capability,
risk, target kind, side effects, environment, timeout, output limit, network policy, or
sandbox selection. It accepts typed values and exact catalog identity, then reconstructs
all authority and process fields from the immutable descriptor. Unknown, missing, mistyped,
out-of-range, or control-bearing values fail before policy evaluation. Descriptor-owned
bindings produce an argument list, never a shell string. Credential-shaped direct values
or identifiers fail before persistence or execution; future secret-bearing tools must use
invocation-scoped secret references.

Authorization now binds the normalized descriptor hash. Legacy tool approvals without a
hash remain readable after migration but cannot match. Current Ready installation, exact
agent version, network-posture intersection, missing-policy denial, and active exact
approval are evaluated outside model control. Approval consumption, durable invocation
idempotency, and redacted authorization audit commit in one transaction before `ISandbox`
is called; policy/version is read again after commit. A failed or denied post-commit start
does not restore the consumed grant.

Terminal records contain hashes and lengths rather than raw stdout/stderr. Exact retries
return the durable record without another process start; request/correlation changes under
the same key conflict. `Authorized` after interruption is explicitly uncertain and cannot
be replayed automatically. This chooses possible one-time non-execution over duplicate
effects. Raw output exists only in the bounded immediate result and must cross the later
model-context redaction boundary before model use.

No public invocation route exists and the default catalog is empty. The restricted-host
adapter cannot enforce denied or loopback network policy, so current agent postures cannot
silently use it. Container/namespace availability remains the gate for descriptors that
require those controls. A local configuration race can still occur after the final policy
read and filesystem replacement can occur before process open; high-risk effects remain
disabled pending stronger isolation and later durable orchestration.

## M2 safe-availability-probe update

Version/help execution is not inferred from PATH inventory and cannot be smuggled into an
ordinary tool descriptor. The catalog admits a probe only under the exact inventory-only
capability with no caller parameters, target, side effects, environment, or network. A
literal argument, tight time/output limits, container sandbox, and declared network
isolation are mandatory; missing controls reject the descriptor rather than downgrade it.

The probe application service does not bypass invocation policy. It needs the current
agent grant and a single-use descriptor-hash-bound approval, commits authorization and
audit before the sandbox, and reuses durable idempotency. A probe can therefore reveal
only approved local metadata, never confer authority to another tool. Immediate output is
strict-UTF-8 and printable, fully scanned for credential material before a 512-character
first-line summary is returned. Sensitive or malformed output is suppressed, raw bytes
are not persisted, and a terminal replay returns no reconstructed summary.

The deterministic sandbox proves this boundary without claiming live isolation. A real
container/namespace adapter and equipped CI runner remain required before production probe
descriptors can be composed. Executable replacement between admission and process open is
also unresolved; immutable images or executable evidence are required for higher-risk use.

The R1 container gate closes the higher-risk portion of this finding. A digest-pinned Docker
adapter now supplies denied networking, an immutable image identity, contained workspace
mounts, non-root/read-only execution, dropped capabilities, no-new-privileges, resource/PID
bounds, and forced cleanup. High-risk requests cannot fall back to restricted host. The
same-user restricted-host replacement race remains an accepted Medium limitation only for
requests whose declared policy permits that weaker capability.

## M3 provider-neutral-contract update

Vendor SDK types, raw HTTP responses, and provider-specific finish/error records are not
trusted application state. The harness boundary accepts bounded typed requests and emits
only AgentForge event records. Requests are snapshotted before streaming, preventing a
caller from mutating messages, tools, schemas, attachment references, correlations, or
budgets after validation. Started evidence carries a SHA-256 over the normalized complete
input, including media references.

JSON is a parser-confusion boundary. Schemas, tool results, tool-call arguments, and
structured outputs have fixed character/depth bounds, must parse, and reject duplicate
property names recursively before normalization. Attachment names reject both slash forms
on every OS and never act as filesystem paths; bytes remain in the content-addressed
artifact store. Unsupported image/audio/document capability returns a typed error rather
than dropping content.

Capability declarations alone are not routing authority. Evidence records its declared,
probed, observed, overridden, or policy-approved source separately from available,
unavailable, unknown, or temporarily failing status and optional expiry. The deterministic
provider fails closed on opposed, future, or expired evidence. Later routing must still
intersect data locality, current policy, provider health, context, tools, cost, and latency.

Deterministic scripts are trusted test/configuration fixtures, not a parser for external
provider errors. They are snapshotted, bounded, capability-checked, and terminally ordered;
a retryable failure never produces false completion. No external adapter, credential
materialization, model-context redaction, public call surface, or cloud fallback is enabled
in this slice, so no prompt can yet cross an external model trust boundary.

## M3 credential-free OpenAI-compatible update

The compatible adapter crosses an external HTTP boundary only when explicitly constructed;
it is absent from production DI and has no CLI/API route. It accepts no credential reference
or raw header map. HTTPS is the default, while the operator's LAN endpoint requires a visible
plaintext-transport opt-in. URIs containing user info, query, or fragment are rejected, the
successful response must report the exact original request URI, and public construction
owns a transport with redirects, cookies, proxies, and automatic decompression disabled.
It accepts no caller-owned client or arbitrary header map; the fake-handler seam is internal
to the unit-test assembly. This is not yet a complete SSRF control: network policy, DNS/IP
revalidation, and locality routing remain mandatory before runtime composition.

Outbound JSON is written from a normalized snapshot and has a fixed byte ceiling. The
response must be `text/event-stream`; declared length, total bytes, individual lines/events,
wall time, output usage, tool calls, arguments, and emitted events are bounded. Every SSE
line is strict UTF-8. Data JSON is depth-limited, recursively duplicate-key checked, and
translated to typed records. Unknown tools, unstable IDs/names, malformed arguments,
unsupported reasoning channels, inconsistent finish reasons, invalid structured output,
and truncation terminate with an error and no false completion. Remote error bodies are
discarded; only bounded status/retry metadata and fixed AgentForge messages cross inward.

The live gate sent one fixed non-secret prompt to the operator-authorized `qwen3.6` LAN
endpoint and verified text, usage, and typed stop completion through the actual adapter.
The live probe remains credential-free and unregistered. Media evidence is rejected at
adapter creation so artifact-backed content cannot be silently omitted.

## M3 provider-egress security update

External model requests cross a versioned preparation boundary before provider validation or
serialization. The preparer creates new read-only message/tool collections and never mutates
the caller's request. It redacts secret-shaped text, attachment names, tool arguments/results,
and descriptions with the shared bounded canonical redactor. Identity and correlation fields
or JSON schemas containing sensitive material fail closed because replacement could change
tool/routing authority or invalidate a contract. Failures use fixed messages and never echo
the rejected input. Started evidence records policy plus redaction count, and the input hash
covers only the prepared request.

Hosted bearer authorization accepts no caller header map. Construction requires an exact
profile/descriptor ID, provider type, model, HTTPS endpoint, capabilities, version, configured
store, and secret reference. Materialization occurs only after the consumer advances beyond
the started event and lasts through the bounded HTTP send. Control, whitespace, non-ASCII,
oversized, missing, or injected header values fail before transport. `finally` removes the
header and clears the character lease before any response event; request JSON, events, errors,
logs, and state never receive credential material. The BCL bearer parameter is transient
managed text and cannot be zeroed directly, so its header reference is dropped immediately
after `SendAsync` rather than retained through response streaming.

This is not runtime authorization or routing. Production still has no provider catalog entry,
public invocation route, hosted setup validation, destination/DNS enforcement, current-profile
re-read, audit, cumulative budget, or run snapshot. Those gates must pass before a stored
profile can cause model egress.

## M3 model-routing update

Routing identity and policy are constructed outside model content. The router only considers
exact-model catalog snapshots, rejects duplicate or excessive attempt exclusions, and requires
current capability evidence plus separate current policy-approved routing evidence. Missing or
malformed routing evidence denies the candidate. `LocalOnly` excludes every cloud descriptor
before fallback, so an outage cannot weaken locality. Context/output capacity is checked before
tool eligibility, and media creates a mandatory image/audio/document capability rather than a
reason to omit the attachment.

Fallback cannot silently choose an arbitrary provider. A viable exact primary wins; otherwise
fallback must be enabled and is ordered by bounded reliability, known cost, latency, then stable
profile ID. The selection hash covers policy, context requirements, attempt exclusions, required
capabilities, and all selected provider evidence, allowing a later audit to detect changed
selection inputs.

The pure router still trusts its caller to supply the current durable `AgentModelPolicy`, and
descriptor-level policy evidence is not yet bound to an agent/profile version. Provider health,
DNS/IP destination revalidation, audit, cumulative reservation, and durable run snapshots are
not implemented here. Therefore the catalog remains empty and there is no public model route;
the later invocation service must close those races before egress.

## M3 health-aware route-planning update

The scoped planner no longer accepts policy, model identity, or budget from model content as
authority. Context is prepared first; then a serializable persistence reader returns the exact
installation, agent, and provider-profile set. Exact expected versions, `Ready`, the primary
profile's durable model, provider catalog/profile identity, persisted capability flags, and
agent request budgets must all agree. A changed request model is denied before health or route
selection.

Health is a bounded typed trust input. Records cannot contain remote error text, duplicate
profiles, unbounded lifetimes, or open-ended retry state. Missing and non-healthy evidence both
exclude a profile, preventing deletion from turning failure into health. Attempt history is
bounded, unique, and exact-catalog-only. Authority and relevant health are read twice; changed
authority conflicts, while changed health or eligibility asks the caller to retry. Plans expire
within five seconds and bind prepared-input, policy/version, route, and health hashes without
returning endpoints, references, request content, or credentials.

Serializable reads and double checking do not make a plan authorization. A profile or network
destination can still change after planning; health observations are deterministic/configured
fixtures rather than a durable circuit breaker. The production catalogs remain empty and no
provider is resolved. Durable run/attempt idempotency, cumulative budget reservation, audit,
destination DNS/IP revalidation, health recording, and exact adapter resolution remain required
before egress.

## M3 durable model-run admission update

Admission treats retry identity and durable evidence as a security boundary. Actor,
idempotency, correlation, and causation values are bounded and rejected if credential-shaped;
model correlation must exactly match the admission envelope. Context is redacted before its
effective hash participates in the admission hash. Exact retries compare hashes in fixed time,
while changed content, authority, history, actor, or correlation under the same installation key
conflicts. A database uniqueness race cannot create two runs or two audit events.

The short-lived plan must exactly match requested installation/agent/request versions and all
four reservation dimensions. Run, first attempt, and redacted audit evidence commit atomically.
The schema cannot store model messages, attachment bytes, endpoints, secret references,
credentials, raw responses, or remote error bodies; byte-scan fixtures verify representative
prompt and credential absence. Failed planning, invalid metadata, and pre-cancellation write
nothing.

Admission alone is not execution authority. The internal execution boundary described below is
the only consumer permitted to resolve a catalog profile from a reserved run; no public mutation
can reach it. Retry/attempt expansion and exact expired recovery are implemented by the later
boundaries below; automatic recovery scanning and the typed loop remain separate gates.

## M3 durable model-attempt execution update

A start race is constrained by three exact optimistic-concurrency resources: the reserved
run/attempt versions, the current Ready installation/agent authority, and the shared agent ledger.
The execution service repeats context preparation and routing with persisted attempt exclusions,
then compares route, input, health, versions, and all reservation dimensions before exact catalog
resolution. The run, ledger, and redacted start audit share one commit before egress, so a losing
worker cannot partly reserve or call the adapter. Hosted destination/DNS binding is not yet
composed in production and remains a required gate.

Lease possession is not inferred from worker identity. A 256-bit base64url token exists only in
the invocation; SQLite and audit receive its SHA-256. Fixed-time comparison of the exact token is
required for success, failure, cancellation, budget exhaustion, or heartbeat. Heartbeats must
advance monotonically and cannot extend expiry. At or after persisted expiry, exact-version
recovery deterministically records retryable failure at the expiry boundary and releases the
ledger in the same transaction. There is no background scanner or operator command yet; deleting
or zeroing evidence would still create duplicate-call risk.

Provider streams are untrusted. Exact request/profile/type/model/prepared-input evidence must
start the stream, sequences must be contiguous, timestamps monotonic and within the lease, usage
may appear once, and one terminal event ends the stream. Text, JSON, errors, counts, and time are
bounded; tool calls are rejected at this gate. Truncation, reordering, substitution, late events,
or event overflow becomes typed failure/budget exhaustion. The accumulator hashes canonical
events in memory and stores only the digest/count/last sequence plus normalized usage and safe
classification. Raw prompt/output/error text has no database or audit field.

The terminal transition and exact ledger release share one transaction. Successful or retryable
provider outcomes also update one versioned provider-health row; cancellation, policy, unsupported,
and budget outcomes do not. Health lifetime, failure count, retry window, evidence codes, run/
attempt provenance, and timestamps are bounded. Concurrency failure rolls back run, ledger, health,
and audit together. Duplicate replay sees a non-reserved state and cannot invoke the provider again.
Caller cancellation is persisted before the cancellation exception propagates. Remaining threats
are health-source poisoning outside the observed adapter boundary, automatic expired-lease scanning,
typed loop resumption, hosted endpoint DNS/IP rebinding, and any future public authentication/rate-
limit surface.

## M3 bounded retry/failover update

Retry count is caller-visible but not caller-authorizing. Admission hashes it, caps it at eight,
re-reads current agent authority, and rejects multiplied token/tool/wall totals above policy. A
changed attempt count conflicts with the existing idempotency key. Each attempt has its own durable
reservation and version; prior terminal attempt rows are never overwritten when a new one is added.

The failed profile ID is appended by trusted application code, not accepted as model output. Route
planning verifies all IDs belong to the exact-model catalog, excludes every prior profile, and
reapplies current locality, fallback, capability, and health evidence. The retry transition verifies
contiguous unique history and a distinct selected profile. Thus a retry budget cannot turn on cloud
fallback, repeat a provider, substitute history, or exceed remaining total run budget.

Ledger accounting uses current-attempt usage while run accounting adds every attempt's usage, cost,
event evidence, and wall time. Currency mismatch and arithmetic or evidence overflow fail the
terminal transaction. Remaining threats are hosted endpoint DNS/IP rebinding, automatic recovery
scanning, and any future public invocation surface.

## M3 durable typed-loop update

Loop state is structured domain data, never provider prose. Every phase transition binds the prior
snapshot hash and recalculates a canonical hash over authority, budgets, counters, correlations, and
normalized evidence. Tampering, resequencing, authority substitution, or changed idempotency input
therefore fails validation or the database key. Snapshot and redacted audit append atomically.

Only Persist accepts progress evidence or advances a turn. A completion signal cannot bypass
Reflect/Persist. Rejected structured output repeats only its current phase and consumes a bounded
repair allowance; repeated normalized progress, token/tool/turn/wall exhaustion, executor failure,
and cancellation all produce explicit terminal snapshots. Checked totals and saturating elapsed
time prevent overflow from restoring authority.

The default executor is deliberately unavailable and no API/CLI mutation exists. Remaining threats
are a later governed step executor binding raw model/tool artifacts to their evidence hashes,
lease-based task ownership and automatic recovery, and public runtime authentication/rate limiting.

## M3 provider-destination update

The production HTTP handler owns DNS resolution and socket creation. It verifies exact host/port,
rejects the complete DNS answer when any normalized address violates the route's Loopback,
PrivateNetwork, or Cloud class, then connects directly to an approved IP. TLS continues to validate
the original host. This prevents hostname re-resolution between a policy check and connection and
blocks mixed-answer rebinding.

Cloud classification fails closed for private, carrier-NAT, loopback, link-local, multicast,
benchmark, unspecified, and documentation ranges; private and loopback classes accept only their
exact address families. Hosted location substitution fails before credential materialization.
Residual risks are compromised public routing, CA/TLS compromise, OS socket behavior, and already
approved pooled connections within their five-minute lifetime. Container/firewall egress controls
remain separate gates.

Named compatible profiles cannot use provider-type ambiguity to select an adapter. Setup normalizes
and requires one of four exact identities, binds the configured OS secret store, materializes one
bounded header-safe lease, clears it, and records conservative unprobed capability evidence.
OpenAI/DeepSeek plaintext is denied; vLLM/generic plaintext is limited to inferred local/private
destinations and remains subject to invocation-time address policy. Configuration never fabricates
tool or media support and never adds a provider to the production catalog.

## M4 durable-DAG update

Graph structure is untrusted until node IDs, bounds, dependency existence, acyclicity, unique
capabilities, context evidence hashes, budgets, retries, and compensation authority validate.
Mutable caller collections are copied before hashing. Models cannot provide a lease token or alter
the persisted policy/budget/skill snapshots.

Worker possession is a random 256-bit token whose hash alone enters snapshots and audit. Exact task
version, owner, token hash, live expiry, and node state are checked before terminal evidence is
accepted. A stale or crashed worker therefore cannot overwrite takeover, and a completed node can
never be reclaimed. Remaining threats are dispatcher starvation, malicious but already authorized
workers, delegation-context overexposure, and schedule-trigger flooding; the latter two are the
next Milestone 4 gates.

## M4 delegation update

A child request is intent, never authority. Unknown optional capabilities disappear at the
intersection; an unknown required capability denies the grant. Requested context must be a subset
of parent evidence hashes, preventing full-parent-context inheritance by default. Requested budget
is independently clamped by parent remaining and per-child limits.

Depth, spawned-child count, active concurrency, expiry, identity bounds, duplicates, and zero useful
budget are checked before a canonical grant is issued. Policy and skill hashes are inherited
unchanged. Remaining threats are incorrect trusted parent accounting and a future public endpoint;
neither exists in the current closed composition surface.

## M4 scheduling update

Cron/calendar text, timezone IDs, scan horizon, queue size, catch-up, jitter, parallelism, leases,
attempts, and failure thresholds are bounded before persistence. Unsupported cron extensions and
missing zones fail typed. DST gaps and ambiguity cannot invoke platform-default guesswork.

Every due and run-now occurrence has a deterministic scoped identity. Latest-version scanning,
expected versions, and database uniqueness prevent two dispatchers from authorizing the same
transition. Worker tokens are random and hash-only. Definitions pin agent and policy/capability/
budget/skill evidence so a later configuration change cannot silently widen a schedule. Remaining
threats are timezone database changes to future calculations and starvation by an authorized flood;
stored UTC snapshots preserve evidence and bounded scanning/queues limit the latter.

## M5 governed-skills update

Skill directories are untrusted input. Loading rejects linked roots, linked files or parents,
escapes, invalid UTF-8/JSON, duplicate or unknown manifest properties, excessive files/bytes/depth,
invalid SemVer/IDs/permissions/requirements, missing exact dependencies, and dependency cycles.
The default signature verifier rejects signed packages until a trusted-key adapter is configured;
an untrusted signature can never convert validation into trust.

Only the canonical bundle enters the content-addressed artifact store. Registry rows contain
descriptors and hashes, never `SKILL.md`; model-visible bodies require an exact immutable run
snapshot and are re-hashed while opening. Search discloses descriptors only. This bounds prompt
exposure and prevents an active session from observing a later promotion.

Proposals bind the exact candidate and active-baseline hashes. Deterministic target, holdout, and
adversarial failure vetoes progress; the proposer cannot approve; stale baseline races deny canary
completion. A separate relational active pointer enforces one exact version per skill while status,
pointer, append-only proposal snapshot, and redacted audit commit in one transaction. Failed canaries
quarantine and rollback restores only the exact prior hash. Remaining risks are compromise of a
future trusted signing key and malicious content within already granted skill permissions; runtime
policy and sandbox gates remain authoritative over package declarations.

## M6 coding-harness update

Repository paths, project XML, source text, patches, backend proposals, command plans, and compiler
output are untrusted. Discovery and semantic navigation are read-only and bounded. Coding begins
from an exact clean commit/tree in a separate linked worktree, preventing operator changes in the
source checkout from entering the mutation boundary.

Every patch binds the baseline tree and exact raw SHA-256 of each target. Canonical paths cannot be
rooted, traverse, or cross links. Strict UTF-8, file/set sizes, headers, hunk coordinates/counts,
and context are validated for the entire set before the first write; staged same-volume moves and
backups roll back write failures. Receipts retain only before/after hashes and line counts.

External backend requests contain session/evidence identifiers but no filesystem path and their
only output is a patch proposal. They cannot declare verification success. Harness-owned command
plans rebind the workspace and authority, pass literal arguments and environment allowlists to the
sandbox, and persist output hashes rather than content. Project build/test/analyzer/format/coverage/
security/dependency/publish execution requires denied-network container plus filesystem isolation;
publish additionally requires an exact external-mutation approval. Until a container adapter is
available, these return typed unsupported capability rather than falling back to the host.

Durable coding sessions store objectives as hashes and raw unified diffs only as content-addressed
artifacts. Relational snapshots bind the exact workspace, authority, repository profile, backend,
instruction hashes, typed plan, patch/verification/review receipts, state, version, correlation, and
previous hash. Each completed verifier command appends and commits before the next begins, so worker
loss retries only the unrecorded command. Git review hashes the complete baseline diff, fails on an
empty/unrelated path set or `diff --check`, and model/backend output cannot declare completion.
Remaining risks are host compromise, a malicious trusted in-process backend, and insufficient live
container isolation; external/untrusted backends remain patch-only and must use the later constrained
out-of-process plugin adapter, while missing container support remains a typed unavailable gate.

## M7 governed-research update

Search providers, query text, remote JSON, URLs, titles, snippets, status codes, and quota evidence are
untrusted. Requests are identity- and scope-bound, provider selection is explicit, endpoints for Brave
and official Google search are exact HTTPS origins, redirects/proxies/cookies are disabled, response
size/depth/result counts are bounded, and credentials exist only in invocation-local headers.

Canonical URL identity removes fragments before deduplication. Citations retain exact source URI,
provider IDs, bounded excerpts, rank score, and evidence hashes; provider content never changes policy
or capability. Throttling and outages are typed and allow independent cited evidence to survive, while
an all-source failure is retryable rather than fabricated. The production catalog remains empty.

## M7 scoped-memory update

Memory text, provenance, source URLs, retrieval text, kind declarations, retention, and scope IDs are
untrusted. Creation validates exact installation/agent/scope identity, enforces kind-specific source,
size, and retention constraints, applies structured sensitive-data redaction before repository access,
and stores content/source hashes with correlation and audit evidence.

Retrieval intersects installation, agent, scope, kind, expiry, literal escaped text, and result bounds.
Memory is data only and cannot add capabilities, instructions, or policy. Deletion requires the exact
same installation/agent/scope tuple, removes the row atomically with a hash-only audit event, and SQLite
secure deletion is enabled. Backup copies created before deletion retain their historical data and must
remain subject to the R1 encrypted-backup retention and destruction runbook.

## M7 governed-channel update

Webhook headers/bodies, provider IDs, sender/recipient IDs, timestamps, text, attachment metadata,
transport status, and remote JSON are untrusted. The selected exact account adapter authenticates raw
bytes before parsing: Telegram uses a fixed-time webhook secret-token check and WhatsApp uses the exact
HMAC-SHA256 body signature. Sender identity must resolve through a durable installation/agent binding.

Inbound records bound body/header/result sizes, time skew, attachments, hashes, and order keys. Every
attachment passes a separate scanner; the production default rejects. A duplicate provider identity is
idempotent only for the same normalized hash and conflicting replay fails. Outbound sends build a canonical
recipient/body/account request, consume the existing exact external-mutation approval before transport,
and enforce quiet hours, hourly rate, and attempt limits. Definite throttling may retry; timeouts, transport
errors, server uncertainty, and malformed success evidence dead-letter without automatic replay.

Official adapters accept text only at this gate; media payloads fail instead of being silently discarded.
Credentials are invocation-scoped. Telegram's protocol requires its token in the request path, so the
adapter uses a private direct `HttpClient`, emits no URI/error body evidence, and never registers live
accounts by default. Remote-mode webhook exposure remains disabled until Milestone 10 hardening.

## M7 and post-R1 loopback web-setup update

Browser origins, cookies, internal bootstrap grants, CSRF tokens, idempotency keys, JSON fields, credential bytes, and repeated
requests are untrusted. Every web-setup endpoint verifies loopback remote address and, when present, exact
same-origin scheme/authority. A random 256-bit one-time grant is consumed internally into a 30-minute HttpOnly,
SameSite=Strict cookie session with an independent CSRF token; bootstrap material is never returned or accepted
as operator input. The same cookie resumes a live session after refresh, while a second browser is denied until
the active session completes or expires. Mutation keys bind operation and request hash;
conflicting reuse denies and exact success replays do not repeat durable work.

Provider metadata is staged before a separate bounded `text/plain` credential body. Strict UTF-8 decoding
targets a clearable character array, the existing setup service stores only its OS-secret reference, and the
buffers are cleared in all outcomes. The browser necessarily owns its input string until submission and
clears the field immediately. CSP, anti-frame, no-sniff, no-referrer, permissions policy, and no-store headers
remain active. Completion makes the session exact-replay-only; another setup session cannot reopen Ready state.

Provider base endpoints, model-catalog JSON, model identifiers, and probe responses are untrusted. Discovery is
limited to installed compatible provider types, rejects user-info/query/fragment endpoints, permits plaintext only
for loopback/private destinations, resolves and connects through the existing destination policy, disables redirect,
cookie and proxy inheritance, and bounds time, headers, body, JSON, model count and identifier length. A selected
model must come from the current catalog or an explicit session-bound manual entry before one mandatory bounded
chat probe; manual entry never marks the model tested. Credentials use a clearable buffer
and are never included in response evidence. Credential-free profiles use a typed sentinel only for loopback/private
vLLM or generic-compatible endpoints; hosted construction rechecks that restriction before omitting Authorization.

The wizard constructs the same conservative defaults as omitted CLI options and calls the same preview,
provider, agent, and completion services. It exposes no runtime/model/tool/channel/device mutation. Remote
wizard use is denied even if the host is explicitly rebound; hardened remote administration is Milestone 10.

## Post-R1 Ready operator workspace

The Ready browser workspace treats local pages, origins, cookies, CSRF input, request identifiers, agent/run
labels, and packaged skill content as untrusted. It is loopback-only and exact-origin checked. To avoid placing
the random administrator bearer credential in JavaScript or browser storage, the server materializes the
current OS user's secret reference, authenticates it against the verifier, clears the lease, and issues a
separate random 30-minute HttpOnly SameSite=Strict cookie. Only one live session is retained for the
installation. State changes additionally require an independent fixed-time-checked CSRF token and a bounded
idempotency key; all responses remain `no-store` and rate limited.

The session is bound to the exact Ready installation and becomes stale when installation state or identity
changes. Agent reads are installation-scoped. Run creation selects an existing exact agent/version and pins
policy, budget, child, and skill-grant hashes before calling the durable orchestrator; cancellation uses the
latest concurrency version. Only the explicit interactive prompt endpoint may claim a node. It requires a
local-only/no-fallback/zero-tool agent and its exact credential-free loopback/private compatible profile,
prepares and redacts the two-message context, rejects any tool or structured-output event, applies hard
token/event/time/character bounds, and persists only a response evidence hash. The raw prompt exists in the
browser/request and the raw response exists only in the response plus a bounded in-memory idempotency cache;
neither is written to the durable orchestration record. Seed installation resolves only the packaged fixed
directory and calls the same package validator, artifact store, audit, and registry service as other
provenance. Installation never activates a skill. Autonomous execution, tool calls, promotion, external
messaging, and device writes remain unavailable from this workspace.

Same-user OS-account compromise remains outside the local single-operator boundary: that user can already
materialize the protected credential and access AgentForge files. Cross-origin browser requests, non-loopback
clients, missing/stale cookies, missing CSRF, changed installation scope, unknown agents, and missing seed
packages fail closed. Deterministic end-to-end tests cover hostile origin, secret non-disclosure, missing CSRF,
durable create/list/cancel, exact/conflicting prompt replay, hostile emitted tool calls, durable prompt
completion evidence, and validated seed install/list.

Explicit LAN mode adds the remote client, TLS certificate, exact origin, host firewall, and temporary access
code as trust boundaries. It is disabled by default. Enabling it requires HTTPS plus a 20-256 character code
and at least one exact HTTPS origin; missing/incorrect codes cannot mint a session. Subsequent requests require
the Secure/HttpOnly/SameSite cookie and normal CSRF controls. Safe same-origin GET/HEAD navigation may omit an
`Origin` header, while remote mutations require the configured exact origin. Forwarded headers are consumed
only from loopback with a one-hop limit. Operators must constrain the firewall to the private interface/local
subnet and must not expose the listener through router forwarding. A self-signed certificate is acceptable
only for an operator-controlled test; production requires a trusted managed certificate.

## M8 passive serial discovery boundary

Device discovery reads Windows serial registry metadata or Linux sysfs/device-node existence only. The inventory
implementation contains no serial-open, write, shell, or process-execution primitive, so candidate enumeration cannot
toggle DTR/RTS or transmit bytes. Stable identity is hashed from hardware evidence independently from a transient COM or
`/dev/tty*` endpoint; re-enumeration is an explicit event rather than a new authority. Profiles default DTR and RTS off.
Inventory, capture, read, write, command, calibration, firmware, and privileged access are separate expiring grants;
having any one does not imply another. Unknown readiness remains explicit and never silently becomes permission.

Serial transport is a separate explicit boundary. The production catalog is empty, so discovery cannot become I/O and
all real hardware remains gated. Capture, one-shot read, and write each rebind the grant to the physical device and exact
operation; a capture grant cannot read or write. Profiles and byte limits are validated before an adapter call, partial
write confirmation fails retryably, and unsupported platforms fail typed. Captures accept only monotonic bounded frames,
account zero-byte drop/disconnect evidence, stop at byte/time bounds, and store raw bytes only in a versioned little-endian
content-addressed artifact. SQLite and audit retain hashes/counts rather than payload bytes. Replay validates artifact
length, magic/version, physical-device binding, frame bounds/order, byte/drop totals, and a canonical stream hash before
yielding data. Artifact tampering is a hard integrity failure.

Decoder definitions are data, not executable plugins. Bounds cover definition size, frame/sync/field counts, input bytes,
parser operations, evaluation corpus, and deterministic fuzz cases. Known fields are typed, while every unclaimed frame byte,
pre-sync noise byte, and partial tail remains exact evidence; the raw frame hash prevents decoded values from replacing source
truth. The evaluation suite hash binds target and holdout bytes plus expectations. Promotion requires passing target, holdout,
malformed, partial, concatenated, resynchronization, unknown-preservation, fuzz, and operation gates.

Candidates may request only protocol-decode authority; device capture/read/write/firmware, filesystem, and network authority
make the definition invalid. Proposer cannot evaluate, approve, or govern their candidate. Proposal snapshots form an
append-only hash chain bound to the exact baseline and candidate. Competing candidates can evaluate, but only the one whose
baseline still matches can atomically become active. Failed canaries quarantine without activation. Active candidates can be
quarantined and rolled back only while their exact hash remains selected. Declarative decoders never inherit device-write
authority.

## M9 recursive-learning boundary

Learning summaries, trajectory references, usage receipts, procedure chains, package candidates, evaluation
claims, and role identities are untrusted. Accepted summaries are bounded, reject credential-shaped material,
and persist beside source, signal, classification, workspace, package, and snapshot hashes. Raw model context,
tool output, secrets, and candidate source are not relational learning fields. A signal classification is data
only and cannot alter policy or become executable instructions.

The Ready learning-intake endpoint accepts only a task in a terminal state from the authenticated session's exact
installation. It binds the signal to the durable task snapshot hash and run causation ID, normalizes the operator's
redacted summary to one bounded line, rejects credential-shaped material through the domain classifier, and applies
the same CSRF, exact-origin, serialized mutation, and idempotency boundary as other Ready administration. Listing is
installation scoped and bounded. Intake deliberately supplies no usage receipt, revision authorization, or repeated
skill chain, so hostile browser input cannot claim revision or bundle authority and a `NewSkill` result remains data.

The Ready candidate endpoint accepts only an existing installation-scoped `NewSkill` signal and derives candidate and
proposal IDs from the installation plus idempotency identity. It writes only two server-generated filenames beneath an
exact candidate directory, rejects links, unexpected prior content, invalid UTF-8/package metadata, conflicting immutable
versions, and a second candidate for the same signal. Registry installation uses `AgentProposal` provenance and remains
`Installed`; the content-addressed PAX workspace and candidate snapshot grant no execution authority.

Candidate transitions accept no source content, package replacement, role identity, permission expansion, provider, tool,
or policy fields. They resolve the five installation-scoped role actors server-side, require current snapshot versions,
accept only hash-shaped receipts for evidence-bearing gates, and delegate every change to the existing learning and skill
state machines. Browser evidence notes are hashed locally and never cross the trust boundary. This protects secrets and
limits stored content, but it does not prove the stated suite actually ran; R1 post-production UI receipts are explicit
operator attestations until the isolated automated evaluator replaces those booleans. Promotion changes only the skill
registry pointer, and agent-level grants remain independently required. Rollback quarantines the candidate and restores
only an exact hash-bound baseline.

Existing-skill revisions require an exact successful usage receipt or unexpired explicit operator authorization
for the current skill version and package hash. A candidate must be an immutable agent-proposed package and carry
a content-addressed isolated-workspace receipt. Worker, proposer, verifier, critic, and governor actors are all
distinct. Target, holdout, adversarial, permission-diff, critic, baseline, and canary failures deny or quarantine;
the learner calls the same skill-governance service used by seed and user packages. It has no path to expand
permissions, approve itself, edit active pointers, or bypass rollback evidence.

Repeated chains create references, not copied instructions. Bundle nodes pin exact package and input/output
contract hashes, adjacent contracts must match, and authority is only the sorted union of permissions already
held by the pinned packages. A separate verifier, critic, and governor advance the append-only proposal chain.
Immediately before activation, every package is re-resolved and a missing, archived, quarantined, or changed pin
denies the transition. Prompt-injection text therefore cannot mint a capability through either learned skills or
bundles.

## M10 production API and MCP boundary

Every `/api/v1` mutation requires the local administrator and an exact bounded idempotency key. Correlation IDs
are normalized at ingress; problem responses, event streams, and OpenAPI omit credential material and raw task
content. Request size and request rate are bounded. A non-loopback URL is invalid unless remote mode, HTTPS, and
an exact non-wildcard origin list are configured together. Browser MCP requests inherit the same exact-origin
policy and remote-address guard.

MCP advertises a deliberately tiny server surface and evaluates the exact tool or resource again at invocation.
An installation that is not Ready, an unauthenticated HTTP caller, an unbounded identity, or an absent allowlist
entry fails closed. Streamable HTTP is stateless and rate limited. The stdio command accepts only an explicit data
directory, starts only against Ready durable state, inherits no web session, and routes all diagnostic output to
stderr so it cannot corrupt or inject JSON-RPC on stdout. MCP results expose readiness metadata only; they cannot
invoke tools, mutate durable state, materialize secrets, or expand policy.

MCP client profiles are immutable and distinguish loopback HTTP, public HTTPS, and local stdio. Public HTTPS
requires a secret reference; its value is materialized into one request header and removed immediately after the
send. Every new socket resolves the configured hostname and rejects the entire answer if any address is outside the
declared scope, preventing private-address substitution and DNS rebinding. Redirects, cookies, ambient proxies and
environment variables, relative commands, and missing command paths are not accepted. Remote catalogs are filtered
to exact configured names, and call/read APIs re-check those names before transport. Results and argument documents
are bounded so a configured MCP peer cannot turn discovery or invocation into unbounded model context.

Plugin manifests and assemblies are fully untrusted. Discovery walks only direct link-free package directories,
accepts an exact bounded JSON schema, verifies the entire assembly hash, and never calls reflection or executes a
candidate. A signature field is not trust; only the configured verifier can mark it valid. The loader re-hashes at
the point of use and denies a changed file. In-process loading requires both verified signature and low risk, checks
the SDK identity after construction, and uses a collectible context.

All unsigned, untrusted, medium-risk, and high-risk plugins cross a versioned one-shot worker protocol containing
only exact identity, hashes, entry type, and declared permissions—never secret values. Admission demands a container
sandbox with denied networking and explicit filesystem, CPU, memory, process-count, output, timeout, and process-tree
isolation. Restricted-host execution is insufficient and returns a typed unsupported result. The worker independently
validates the assembly and emits only a bounded identity receipt before exiting.

PostgreSQL connection strings are untrusted secret input and are read only from an explicitly named process
environment variable. Sensitive EF logging is disabled. Backup removes the password from the `--dbname` argument,
passes it only as `PGPASSWORD` to an otherwise empty child environment, invokes an exact absolute binary without a
shell, bounds stdout/stderr and wall time, and kills the process tree on failure. Restore refuses the configured
source secret name and requires a distinct explicit target, preventing an ordinary verification run from cleaning
the live database. Backup/restore directories must be separate, empty, contained, and link-free; every database and
artifact file is length/hash verified before restore. A manifest hash covers provider, time, identity, ordering, and
all file evidence.

Trajectory exports treat even redacted audit JSON as untrusted input. Export is blocked unless the entire global
audit sequence and hash chain verifies. Events are bounded, scoped to the current installation, optionally filtered
by an exact correlation ID, parsed as JSON, and passed through the structured redactor again before serialization.
The export preserves source event hashes rather than recomputing them over transformed content and records the
secondary-redaction count. It stores no provider response text, model context, process output, or credential beyond
what the append-only redacted audit contract already admitted. A content hash binds the exact JSON bytes; a durable
receipt binds request scope and idempotency. The CLI must materialize the OS-backed local administrator credential
before it can invoke export, then disposes the lease without including the credential in the request or artifact.

Release inputs, package trees, archives, service definitions, and backup packages are untrusted. Release output is
restricted to one validated artifact subtree; links and traversal are rejected, files are ordered, timestamps are
commit-derived, and a complete SHA-256 manifest includes the SPDX document. GitHub OIDC attestations bind workflow
identity to archives and image digests without a repository signing secret. The container runs as the platform app UID,
binds loopback only, and has no plaintext fallback when an OS secret facility is unavailable.

R1 secret references are user-scoped, so a service identity change is an authority and recoverability change. Windows
installation requires the current operator's prompted `PSCredential`; Linux uses the operator's systemd user manager.
LocalSystem, root, or a detached account is not the supplied topology. Backups include the online database, artifacts,
protected secret files, and auxiliary state, verify every hash, and restore only into a separate empty target. PostgreSQL
also requires a target connection variable distinct from the configured source. A destructive down migration is never
used as rollback.

## Ready streamed-interaction boundary

Provider stream content, timing, and termination are untrusted. The public Ready stream accepts only the exact
installation-owned local-only agent and pinned credential-free loopback/private compatible profile, with no
fallback, tools, structured output, files, messages, devices, or other capability. The existing provider socket
policy still binds the actual destination. Prompt, output, and remote error bodies remain transient, bounded,
context-redacted, and excluded from persistence and replay caches.

Cancellation authority is not represented by knowledge of a task ID. The protected mutation must resolve the
same installation and Ready session, append durable cancellation with optimistic concurrency, and only then may
signal the matching in-memory invocation. Cross-installation, stale-session, and unknown-task attempts cannot
reach the cancellation source. A provider racing completion loses to the already committed terminal state and
cannot replace Canceled with Completed.

The SSE response is authenticated by the short-lived HttpOnly session, mutation CSRF/origin checks, and a fresh
idempotency key. Buffering is disabled, caching is forbidden, event names are fixed, JSON serialization is
server-owned, output/event/token/time limits remain enforced, and browser rendering uses text-only DOM APIs. A
reused key is rejected instead of regenerating raw output. Client disconnect cancels the invocation but is not
treated as an operator cancellation; failure evidence is persisted when the lease still permits it.

## Ready profile-administration boundary

Ready configuration input, model catalogs, model probe output, raw credentials, grant identifiers, browser state, and
previously issued previews are untrusted. Every operation requires the protected operator session, exact
same-origin/HTTPS-remote checks, session CSRF, and a bounded idempotency key. It scopes every entity to the session
installation and authenticates the same local administrator again through an invocation-scoped OS-secret lease before
the setup profile editor evaluates or commits a candidate.

Existing-provider model edits preserve the current type, endpoint, and secret reference. New providers accept their
complete topology, but the selected model must produce visible bounded text through a thinking-disabled probe both
before preview and immediately before apply. A submitted credential is bounded, briefly placed behind a transient
OS-backed secret reference for each probe, then deleted; the preview retains only its SHA-256 fingerprint. Apply must
receive the same credential, and any probe, fingerprint, validation, transaction, or cancellation failure attempts to
delete the transient or uncommitted final secret. The HTTP request binder necessarily holds its immutable credential
string until garbage collection, but browser code clears the field immediately and the server never places that value
in session state, logs, audit, configuration, relational state, artifacts, or responses.

Agent creation and editing use a complete candidate rather than a sequence of partial grants. Every provider binding,
locality/fallback choice, memory setting, network posture, exact tool/skill grant, run budget, child limit, and learning
permission passes the same conservative policy evaluator. Newly added tool grants must exist in the current immutable
catalog and skill grants must reference current Active skills. Ready-state bounds additionally limit generated output
to 256–262,144 tokens and tool invocations to 0–1,000, with zero invocations when no tool is granted. Missing or
unknown authority is denied.

Preview hashes bind installation/provider/agent versions, normalized complete candidate, actor, correlation, and
credential fingerprint where applicable. Previews are short-lived in-memory session objects; apply requires the exact
hash and repeats external validation under optimistic concurrency. A committed create/edit increments the installation
while preserving Ready state and transactionally appends a redacted create/edit audit event. The response reports that
authority-version changes invalidate continuation: old immutable run snapshots remain evidence, but an existing
conversation cannot silently adopt the new agent/provider policy. A crash, stale preview, substituted credential, or
changed grant catalog cannot partially apply or widen authority.

Run response presets and browser numeric bounds are convenience controls, not policy evidence. The authenticated
stream endpoint independently clamps the effective agent ceiling to the 262,144-token hard cap and rejects an explicit
limit below one or above that ceiling before task admission. The exact accepted value is bound into the run snapshot,
event configuration, idempotency request hash, and provider request. A larger output allowance does not expand tool,
network, skill, file, message, device, credential, or fallback authority; interactive wall-clock time remains capped at
270 seconds and the corresponding node lease never exceeds five minutes.

## Ready tool-approval boundary

Tool catalog data, browser parameters, filesystem paths, previously issued previews, and handler output are untrusted.
The Ready surface lists only immutable authoritative descriptors and reconstructs every grant candidate from the current
agent. A grant may change one exact tool capability and only the per-run tool-invocation ceiling; network posture,
skills, every other budget member, and all other agent authority must compare unchanged. The existing administrator
secret, CSRF/origin/session checks, optimistic installation/agent versions, preview hash, audit, and transaction remain
mandatory. Catalog visibility alone never authorizes execution.

Invocation preview and execution share one canonical planner. It fixes capability, risk, target kind, side effects,
sandbox, network policy, timeout, output limit, and handler from the exact tool/version/descriptor hash; the caller can
provide typed descriptor parameters and an absolute workspace only. Authorization binds canonical parameters,
normalized target/workspace, actor, agent policy version, installation version, expiry, correlation, and request hash.
Missing grants deny, grant/deny decisions are exact and expiring, a grant is consumed before execution, and a consumed
preview cannot be reused. Raw output is returned only to the authenticated response and is never placed in relational
state, audit, or idempotent durable replay; only hashes, lengths, exit state, and approval evidence persist.

The initial managed workspace handlers are not a weak process sandbox. They start no process, receive no environment,
and have no networking primitive. Preview and execution independently require an existing absolute target contained by
an existing absolute workspace, reject UNC workspace roots on Windows and every traversed link/reparse point, enumerate
only direct children without following links, cap entry/output counts, and read only bounded strict-UTF8 files. Unknown
handlers, detected traversal/link paths, binary input, oversized files/output, cancellation, and I/O errors fail typed.
A same-user filesystem replacement race between validation and managed read remains possible under the documented
single-operator OS-account boundary; the initial catalog is therefore read-only and grants no credential access.
Process-backed tools still pass through their declared sandbox and cannot degrade to this built-in execution kind.

## Automated learning-evaluator boundary

Proposal workspace archives, artifact metadata, installed package records, skill Markdown, manifests, declared
permissions, hostile candidate instructions, and browser requests are untrusted. The Ready endpoint accepts only a
candidate ID and current version behind the existing authenticated session, same-origin/HTTPS-remote, CSRF,
idempotency, rate-limit, and installation-scope controls. Verifier pass flags, scores, evidence hashes, content, and
permission changes are not accepted. The assigned verifier identity remains server-derived from immutable candidate
state.

Each evaluation opens the exact content-addressed workspace and independently recomputes its length and SHA-256 hash.
It extracts into a unique AgentForge-data-directory sandbox using regular files only, rejects traversal, rooted paths,
links, duplicates, unexpected names, file-count and byte-bound violations, then loads the package twice through the
ordinary strict portable loader. Both canonical loads must match each other, the installed AgentProposal descriptor,
candidate ID/version/package hash, and exact sorted permission declarations. No process, environment materialization,
network primitive, provider, tool, or active skill authority is available in this managed evaluator.

The hostile corpus rejects instructions that request policy/approval bypass, secret disclosure, exfiltration, system
prompt access, or self-granted authority. Automatic permission evaluation permits only exact declarations ending in
the read-only `:read`, `.read`, `:metadata`, or `.metadata` forms. Every unknown or wider declaration is rejected rather
than escalated to an implicit approval. Passing evaluation does not activate or
grant the skill: separate critic, governor, canary, active-version, and per-agent grant gates remain mandatory.

The receipt contains only immutable identifiers/hashes, evaluator version, bounded check codes/summaries, and computed
scores. It is deterministic JSON in the content-addressed artifact store, and only its hash enters candidate and audit
state. Sandbox cleanup failure can leave bounded non-secret proposal material under the AgentForge data directory but
cannot write outside it. Candidate-authored executable behavioral tests are not run by this managed evaluator; adding
them requires the digest-pinned container isolation boundary and a separately gated test contract.

## Local-model skill-generation boundary

Classified summaries, source-run metadata, optional operator guidance, model output, and browser candidate fields are
untrusted. The Ready endpoint scopes the signal to the authenticated installation, derives its source task from the
immutable causation ID, requires the latest terminal task snapshot hash to equal the signal's source-evidence hash, and
derives the agent ID from that task. The browser cannot select a generation agent, provider, model, evidence hash,
generation receipt, or generated body.

Generation reloads the current agent and exact pinned provider. Agent policy must be `LocalOnly`, forbid fallback,
permit `Propose` or `ScopedAuto` learning, and restrict mutation to a proposal workspace/approved class. The provider
must be a credential-free vLLM/OpenAI-compatible text profile; the model adapter separately restricts its endpoint to
loopback or a literal private network. The request supplies zero tools, no skill bodies, no environment, no process,
no credential, and no external fallback. The fixed system instruction treats every user-message field as reference
data, but authorization never relies on model obedience.

AgentForge preflights all generation input through the sensitive-data redactor and vetoes any detected secret rather
than silently persisting it. Model output remains transient until it parses as one exact JSON object with only a
bounded `markdown` string, six exact procedure headings, no package-internal markers/frontmatter, no active script tag,
and no sensitive-data match. A non-Stop/truncated response, malformed JSON, duplicate/extra field, missing heading, or
policy failure creates no package or candidate. No raw prompt or complete raw response is persisted.

The accepted Markdown is wrapped between AgentForge-owned markers. `generation.harness.json` binds signal and source
hashes, candidate/skill/version, agent/provider/model versions, stable model request, provider evidence, raw-response
hash, selected-body hash, generation-request hash, redaction count, and finish reason. Both files enter the ordinary
portable package hash and the content-addressed proposal workspace. A durable replay verifies that exact receipt before
returning and never calls the model again for a matching candidate ID. The automated evaluator rehashes the marked body
and checks the receipt before `Verified`; independent critic, governor, canary, activation, and per-agent grant gates
remain mandatory because well-formed model output may still be substantively wrong.

## AI-generated HTTP API skill boundary

API documentation, missing-capability evidence, operator guidance, generated skill Markdown, profile labels, relative
paths, scalar query values, external response JSON, and model tool calls are untrusted. A generated package may name
only exact catalog tool IDs supplied to generation. Required IDs are request-hashed, provenance-bound, manifest-hashed,
replay-compared, and independently checked against the evaluator's read-only allowlist. Declaring
`tool:http-api.get` does not configure a profile, activate the package, grant it to an agent, change network posture,
create an approval, or materialize a credential.

Named API profiles accept exactly one HTTPS base origin, a contained relative probe path, bounded non-secret headers,
an enabled flag, and a write-only bearer token. Authorization, cookie, proxy, host, transport, API-key, token, secret,
credential, and subscription-key header names are rejected; unknown brace templates are rejected. Only server-owned
`{correlationId}` and `{requestId}` expansion is allowed. Configuration input is redaction-scanned, live-probed on
preview and apply, versioned optimistically, fingerprint-bound in session memory, audited without the token, and stored
with an OS-secret reference. Rotation deletes the old reference only after the new profile transaction commits.

The run loop exposes the generic model contract only when the selected immutable skill is Active and agent-granted,
its current package requires the generic tool, the agent separately grants `tool:http-api.read`, network posture is
`ApprovedEndpointsOnly`, tool budget remains, the profile is enabled, and model transport is compatible. The browser
cannot choose an unconfigured endpoint: model profile/path/query arguments are schema-checked, resolved from durable
profile state, and rebound as the invocation target. Approval covers the exact descriptor/version, canonical arguments,
resolved endpoint, agent/policy/workspace scope, expiry, and request hash. The built-in handler resolves the profile
again, compares endpoint and target byte-for-byte, materializes the bearer once, performs a no-redirect/no-cookie/no-
proxy bounded GET, accepts strict UTF-8 only, and returns a hash-bearing bounded result. Dot segments (including encoded
forms), absolute/network paths, cross-origin redirects, non-scalar query values, output flooding, stale profile state,
and duplicate approvals fail closed.

Residual risks are substantive API semantics in otherwise safe generated instructions, a bearer whose server-side
scope is wider than AgentForge intends, and sensitive data legitimately returned by the approved endpoint. Operator
review, least-privilege/short-lived bearer issuance, exact per-call approval, bounded output, deterministic holdout
fixtures, canary, and rollback remain required. Candidate-authored behavioral assertions are not trusted until the
future digest-pinned container evaluator executes them independently.

## Durable conversation boundary

Conversation prompts, run guidance, prior assistant text, model streams, artifact files, browser-selected run IDs,
idempotency keys, and interrupted task state are untrusted. Initial and follow-up mutations require the existing
authenticated Ready session, origin/CSRF controls, rate limits, installation scope, and fresh exact idempotency. The
server derives conversation, turn, and task identities from the installation and mutation key. A conversation pins the
exact agent/provider versions, model ID, policy, budget, system instruction, and immutable skill snapshot; continuation
or resume fails if current authority no longer matches. The browser cannot replace stored context or resume an older
turn.

Before persistence and model use, system, user, and assistant text pass through the sensitive-data redactor.
Credential-shaped strings become `[REDACTED]`. Accepted text is stored only as bounded UTF-8 content-addressed
artifacts; append-only conversation snapshots contain hashes and metadata. Every details/history read verifies media
type, length, strict UTF-8, and SHA-256 before returning text. Audit contains artifact/request/response/evidence hashes,
never conversation bodies. Redaction reduces accidental secret retention but is not a general data-loss-prevention
guarantee; the single operator must still avoid supplying unrecognized secrets.

Every turn owns a distinct durable orchestration task, random hash-only lease, bounded retry/tool ceilings, and existing
output/event/wall-clock limits. A tool-capable provider receives only descriptors intersected by the exact current
agent grant and network posture; today that model-facing set is either empty or the single bounded `search_web`
contract. At most 20 completed turns and 100,000 characters are reconstructed
as alternating user/assistant messages; only those roles and single bounded text contents are accepted. A conversation
holds at most 64 turns. Completed turns cannot resume or replay. A retryable provider/browser interruption becomes
`NeedsResume`; a crash-orphaned lease can be reclaimed only after expiry. Resume uses the same stored prompt and task
authority, while an active unexpired lease conflicts. Explicit cancellation durably cancels both the task and current
conversation turn before signaling the live provider call.

An accepted model tool call is not authority to execute. AgentForge validates the exact tool name and JSON schema,
stores the canonical call in the conversation hash chain, moves both task and turn to `ApprovalRequired`, and exposes
only an authenticated approval preview. Grant and denial bind the official endpoint, query/result count, descriptor,
agent/installation versions, actor, expiry, correlation, and request hash. A grant is consumed once before the managed
handler runs; a denial performs no network request. The bounded JSON result is hash-chained and supplied as a typed
tool-result continuation. Unknown tools, changed policy, malformed arguments, stale previews, missing credentials,
exhausted budgets, or unresolved calls fail closed and cannot silently become text-only execution claims.

## Scheduled execution and attached-context boundary

Schedule forms, recurrence fields, prompts, run guidance, memory text, search queries, provider results, citations,
receipt hashes, and browser-selected identities are untrusted. Every mutation requires the authenticated Ready
session, exact origin/CSRF, bounded idempotency, installation scope, and optimistic versions. Schedule creation uses
a write-free recurrence/authority preview and then revalidates the exact agent, provider, Active granted skills, and
installation versions. The immutable template stores only redacted content-addressed prompt/system artifacts plus
exact policy, capability, budget, skill, agent, provider, and model pins. Editing any pinned agent/provider authority
requires a replacement schedule; it never silently changes existing automation.

The dispatcher only creates due occurrences. A separate bounded worker claims an exact hash-only occurrence lease and
derives one deterministic task, conversation, and turn identity from that occurrence hash. A crash after model
completion reopens the durable Ready conversation and completes the occurrence without invoking the model again. An
expired retryable task lease can resume the same incomplete turn; terminal failed/canceled conversations never rerun.
Legacy schedules without an immutable run template remain inert to this worker. Unsupported or changed authority fails
typed rather than degrading.

Memory operations derive the effective Agent or Operator scope from the current agent policy; Task-scoped memory is
not attachable before a new task identity exists. Entries retain exact source kind/evidence hash, redaction count,
expiry, and deletion tuple. Research executes only after an exact operator preview binds agent version, query,
provider set, and result limit. Provider credentials remain invocation-scoped inside adapters. The resulting bounded
citation receipt is session-held and must be attached explicitly to the same agent.

The Ready Brave configuration surface accepts credential material only in an authenticated CSRF/idempotency-bound
mutation. Preview and apply independently probe the fixed official HTTPS endpoint; the session retains a SHA-256
fingerprint and sanitized policy only. Apply writes an opaque OS-secret reference and the provider profile plus audit
event in one database commit, then removes the superseded reference. Secret material is never returned to the browser,
placed in configuration JSON, audit, cache identity, citation evidence, or model context. The versioned profile evidence
hash includes the opaque reference identity, so rotation, enablement, safe-search, country, or language changes invalidate
stale research approvals and cache keys. A managed Brave invocation fails closed unless the request carries that exact
evidence hash.

Memory, citations, and runtime search results are enclosed in an explicit untrusted-reference boundary before model
use. Embedded instructions cannot change policy, grants, tools, or system text. Context evidence identities enter the
policy and budget snapshot hashes; the complete redacted system context is additionally content-addressed for
continuation and restart. The local model never receives a network client or credential: when all policy intersections
pass it receives only the `search_web` schema, and AgentForge performs the eventual approved request through the fixed
managed Brave adapter. Missing or changed search configuration/credentials returns typed failure; deterministic
providers and restart fixtures cover the approval and continuation boundary.

## R1 final disposition

The final review re-evaluated every trust boundary above against production composition rather than the milestone in
which it first appeared. The earlier High dependency advisory, missing strong-isolation adapter, unsigned production
plugin trust, and incomplete remote-mode controls are resolved. The restricted-host same-user replacement race remains
Medium and explicitly cannot satisfy requests that require filesystem, network, credential, privilege, or container
isolation. Same-user OS-account compromise and Docker-daemon compromise remain outside the R1 single-operator boundary.

The locked dependency audit, secret scan, architecture/security suites, critical-path coverage gate, release-default
validation, dual-platform runs, and equipped Docker workflow are release requirements. The detailed finding table and
release conditions are in `SECURITY_REVIEW_R1.md`. No High or Critical finding remains open under the documented local,
single-operator R1 posture.
