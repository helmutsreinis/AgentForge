# AgentForge R1 upgrade and rollback

## Before an upgrade

1. Read the release notes and verify the archive SHA-256, SPDX SBOM, and GitHub build-provenance attestation.
2. Run `agentforge doctor` and stop if audit integrity, provider-secret materialization, or storage checks fail.
3. Create and verify an online package with `agentforge backup create --data-directory <current> --destination <empty-backup>` followed by `agentforge backup verify --data-directory <current> --backup-directory <backup>`.
4. Stop the host and retain an additional cold copy of the entire data directory. Keep the OS user/account with it:
   Windows DPAPI and Linux Secret Service references are account-scoped.
5. Record the installed commit/version and service definition. Never run a down migration against operator state.

SQLite migrations run forward during host startup inside the normal database initializer. The first PostgreSQL R1
schema is advisory-lock protected; later released schemas must add a separate forward migration and previous-release
fixture before shipping. Start the replacement binary on loopback and wait for `/health/live`; then run `doctor` and
the relevant acceptance smoke before returning the service to normal operation.

## Restore drill and rollback

Restore always targets a separate absent or empty directory. The command verifies every database, artifact, secret-
store file, and auxiliary state-file hash before copying:

```text
agentforge backup restore --data-directory <current> --backup-directory <backup> --target-data-directory <empty-restore>
```

For PostgreSQL, set a distinct target connection environment variable and add
`--postgres-target-environment <name>`. AgentForge refuses the configured source variable as a restore target.
Validate the restored copy with the old binary, `doctor`, audit verification, and a provider credential probe. Stop
both copies before atomically selecting the restored data directory. Preserve the failed upgraded directory for
forensics; do not merge individual SQLite/WAL, artifact, or secret files between generations.

R1 supports rollback by restoring the complete pre-upgrade package and backup. A schema downgrade is deliberately not
the rollback mechanism because destructive down migrations can discard provenance. If the old binary cannot read the
restored copy, the rollback evidence is invalid and the installation remains in recovery mode.
