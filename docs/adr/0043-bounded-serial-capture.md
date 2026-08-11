# ADR 0043: Exact serial sessions and bounded immutable capture

Status: Accepted

## Decision

Resolve serial I/O only through an approved platform transport catalog. Keep that catalog empty in production until a
hardware gate explicitly installs an adapter. Every capture, read, or write revalidates an exact expiring grant for the
same physical device and operation. A partial write is not success.

Encode captures as a versioned, deterministic little-endian artifact containing the physical-device binding and ordered
frames with offset ticks, dropped-byte count, disconnect marker, and raw bytes. Bound request duration, total bytes, frame
count, and individual frame size. Persist only immutable metadata and hashes in SQLite, audit terminal counts/hashes, and
validate artifact length, structure, binding, totals, ordering, and stream hash before replay.

## Consequences

Deterministic fakes provide complete CI evidence without hardware. Raw capture evidence is intentionally part of backup
and retention scope but does not enter relational JSON or logs. A truncated capture is successful only with an explicit
truncation marker. Corrupt evidence cannot be partially replayed.
