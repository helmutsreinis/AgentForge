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

## Gate and recovery rules

- Record every command and result in `artifacts/gates/<gate-id>.md`.
- A failed deterministic test makes the gate `Revise` or `Block`.
- Do not delete state during recovery. Back up the database and artifact metadata,
  validate hashes, then use a documented repair transition.
- Never place secrets in command lines, configuration, gate reports, or trajectories.
