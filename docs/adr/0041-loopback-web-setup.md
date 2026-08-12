# ADR 0041: Loopback web setup sessions

Status: Accepted — amended by ADR 0048 on 2026-08-12

## Decision

The first-run web wizard is a loopback-only adapter over `ISetupApplicationService`. A random one-time nonce
creates a short-lived HttpOnly SameSite session and independent CSRF token. Every setup mutation additionally
requires an operation-scoped idempotency key bound to its normalized request hash. Successful exact retries
return cached responses; conflicts fail. Completion makes the session exact-replay-only.

Provider metadata and the credential body use separate steps. Credentials arrive as bounded strict UTF-8
text, are decoded into a clearable character buffer, passed once to the shared credential service, and cleared.
Agent fields use the CLI's conservative omitted-option defaults and always pass shared preview before create.

## Consequences

The wizard cannot be used from a remote binding and has no general administration capability. The same setup
validation, persistence, redaction, audit, and rollback behavior serves CLI and web. Sensitive setup responses
are never cached. Browser-owned credential input is cleared immediately after staging.
