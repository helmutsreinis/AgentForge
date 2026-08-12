# ADR 0047: Digest-pinned Docker sandbox

Status: Accepted

## Context

Restricted-host execution cannot enforce filesystem, network, CPU, memory, or process
isolation. Plugin workers and non-review coding verifiers require those controls and
must fail typed when the equipped runtime is absent.

## Decision

Compose a sandbox selector with restricted-host and Docker adapters. Docker is disabled
until an operator supplies an exact absolute runtime path and an image SHA-256 identity.
The adapter accepts only denied networking, an empty invocation environment, a validated
link-free workspace, an allowlisted executable mapping, and bounded arguments. It uses
an argument array to apply a non-root identity, read-only root, dropped Linux capabilities,
`no-new-privileges`, a single read-only or read/write workspace mount, isolated temporary
storage, and CPU, memory, PID, output, and time limits. A random one-shot container name
is force-removed after every outcome, including cancellation. Runtime or isolation gaps
return `UnsupportedCapability`; selection never falls back to host execution.

The sandbox image contains the constrained plugin worker and .NET SDK tooling. Release CI
builds it and runs the equipped adapter test. Local installations without Docker retain a
fully deterministic adapter test and report typed unavailability.

## Consequences

The Docker daemon is a trusted local administrator boundary. Container image construction,
digest distribution, daemon policy, and host kernel patching remain operator/release duties.
Invocation environment variables are deliberately unsupported because common Docker CLI
forms would expose values through the host process argument list.
