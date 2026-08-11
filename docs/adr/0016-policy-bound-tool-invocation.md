# ADR 0016: Policy-Bound Tool Invocation

## Status

Accepted on 2026-08-11.

## Context

An authoritative descriptor and a restricted process adapter are independently useful,
but connecting them incorrectly would create an authority-confusion boundary. A caller
must not submit its own capability, risk, target kind, executable, argument template,
environment, timeout, output limit, network policy, or sandbox requirements. Tool ID and
version alone are also insufficient because administrator configuration could replace a
descriptor under the same identity.

Approval consumption and process start cannot be one database transaction. The system
therefore needs a durable fail-closed handoff that prevents duplicate effects after an
interruption and proves authorization was committed before a sandbox receives the request.

## Decision

`IToolInvocationService` accepts exact catalog identity, typed parameter values, agent and
installation versions, workspace, correlation, and idempotency only. It resolves the exact
descriptor and derives every security and process field from that immutable record. The
normalized descriptor SHA-256 is part of the authorization request, durable approval,
invocation record, and audit evidence.

Parameter values must exactly match the descriptor schema. Required values cannot be
omitted, unknown values are rejected, types and bounds are revalidated, and descriptor-
owned bindings render an argument array without a shell. The descriptor target is derived
from its required target parameter. Credential-shaped direct metadata or values are
rejected; later secret use must materialize from references. Agent network posture is intersected with descriptor
network policy before authorization; a weaker sandbox request is never substituted.

The service loads the current installation and agent policy, evaluates missing-policy-
denies rules, and requires an active exact approval where configured. In one EF transaction
it consumes the single-use approval, creates an `Authorized` invocation row with a unique
installation-scoped idempotency key, and appends redacted authorization audit evidence.
Only after that commit and a second policy/version read does it call `ISandbox`.

Completion updates the invocation and appends completion audit in a second transaction.
Durable evidence stores exit code, typed failure, output lengths, and SHA-256 hashes but no
raw process output. Raw bounded output is returned only to the immediate in-process caller.

An exact retry of a terminal record returns its durable status without executing again and
without reconstructing raw output. Changed request or correlation under the same key fails
with `ConcurrencyConflict`. An `Authorized` record has uncertain completion after a crash
and is never automatically replayed. A consumed approval is never restored on start,
policy-revalidation, execution, or completion failure.

Migrations add descriptor hashes as nullable on legacy approvals, then create the durable
invocation table. A legacy tool approval with no descriptor hash remains readable but can
never match a current descriptor-bound request.

## Consequences

- Model or caller input cannot understate tool authority or change the execution template.
- Audit authorization is durable before any sandbox call; uncertain work fails closed.
- Single-use approvals and idempotency prevent silent duplicate process starts.
- Completion output is privacy-minimized, but exact retries intentionally cannot reproduce
  the original raw bytes.
- This service is registered but has no public CLI/API invocation command. An empty default
  catalog and unsupported isolation keep production invocation closed until tools are
  explicitly composed and their sandbox requirements are available.
- Restricted host cannot satisfy denied or loopback network posture and is not silently
  selected for such descriptors. Container/namespace execution remains an open live gate.
