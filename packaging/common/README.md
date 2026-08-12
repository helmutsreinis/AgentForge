# AgentForge R1 package

This package contains the self-contained AgentForge host, `agentforge` CLI, and isolated plugin worker. It does not
require a machine-wide .NET installation. Verify `SHA256SUMS` from the release beside this package and verify the
GitHub build-provenance attestation before installation.

AgentForge binds `127.0.0.1:5047` by default. Complete setup through the local web wizard or CLI before enabling
normal operation. Never expose the listener remotely without explicit HTTPS, authentication, exact origins, network
policy, and audit configuration. The default plugin catalog is closed and high-risk plugins require container
isolation.

Back up the complete data directory, artifacts, and OS secret-store account together before upgrades. See the
repository `docs/RUNBOOK.md` and `docs/UPGRADE.md` for recovery and migration procedures.
