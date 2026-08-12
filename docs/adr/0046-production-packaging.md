# ADR 0046: Self-contained single-operator production packaging

Status: Accepted

R1 publishes self-contained `win-x64` and `linux-x64` host, CLI, and constrained-worker components. A repository-owned
cross-platform release tool produces deterministic archives, a complete SHA-256 manifest, and SPDX 2.3 file/dependency
evidence. Release CI smokes the actual binaries on their target OS and attests archives plus the non-root container
image. Signing is represented by GitHub's keyless OIDC build-provenance attestation; no long-lived signing key is
stored in AgentForge or CI.

Service identity follows the local single operator because R1 secret storage is user-scoped. Windows prompts for a
secure service credential matching the installing identity; Linux installs a hardened systemd user service. Running
under LocalSystem, root, or a detached service user would make DPAPI/Secret Service recovery inconsistent and is denied
by the supplied installers. All packages remain loopback-only by default. The container cannot silently replace a
missing OS secret facility with plaintext configuration.

Backup packages cover the online database, artifacts, protected secret files, and auxiliary installation state. Restore
is hash-gated into a separate empty target. Forward migration plus full-package restore is the upgrade/rollback model;
destructive down migration is not an operator rollback mechanism.
