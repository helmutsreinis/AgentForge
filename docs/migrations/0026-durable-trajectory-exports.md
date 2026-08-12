# Migration 0026 — Durable trajectory exports

Migration `20260812011148_DurableTrajectoryExports` adds `trajectory_exports`. Each row binds one
installation-scoped idempotency key and request hash to the exact content-addressed trajectory
artifact and serialized receipt. Foreign keys restrict deletion of the installation or artifact.
Existing installations receive no fabricated export row.

Before upgrading, create and verify a database-plus-artifact backup. After migration, export a
bounded trajectory, verify its content hash and audit head, replay the exact request across restart,
and confirm a changed request conflicts. The generated down migration drops durable export and
idempotency evidence; do not apply it to operator state. Restore the complete pre-0026 backup instead.
