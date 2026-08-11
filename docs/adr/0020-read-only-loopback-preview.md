# ADR 0020: Read-Only Loopback Control-Plane Preview

## Status

Accepted on 2026-08-11.

## Context

Operators need an early browser-visible first-run surface while AgentForge is still before
the Milestone 7 authenticated setup-wizard gate. Reimplementing setup in JavaScript or
accepting credentials now would introduce sessions, CSRF, nonce, idempotency, rate-limit,
audit, and secret-handling obligations before those controls exist.

## Decision

Serve a local static preview at the host root. It reads only the existing same-origin
liveness, readiness, setup-status, and sandbox-capability GET endpoints. It contains no
form or password field and does not create a web session, cookie, mutation endpoint, model
invocation, or new application-service path. The exact CLI command directs the operator to
the already-gated shared setup service.

The page uses local HTML, CSS, and JavaScript. Dynamic API values are assigned with
`textContent`. Host middleware sends a same-origin content security policy, denies framing,
MIME sniffing, forms, referrers, and ambient device APIs, and disables static-asset caching
for this development preview. The host remains loopback-bound by default.

## Consequences

- A clean installation has a usable visual status surface without gaining authority.
- UI styling and responsive/accessibility foundations can be reviewed early.
- This preview is not the web setup wizard and cannot claim CLI/web equivalence.
- Milestone 7 must replace or extend it only through the existing setup application
  services after nonce/session/CSRF/origin/rate-limit/idempotency/audit controls pass.
- Remote exposure remains disabled by default and is not made safe by these browser headers.
