# ADR 0012: Passive environment inventory

Status: Accepted

## Context

AgentForge needs enough host evidence to route later tools and sandboxes, but discovery
itself crosses untrusted filesystem and operating-system metadata. Running a candidate
to identify it would turn inventory into execution before policy and isolation exist.

## Decision

Environment profiles are immutable records with a canonical SHA-256 fingerprint.
The system profiler uses bounded runtime, registry/token, filesystem, proc/sysfs,
distribution, service-marker, and PATH directory metadata. It never uses a process-
start primitive, recursively searches arbitrary trees, follows a candidate through
execution, or grants authority from presence.

Executable entries retain path, size, modification time, link target, provenance,
and conservative trust. Windows UNC PATH entries are skipped. Linux executable bits
are read as metadata. Kali identity is derived only from exact normalized
`/etc/os-release` ID metadata. Collections are bounded, normalized, deduplicated, and
sorted before fingerprinting; request actor, correlation, and observation time are
excluded from the fingerprint.

The application service redacts the complete capture before content-addressed storage
and atomically records its hash, fingerprint, counts, and truncation in the audit
journal. The CLI hides executable details unless the operator opts in.

## Consequences

Inventory can safely precede setup and restricted execution. A fingerprint represents
the exact normalized evidence and can change when host files or metadata change; it is
not a permanent machine identity. Missing, inaccessible, or truncated evidence stays
explicit and cannot be promoted to a capability. Version/help probes, recursive search,
network inspection, and invocation require later policy and executor gates.
