# ADR 0002: Repository-owned SQLite with PostgreSQL parity

Status: Accepted

EF Core SQLite in WAL mode is the zero-dependency store. Explicit transactions,
numeric aggregate versions, leases, unique idempotency constraints, and inbox/outbox
records implement durability. Large immutable payloads use a content-addressed
artifact store. PostgreSQL implements the same repository contracts; provider code
never leaks into Domain.

R1 selects `Sqlite` or `PostgreSql` in persistence options. PostgreSQL connection
material is accepted only through the configured process environment variable and is
never written to AgentForge configuration, logs, audit, or backup metadata. The first
PostgreSQL release creates the complete current schema under an advisory lock, records
`r1-20260812`, enables `citext`, and uses provider-specific uniqueness translation.
Future PostgreSQL releases must add forward version transitions from that marker.

`IDatabaseBackupService` packages the relational store with content-addressed artifacts
and a canonical SHA-256 manifest. SQLite uses its online backup API. PostgreSQL invokes
only exact absolute `pg_dump`/`pg_restore` paths without a shell, places the password in
the one process environment, bounds output/time, and requires a distinct target secret
for restore. Restore is allowed only into a separate empty artifact directory.
