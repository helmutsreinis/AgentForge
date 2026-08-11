# ADR 0017: Safe Availability Probes

## Status

Accepted on 2026-08-11.

## Context

Passive inventory must not execute unknown PATH candidates, but operators eventually need
bounded evidence that an explicitly trusted tool responds. Treating any catalog entry as
probeable, invoking it during discovery, or accepting model-authored version arguments
would collapse inventory, authorization, and execution boundaries. Version output can
also contain malformed data or credential material and cannot be durably reconstructed on
an idempotent retry.

## Decision

The catalog carries an explicit `Invocation` or `AvailabilityProbe` operation kind.
Availability probes are admitted only with capability `tool:availability.probe`, risk
`Inventory`, no target, parameters, side effects, inherited environment, or network, and
non-sensitive output classification. Their descriptor owns at least one fixed/literal
version/help argument, a maximum 30-second timeout and 64 KiB output, a container sandbox,
and required network-isolation evidence.

`IToolAvailabilityProbeService` accepts exact tool/version, current installation and agent
versions, actor, workspace, correlation, and idempotency. It passes an empty typed parameter
set through `IToolInvocationService`; current policy, exact single-use approval, durable
authorization/audit, sandbox execution, terminal evidence, and retry semantics are
therefore identical to other tool calls.

Availability is true only for a successful terminal invocation. The non-replay caller may
receive the first nonempty printable strict-UTF-8 output line. The complete line is checked
by the structured redactor before truncation to 512 characters. Invalid encoding,
credential-shaped output, and idempotent replay expose no observed summary. Raw probe bytes
are never persisted.

## Consequences

- Inventory, catalog admission, search, and description remain execution-free.
- Probe authority cannot be reused for a target, parameterized call, network access, or
  another tool capability.
- A deterministic container-capable fake can verify application behavior without claiming
  live OS isolation.
- Production composition remains closed: no public probe route exists, the default catalog
  is empty, and restricted host cannot satisfy denied networking.
- A live container/namespace adapter and executable/image integrity evidence remain open
  gates before probe descriptors are enabled in production.
