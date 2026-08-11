# ADR 0029: Provider destination policy is enforced at socket connect

Status: Accepted

## Context

Validating an HTTPS URI or resolving its hostname during profile creation does not prevent DNS
rebinding. The address can change between validation and connection, a mixed DNS answer can include
an internal target, and the platform HTTP handler may resolve the name again without applying the
agent's declared data-location policy.

## Decision

Production OpenAI-compatible transports use a `SocketsHttpHandler.ConnectCallback`. The callback
requires the exact approved host and port, resolves at most 64 addresses, normalizes IPv4-mapped
addresses, rejects the entire answer when any address violates the declared Loopback,
PrivateNetwork, or Cloud class, and connects directly to one approved IP. TLS still authenticates
the original hostname. Redirects, cookies, proxies, decompression, and ambient credentials remain
disabled.

Loopback permits loopback addresses only. PrivateNetwork permits RFC1918 IPv4 and unique-local IPv6
only. Cloud permits only globally routable addresses and rejects unspecified, loopback, private,
carrier-grade NAT, link-local, multicast, benchmark, and documentation ranges. InProcess cannot
create an HTTP transport. Literal endpoints are classified without DNS; callers may explicitly pin
a location, but a hosted profile must match policy-approved routing evidence exactly.

Testing adapters may supply a deterministic message handler and exercise the pure address policy
separately. Production construction cannot replace the policy-bound socket handler.

## Consequences

DNS is checked for each new pooled connection and the chosen IP is the one actually connected, so
there is no resolve/check/resolve gap. Existing approved pooled connections may live for at most
five minutes. A mixed answer fails closed instead of trying only its apparently safe members.

This is address-class enforcement, not a general network sandbox. Firewall policy, certificate
validation, egress allowlists, proxy mediation, and container network isolation remain independent
layers.
