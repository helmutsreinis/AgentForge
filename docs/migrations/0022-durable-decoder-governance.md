# Migration 0022: Durable decoder governance

Creates append-only `decoder_proposal_snapshots` keyed by proposal/version and `decoder_active_versions` keyed by
installation/decoder ID. Proposal rows bind candidate, optional baseline, state, previous/current snapshot hashes, timestamp,
and complete immutable JSON. Active pointers carry a numeric concurrency version. Both tables restrict-delete against the
installation; no proposal or active decoder is fabricated for existing installations.

Before upgrade, stop AgentForge and back up SQLite with WAL/SHM and the artifact directory. Apply through normal startup,
confirm no pending model changes, execute a deterministic propose/evaluate/approve/canary/promote/restart/rollback fixture,
and verify proposal chain plus audit integrity.

The down migration destroys governance and active-selection evidence. Restore the complete pre-0022 backup instead. Never
edit a baseline, candidate, snapshot hash, or active pointer to force promotion or rollback.
