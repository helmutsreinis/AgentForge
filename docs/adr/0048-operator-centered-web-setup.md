# ADR 0048: Operator-centered web setup and model discovery

Status: Accepted — 2026-08-12

## Context

The original loopback wizard exposed its one-time setup nonce as a required form field and
accepted a free-text model identifier. That made an internal security handshake look like
operator configuration, made a consumed session difficult to recover, and ignored the model
catalog already exposed by compatible endpoints.

## Decision

Keep the random one-time setup grant server-side. A same-origin loopback request atomically
creates the 30-minute HttpOnly SameSite session and returns only the independent CSRF token to
the page. The existing cookie resumes the same session after refresh; concurrent browsers are
denied and Ready state cannot be reopened. Every mutation retains exact operation/request-hash
idempotency and serialized session execution.

Add `IModelCatalogDiscoveryService` as the harness boundary for setup-time catalog discovery and
verification. The Models implementation allows only installed compatible provider types, derives
`/models` and `/chat/completions` from one bounded base URL, reuses destination-policy socket
controls, disables redirects/cookies/proxies, applies strict time/body/JSON/model-count bounds,
and never returns remote bodies or credentials as failure evidence. The selected identifier normally
comes from the current discovered catalog. Servers without a catalog route have a visibly manual
exact-ID fallback; that entry is kept in the same session and remains unverified until the identical
bounded chat probe succeeds.

Represent deliberate no-auth configuration with `SecretReference.NoCredential`, not a fabricated
key. It is valid only for loopback/private vLLM or generic OpenAI-compatible profiles. Setup
validation, minimum-viability completion, and hosted adapter creation each recheck the restriction;
the runtime omits Authorization for that exact typed sentinel. Public endpoints still require
HTTPS and an OS-backed credential reference.

## Consequences

- Operators see Connect, Choose, Verify, Agent, and Review rather than nonce/cookie mechanics.
- Refresh recovery does not weaken origin, CSRF, idempotency, or post-completion lockout.
- Model discovery performs no model inference; verification performs one explicit bounded probe.
- Manual model entry cannot bypass selection or verification.
- Local servers that intentionally require no authentication persist no fake credential.
- There is no schema migration because the existing secret-store/key columns can store the typed
  sentinel values.
