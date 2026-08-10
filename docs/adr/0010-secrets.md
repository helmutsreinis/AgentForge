# ADR 0010: Secret references and invocation-scoped resolution

Status: Accepted

Normal configuration contains secret URIs, never plaintext credentials. OS-backed
stores are preferred for local use; external vault and headless adapters implement
the same interface. Resolution requires an authorized invocation scope, values are
injected only into the target operation, and redaction runs before model context,
logs, state, audit, reports, and exports.
