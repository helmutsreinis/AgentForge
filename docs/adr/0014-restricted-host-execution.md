# ADR 0014: Restricted-host execution is an honest capability boundary

Status: Accepted

## Context

Milestone 2 needs a portable execution primitive before tool discovery can safely add
optional version probes or invocation. Windows and Linux expose different containment
mechanisms, and the current workstation has no container runtime. Treating a direct host
process as equivalent to a container would silently weaken requested security.

## Decision

AgentForge owns typed execution requests, results, output events, and capability records
in Domain and exposes them through `ISandbox` in Abstractions. `AgentForge.Tools`
implements the first `RestrictedHost` adapter with:

- fully qualified existing executables with no link/reparse traversal;
- `ProcessStartInfo.ArgumentList`, `UseShellExecute = false`, and closed standard input;
- existing non-link working directories contained within an existing workspace;
- a cleared environment rebuilt only from bounded configured allowlists;
- one ordered combined output budget, wall-clock timeout, and caller cancellation;
- process-tree termination and Windows Job Object kill-on-close; and
- exact capability reporting with typed failure for every unavailable required feature.

No public endpoint invokes this interface. The future tool application service must use
an immutable authoritative descriptor, construct the current authorization context,
re-evaluate policy, consume matching approval evidence, and append audit state before
calling the sandbox.

## Consequences

The adapter can support low-risk, explicitly host-networked tools after the authorization
slice passes. It cannot satisfy container, filesystem, denied/loopback network, CPU,
memory, process-count, credential, or privilege isolation, and must return
`UnsupportedCapability` for those requests.

Path validation and process-tree attachment/discovery retain OS-level timing windows, so
restricted-host capability is not sufficient for untrusted or high-risk binaries. The
later container/namespace adapter remains mandatory, and Docker absence keeps that live
gate open without weakening deterministic tests.
