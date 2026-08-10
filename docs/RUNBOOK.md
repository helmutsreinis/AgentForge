# Development Runbook

## Session start

1. Read `PROJECT_STATE.md`, `REQUIREMENTS.md`, `TRACEABILITY.md`, open threat findings, and the latest gate report.
2. Inspect `git status --short --branch` and never overwrite unrelated changes.
3. Verify `dotnet --info`; check WSL/container availability when the slice needs it.
4. Resume the highest-priority unblocked requirement and record its exit gate.

## Standard verification

```text
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
dotnet format --verify-no-changes --no-restore
```

For Linux verification from this Windows workstation:

```text
wsl.exe -d Ubuntu-24.04 -- bash -lc "cd '<repository mounted path>' && dotnet --info && dotnet restore --locked-mode && dotnet build --no-restore --configuration Release && dotnet test --no-build --configuration Release"
```

Run `scripts/verify-linux-smoke.sh` after the Linux Release build. Set `DOTNET_BIN`
when using a repository-local SDK.

Run `pwsh -File scripts/verify-windows-smoke.ps1` after the Windows Release build.
Both smoke scripts use validated temporary data directories and disable SQLite
connection pooling only for deterministic cleanup.

Windows and WSL builds share this checkout and therefore share generated `obj`
assets. After switching operating systems, run that platform's locked restore before
build, format, package inspection, or design-time tooling; package-cache paths are
platform-specific even when lock files are identical.

## Local smoke

Start `dotnet run --project src/AgentForge.Host`. Confirm `/health/live` is 200,
`/health/ready` is 503 on a clean installation, `/api/v1/setup/status` is available,
and `/api/v1/runtime/ping` is 503. The CLI returns exit code 2 for setup-required.

Begin a deterministic offline setup transaction with:

```text
agentforge setup begin --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id> --installation-id <guid>
```

Use `agentforge setup begin --interactive` for prompts backed by the same service.
Success returns one JSON object and exit code 0. Validation/state failures return
JSON and exit code 1; a retryable concurrency conflict returns exit code 3; Ctrl+C
returns 130. The command currently stops at `Configuring` by design.

## Secret-store diagnostics

On Windows, AgentForge stores provider credentials as current-user DPAPI-protected
files under the configured data directory's `secrets` folder. Copying these files to
another user does not make them decryptable. On Linux, install `secret-tool` and run
AgentForge inside a working Secret Service/DBus session. `agentforge doctor` reports
the store as unavailable when this facility is absent; it never falls back to
plaintext. Never place provider secrets in CLI arguments, configuration, SQLite,
migration fixtures, logs, audit, exports, or gate reports.

Backups preserve provider database rows and OS secret references together. Restoring
only SQLite can leave valid-looking but non-materializable references; validate every
reference before proceeding beyond setup.

## Agent policy preview and creation

After a provider profile has been validated, preview a conservative named-agent
definition without writing state:

```text
agentforge setup agent preview --data-directory <absolute-path> --name <name> --provider-id <guid> --actor <actor-id> --correlation <correlation-id>
```

Use `setup agent create` with the same options to persist the previewed defaults.
Optional flags configure model locality/fallback, memory scope/retention, network
posture, budgets, child bounds, learning mode, and mutable-skill scope; `--help`
lists the command shape. Always run preview first and inspect every capability
decision. `Deny` is the default for external network, credentials, messages, device
writes, privileged execution, and learning promotion. Exact tool/skill grants are
available through the application contract but remain approval-gated until their
catalogs exist.

Creation is allowed only while installation state is `Configuring`, requires a same-
installation provider with observed text capability, and returns JSON. Exit codes are
0 for success, 1 for validation/policy/state failure, 3 for a retryable write conflict,
and 130 for cancellation. Agent creation does not transition the installation to
`Ready`; minimum viability and administrator bootstrap remain separate gates.

Complete minimum-viability validation with:

```text
agentforge setup complete --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id>
```

The command verifies the audit chain, usable text provider and secret reference, and
named agent before it creates the local administrator and reaches `Ready`. Output
contains only the OS secret reference, never credential material. Keep the data
directory and OS secret-store account together; losing the client reference requires
the later authorized recovery flow. On Linux, a working Secret Service session is a
live prerequisite. The deterministic application-service fixture remains the
credential-free acceptance path in CI.

Ready runtime calls require `Authorization: Bearer <credential>`. Retrieve the value
through the OS secret reference for one invocation and clear it immediately. Never
copy it into shell history, process arguments, configuration, logs, or reports.

Inspect a configured installation without exposing credentials:

```text
agentforge doctor --data-directory <absolute-path> --actor <actor-id> --correlation <correlation-id>
```

The command exits 0 when every required check passes and 2 when diagnosis succeeds
but one or more checks fail. Create a redacted report and rollback profile using the
exact version returned by doctor:

```text
agentforge setup export --data-directory <absolute-path> --expected-version <n> --actor <actor-id> --correlation <correlation-id>
```

The output identifies content-addressed artifacts; it does not print the JSON or any
credential. Before configuration maintenance, enter recovery with an explicit reason:

```text
agentforge setup recovery enter --data-directory <absolute-path> --expected-version <n> --reason <text> --actor <actor-id> --correlation <correlation-id>
agentforge setup recovery resume --data-directory <absolute-path> --expected-version <n+1> --actor <actor-id> --correlation <correlation-id>
```

Entry automatically captures another pre-recovery rollback profile. Resume returns
to `Configuring`; after approved edits, run `setup complete` to revalidate and return
to `Ready` using the existing administrator identity. Snapshot restore and CLI edit
commands are not enabled yet; do not modify SQLite directly.

## SQLite migration and cold backup

The host applies checked-in forward migrations before it starts listening. Before a
manual cold backup, stop AgentForge and confirm no host process is using the data
directory. Copy `agentforge.db` and the content-addressed `artifacts` directory as one
backup set; preserve their relative layout and record SHA-256 hashes. Restore into a
new data directory, start in setup/recovery mode, and verify installation state and
the complete audit chain before permitting normal mode. Never replace a live WAL
database by copying only its main file.

Migration 0001 creates a new store and has no data-preserving down migration. On a
failed first install, retain diagnostics and restore the pre-migration directory. Do
not delete or overwrite a populated database to simulate rollback.

Migration 0003 creates agent identities and foreign-key binds their installation and
primary provider. Before upgrading, stop AgentForge and back up the complete SQLite,
artifact, and secret-reference set. Its generated down migration drops identities;
restore the pre-upgrade backup instead of applying down to operator state.

Migration 0004 creates the single local-administrator row. Its down migration drops
authentication state and is destructive; restore the full pre-0004 backup instead.

Migration 0005 creates setup-profile snapshot metadata and foreign-key binds each row
to its installation and content-addressed artifact. Its down migration discards
recovery evidence; restore the full pre-0005 SQLite/artifact/OS-reference backup set
instead of applying it to operator state.

## Gate and recovery rules

- Record every command and result in `artifacts/gates/<gate-id>.md`.
- A failed deterministic test makes the gate `Revise` or `Block`.
- Do not delete state during recovery. Back up the database and artifact metadata,
  validate hashes, then use a documented repair transition.
- Never place secrets in command lines, configuration, gate reports, or trajectories.
