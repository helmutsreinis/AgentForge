# Migration 0021: Durable serial captures

Creates `serial_captures` with exact installation, agent, and content-addressed artifact foreign keys; a unique
installation/idempotency binding; physical-device/time lookup; immutable request/stream hashes; and bounded capture JSON
metadata. Existing installations receive no fabricated device or capture records.

Before upgrade, stop AgentForge and back up the SQLite database together with WAL/SHM files and the complete artifact
directory. Apply through normal startup, confirm no pending model changes, create a deterministic fixture capture, restart,
and verify its repository record, audit chain, artifact hash, and replay totals.

The down migration deletes capture metadata but not necessarily content-addressed files. Do not use down migration as
operator recovery. Restore the complete pre-0021 database and artifact backup together. Never edit byte/drop totals or
artifact hashes to make corrupt evidence replayable.
