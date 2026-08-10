# ADR 0003: Append-only redacted audit and trajectory records

Status: Accepted

Audit is written through typed events containing sequence, correlation/causation,
actor, authorization, versions, hashes, outcome, and immutable artifact references.
Redaction occurs before persistence. Critical streams use chained hashes. Audit is
not used as mutable application state, and model text cannot suppress an event.
