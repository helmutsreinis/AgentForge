# 0027 Ordered event outbox

Adds `OccurredAtUtcTicks` to the previously internal, unwritten outbox table and
indexes pending records by terminal state, UTC ordering key, and identity. The public
`IEventOutbox` contract begins with this schema, so no released producer can have
created a legacy row without the ordering key.

Upgrade is forward-only and automatic at startup after a verified backup. Rollback
uses the pre-upgrade package and its verified backup in a separate installation
directory; operators must not run a destructive down migration against production
state. Backup/restore and schema construction are exercised for SQLite and PostgreSQL.
