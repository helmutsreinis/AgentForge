# ADR 0038: Governed research and citation evidence

Status: Accepted — 2026-08-12

## Decision

AgentForge owns normalized search contracts and immutable citation evidence. Providers return bounded
candidate hits only. The research service canonicalizes URLs, deduplicates, applies reciprocal-rank
fusion, bounds excerpts, records provider failures, and caches only the exact query/scope/provider hash.

Brave and official Google Custom Search adapters use exact HTTPS endpoints, no redirect/proxy/cookie
state, bounded JSON, and invocation-scoped OS-secret materialization. Deterministic/local providers use
the same contract. Search results carry no policy authority.

## Consequences

Throttled or unavailable sources can coexist with surviving citations. Total outage is a typed retryable
failure. Provider credentials and remote error bodies are absent from results, cache identity, logs, and
durable evidence. Production starts with an empty provider set.
