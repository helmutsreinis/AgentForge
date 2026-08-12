# ADR 0049: Ready-state operator workspace

Status: Accepted
Date: 2026-08-12

## Context

First-run setup now reaches a real Ready installation, but the loopback page previously left Agents, Runs,
and Skills as disabled placeholders. The production API accepts the random local administrator bearer
credential, while that credential is intentionally stored behind an OS secret reference and must not be
copied into browser storage or rendered into the page.

## Decision

Add a separate short-lived Ready-state operator session. On loopback and exact same origin, the host
materializes the current user's administrator credential, validates it against the durable verifier, clears
the lease, and issues a random 30-minute HttpOnly SameSite=Strict cookie. JavaScript receives only an
independent CSRF token, installation ID, actor ID, and expiry. The session is memory-only, single-installation,
rate-limited, and rechecks exact Ready scope on every request.

The first workspace slice exposes only existing harness-owned boundaries:

- `IAgentIdentityRepository` for installation-scoped agent policy summaries.
- `ITaskOrchestrator` and latest-snapshot repository queries for durable planned run create/list/cancel.
- `ISkillRegistryService` and the registry repository for validated packaged-seed install/list.

Run creation pins exact agent version plus policy, budget, child, and skill-grant hashes. It does not claim a
node or invoke a model/tool. Seed installation does not activate or promote the skill.

## Consequences

The single local operator can test meaningful persisted behavior without handling a bearer secret. Cross-
origin, non-loopback, stale installation, missing-CSRF, and missing-idempotency requests fail closed. Existing
audit and transaction behavior stays inside orchestration and skill services. Automatic model execution,
agent editing, skill promotion, remote administration, messaging, and physical-control UI remain later
independently gated slices.
