# ADR 0005: Risk-based plugin process boundary

Status: Accepted

Signed, trusted, low-risk adapters may load in-process behind Plugin SDK contracts.
Untrusted, high-risk, or independently scalable plugins run out-of-process through a
versioned constrained protocol. Plugin discovery is not authorization, and no plugin
may increase its declared capability or access raw host secrets.

R1 packages contain an exact-schema `plugin.harness.json` and one hash-pinned assembly in a link-free package
directory. Catalog discovery is read-only and rejects unknown/duplicate fields, duplicate identities, path escape,
links, excessive sizes, invalid identifiers, and changed hashes. Trust is the verifier's result rather than a
manifest flag. A verified low-risk adapter may instantiate its exact SDK entry type in a collectible load context;
identity is checked again after construction and the assembly hash is re-read immediately before load.

Every other package is encoded into protocol version 1 and submitted to `agentforge-plugin-worker` only through
an `ISandbox` that proves container, denied-network, filesystem, CPU, memory, process-count, output, timeout, and
process-tree controls. The worker rechecks request bounds, assembly hash, SDK type, and identity, returns only a
bounded receipt, and exits. The default restricted-host sandbox cannot meet that request, so unavailable container
isolation produces `UnsupportedCapability` and never falls back in-process.
