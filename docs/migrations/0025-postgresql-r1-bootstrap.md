# PostgreSQL R1 schema bootstrap

R1 is the first AgentForge release supporting PostgreSQL, so no prior PostgreSQL operator
schema exists to upgrade. Under a database advisory lock, startup creates the complete model,
enables `citext`, then inserts the immutable `r1-20260812` marker into
`agentforge_schema_versions`. Existing SQLite installations continue through the checked-in EF
migration chain and are not converted automatically.

Before bootstrap, create an empty database and take a provider-native snapshot if it is not
disposable. After startup, verify the marker, run setup/doctor and audit-chain checks, create and
read a content-addressed artifact, then run an online `pg_dump` package and restore it into a
separate empty database. Do not point the live restore gate at an operator database.

Rollback does not drop tables or the extension. Stop AgentForge, retain diagnostics, and restore
the pre-bootstrap database snapshot. Never edit the schema marker or attempt to make a partially
created schema appear current. Future PostgreSQL schema changes must start from this exact marker
with forward and restore evidence.
