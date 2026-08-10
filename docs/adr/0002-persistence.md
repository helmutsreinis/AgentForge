# ADR 0002: Repository-owned SQLite with PostgreSQL parity

Status: Accepted

EF Core SQLite in WAL mode is the zero-dependency store. Explicit transactions,
numeric aggregate versions, leases, unique idempotency constraints, and inbox/outbox
records implement durability. Large immutable payloads use a content-addressed
artifact store. PostgreSQL later implements the same repository contracts; provider
code never leaks into Domain.
