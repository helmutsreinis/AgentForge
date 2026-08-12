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

Production trust uses configured ECDSA P-256 public keys and algorithm identity `ECDSA-P256-SHA256`. The signature
covers the canonical UTF-8 manifest fields produced by `PluginManifestValidator.CreateSigningPayload`; the signature
object itself is excluded. Missing keys, unknown key IDs or algorithms, malformed keys or signatures, changed fields,
and non-P-256 keys remain untrusted. Public keys are configuration, not secrets, and an empty catalog trusts nothing.

Every other package is encoded into protocol version 1 and submitted to `agentforge-plugin-worker` only through
an `ISandbox` that proves container, denied-network, filesystem, CPU, memory, process-count, output, timeout, and
process-tree controls. The worker rechecks request bounds, assembly hash, SDK type, and identity, returns only a
bounded receipt, and exits. The default restricted-host sandbox cannot meet that request. The opt-in digest-pinned
Docker adapter may meet it; otherwise isolation produces `UnsupportedCapability` and never falls back in-process.
