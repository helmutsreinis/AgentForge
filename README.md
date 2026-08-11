# AgentForge

AgentForge is a security-first, cross-platform recursive agent harness for .NET 10.

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
`127.0.0.1:5047` by default. Open `http://127.0.0.1:5047/` for the read-only local
control-plane preview. An uninitialized installation exposes health and setup status
only; normal runtime endpoints fail closed until setup is complete. Credential entry
and every setup mutation remain in the trusted CLI until the authenticated web-wizard
gate is complete.

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
intended. Milestone 1 is the `0.1 Foundation Alpha` checkpoint; see
`docs/RUNBOOK.md` for the currently executable commands.
