# ADR 0018: Provider-Neutral Model Contracts

## Status

Accepted on 2026-08-11.

## Context

AgentForge must support hosted and local model providers without allowing an SDK to own
policy, durable state, audit, tool authority, or run behavior. Text-only chat records are
insufficient: the runtime also needs streaming, structured output, media references, tool
calls/results, usage/cost, errors, cancellation, capability evidence, and stable input
provenance. Deterministic tests must exercise the same public contracts before credentials
or live services are introduced.

## Decision

Domain owns provider-neutral immutable records for typed message content, artifact-backed
attachments, response formats and schemas, tool contracts, request limits, capability
evidence, usage, safe errors, and sequenced stream events. Abstractions owns only
`IModelProvider` and an exact `ProviderProfileId` catalog. Vendor SDKs and
`Microsoft.Extensions.AI` may exist only inside later adapter projects.

The Models feature validates and snapshots complete requests before asynchronous work.
All JSON is bounded, depth-limited, duplicate-key-rejecting, and normalized. Attachment
references include hash, media type, length, and modality in the input fingerprint.
Provider capabilities separate evidence source from availability status and expiry; an
unavailable, unknown, temporarily failing, future, expired, or opposed record fails closed.
Started events carry separate SHA-256 fingerprints for normalized request input and sorted
capability evidence.

The first implementation is a deterministic scripted provider. It snapshots script input,
checks capabilities and request budgets, produces strictly ordered typed events, honors
cancellation, and ends on failure without a completion event. Its exact-profile catalog
snapshots descriptors and rejects duplicate IDs. Default production composition uses an
empty catalog and exposes no model call route.

## Consequences

- Provider adapters translate at one boundary and cannot leak SDK types into durable or
  policy code.
- Media cannot be silently discarded without changing the normalized input hash.
- Deterministic fixtures cover streaming behavior without credentials or network access.
- Scripts are trusted fixtures; later adapters must independently sanitize raw errors and
  redact model context before crossing an external trust boundary.
- Routing, failover, live OpenAI-compatible/hosted adapters, secret materialization,
  persistence, structured-output repair, and agent loops remain later Milestone 3 slices.
