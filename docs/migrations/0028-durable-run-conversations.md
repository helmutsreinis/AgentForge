# Migration 0028: Durable run conversations

Migration file: `20260816165307_DurableRunConversations.cs`

Migration 0028 adds append-only `run_conversation_snapshots`. Each snapshot is installation-, agent-, and
provider-bound, carries a canonical previous/current hash chain, and stores only bounded metadata plus JSON references
to content-addressed redacted text artifacts. Existing orchestration tasks, model runs, and legacy Ready receipts are
unchanged; no conversation or content is fabricated for prior rows.

## Upgrade and verification

Stop the host and back up the entire AgentForge data directory, including the SQLite database, WAL/SHM files, OS secret
references, and artifact directory. Start the new host and allow EF Core to apply the migration. Verify that the table,
installation/agent/provider foreign keys, unique installation/idempotency/version index, and latest-agent index exist;
run a two-turn conversation, restart the host, open its details, and verify all artifact lengths and SHA-256 hashes.
The migration-drift test must report no pending model change.

## Rollback and recovery

The down migration drops only `run_conversation_snapshots`; it does not delete shared content-addressed artifacts or
legacy task receipts. Before rollback, export or otherwise preserve any conversation history that must remain
available. A code rollback leaves unreferenced immutable text artifacts that may be removed only by a future verified
artifact garbage collector—do not delete hashes manually. Restore the stopped full-data backup if migration or
integrity verification fails.
