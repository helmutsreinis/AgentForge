# ADR 0028: Durable typed agent loop snapshots

Status: Accepted

## Context

Provider runs alone do not define an agent. A bounded agent must expose explicit observe, plan, act,
verify, reflect, and persist phases, survive process loss without guessing completed work, and stop
on exhausted authority, malformed structured output, cancellation, or repeated lack of progress.
Persisting model prose as orchestration state would widen the sensitive-data boundary and make
recovery dependent on provider-specific formats.

## Decision

Introduce `AgentForge.Runtime` as an independent feature module depending only on Domain and
Abstractions. The domain owns a pure six-phase state machine. Every transition creates an immutable
snapshot with stable loop/authority identity, total budgets and consumption, turn and repair counts,
normalized evidence hashes, correlation, the previous snapshot hash, and its own canonical SHA-256.

SQLite stores snapshots append-only under `(LoopId, Sequence)`. A unique installation/idempotency/
sequence index makes concurrent creation deterministic. The service resumes only when the complete
request authority and initial-state hash match, appends each snapshot and redacted audit event in one
unit of work, and treats a terminal replay as read-only.

Structured-output rejection repeats the current phase only within an explicit repair allowance.
Persist is the only phase that may advance a turn and must supply normalized progress evidence.
Repeated evidence produces typed `NoProgress`; turn, token, tool, or wall exhaustion produces typed
`BudgetExceeded`. A completion request still passes through Reflect and Persist before completion.

No governed step executor is composed by default. The host therefore contains the durable loop
service but cannot start autonomous work or provider egress through it.

## Consequences

Recovery can prove exactly which phase was durably accepted and does not repeat earlier transitions.
Audit and snapshot records retain hashes and counts, not prompts, completions, tool output, or
credentials. Later orchestration can pin task nodes to exact loop snapshots.

The current service is a single-process transition runner. Lease-based task ownership, automatic
crash scanning, DAG nodes, tool-result artifact binding, and a governed model/tool step executor are
Milestone 4 and later concerns. Snapshot down-migration is destructive and must not be used as an
operational rollback.
