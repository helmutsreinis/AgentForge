# AgentForge

AgentForge is a security-first, cross-platform recursive agent harness for .NET 10.

Production R1 (1.0.0) completes Milestones 0–10 and all 25 acceptance scenarios. The
machine-generated release record is `artifacts/acceptance/R1-execution.json`, and the
final security/release decision is `artifacts/gates/M10-10-20260812.md`.

The repository is developed through evidence-backed vertical slices. Current status,
requirements, and verification evidence live in `docs/PROJECT_STATE.md`,
`docs/REQUIREMENTS.md`, `docs/TRACEABILITY.md`, and `artifacts/gates/`.

## Developer quick start

```text
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Run the local host with `dotnet run --project src/AgentForge.Host`. It binds to
`127.0.0.1:5047` by default. Open `http://127.0.0.1:5047/` for the local control plane
and secure first-run wizard. The wizard is loopback-only and uses a one-time nonce,
short-lived HttpOnly session, CSRF token, mutation idempotency, and the same setup
services and conservative defaults as the CLI. An uninitialized installation exposes
health and setup only; normal runtime endpoints fail closed until setup completes.

Start the first offline setup transaction with explicit deterministic input:

```text
dotnet run --project src/AgentForge.Cli -- setup begin --data-directory <path> --actor local-operator --correlation setup-001 --installation-id 00000000-0000-0000-0000-000000000001
```

The interactive equivalent is `agentforge setup begin --interactive`. Shared setup
services now support validated provider profiles, bounded named agents, minimum-
viability completion, local administration, doctor, redacted export, and authorized
recovery. Provider onboarding accepts credentials only through redirected stdin or a
hidden prompt, and recovery edits require authenticated version/hash-bound preview
and apply. Hash- and audit-proven rollback snapshots can be restored without changing
entity topology.

Capture a passive, hashed environment profile without executing any discovered tool:

```text
agentforge environment inspect --data-directory <path> --actor local-operator --correlation environment-001
```

The command stores redacted content-addressed evidence and prints only an executable
count by default. Add `--include-executables true` only when local path disclosure is
intended. See `docs/RUNBOOK.md` for the executable R1 commands and operational limits.

Milestone 8 adds passive Windows/Linux serial inventory, stable physical-device identities,
separate capture/read/write/command/calibration/firmware/privileged grants, bounded immutable
capture artifacts, deterministic replay, and governed declarative decoder promotion. Production
still installs no serial transport, exposes no device I/O route, and leaves compiled decoders and
real hardware behind explicit live gates.
