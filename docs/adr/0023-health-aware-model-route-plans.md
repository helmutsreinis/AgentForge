# ADR 0023: Health-Aware, Version-Bound Model Route Plans

## Status

Accepted on 2026-08-11.

## Context

Pure routing cannot prove that an agent policy, installation, provider profile, health
observation, or budget is still current. Treating a route selection as authorization could
allow a stale profile, model substitution, expired health record, repeated failed provider,
or concurrent configuration change to reach egress. The next boundary must produce useful
failover evidence without opening model invocation before audit, budget reservation, and run
snapshots exist.

## Decision

Add a scoped `IModelRoutePlanner`. It prepares and redacts context first, then reads the
installation, exact agent, and installation provider profiles through a serializable
`IModelRouteAuthoritySnapshotReader`. Planning requires exact caller versions, a `Ready`
installation, the request model from the agent's durable primary profile, and request limits
within the current agent budget.

Add an immutable `IModelProviderHealthSource` contract and a deterministic health catalog.
Health evidence is unique per exact profile, uses a fixed status/source and safe evidence
code, has bounded failure/retry fields, and expires within 15 minutes. Missing, future,
expired, unknown, or temporarily unavailable evidence excludes that profile. Attempt history
is limited to eight unique profiles and must still belong to the exact-model catalog.

The planner snapshots mutable inputs, validates catalog/profile identity and persisted
capability authority, routes the prepared request, then reads durable authority and health a
second time. Any authority change returns `ConcurrencyConflict`; any health/eligibility change
returns retryable `RecoverableExternalFailure`. A successful plan expires after at most five
seconds and binds request input, context-preparation policy, installation/agent/provider
versions, route selection, and health evidence into canonical SHA-256 evidence.

Production registers empty provider and health catalogs. Planning remains internal and
performs no provider resolution, credential materialization, HTTP call, audit write, budget
reservation, or public API/CLI mutation.

## Consequences

- A model-controlled request cannot choose a model outside the current agent primary-model
  policy, and failed profiles cannot be retried by deleting health evidence.
- Failover attempts can carry bounded exact profile history without duplicating request
  content or weakening locality/capability policy.
- Serializable reads plus a second version/evidence check narrow configuration races, but a
  plan is still not an authorization token.
- The invocation boundary must consume the plan immediately, atomically revalidate/reserve
  durable run state and budgets, append audit evidence, and resolve the exact current adapter.
- Destination/DNS policy and durable health observation remain future gates.
