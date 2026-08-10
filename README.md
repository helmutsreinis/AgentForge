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
`127.0.0.1:5047` by default. An uninitialized installation exposes health and setup
status only; normal runtime endpoints fail closed until setup is complete.
