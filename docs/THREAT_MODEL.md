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
text capability. `LocalOnly` requires a loopback endpoint and forbids fallback.
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
