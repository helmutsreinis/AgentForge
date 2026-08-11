# ADR 0013: Capability policy and exact approvals

Status: Accepted

## Context

Model output and discovered tool metadata are untrusted proposals. A human-readable
approval that binds only a tool name can be replayed with different arguments, target,
workspace, agent, or tool version. Conversely, letting missing policy inherit ambient
authority makes future integrations unsafe by default.

## Decision

AgentForge constructs a canonical `AuthorizationContext` in deterministic application
code. Its request hash binds installation/version, agent and exact agent-policy version, request
actor, normalized capability and risk, tool and version, canonical JSON parameter hash,
target kind/hash, and workspace hash. Correlation remains provenance; the approval
preview separately binds its correlation, authenticated approver, disposition, expiry,
current policy fingerprint, and exact request hash.

The policy evaluator accepts only one exact same-scope rule and returns `Deny` for
missing, ambiguous, cross-installation, cross-agent, or stale-version inputs. Parent and
child policies intersect over the union of keys: omission denies and the remaining rule
uses the most restrictive decision. Approval records can influence only
`RequireApproval`; an active, unexpired grant allows the exact request, an exact denial
denies it, and changed data requires a new decision.

Approval preview/apply requires the current local-administrator credential. SQLite stores
the bound hashes and normalized identifiers, never raw parameters, targets, or workspace.
An installation-scoped unique idempotency key returns the prior record only for an exact
authenticated retry. Audit receives hash-only metadata through the structured redactor in
the same transaction. CLI request parameters are read from bounded redirected stdin so
they do not enter shell history or the process argument list.

## Consequences

Approval evidence is durable, auditable, portable across SQLite restarts, and cannot be
retargeted. An agent profile version change invalidates old decisions by construction.
Canonicalization intentionally treats different numeric JSON spellings as different
requests; callers should reuse preview inputs exactly.

This decision does not enable tool execution. The next restricted-executor slice must
derive capability/risk/tool identity from its immutable descriptor, enforce containment
and resource policy, re-evaluate current policy, and atomically consume an exact grant.
Unsupported isolation fails typed and inventory never grants authority.
