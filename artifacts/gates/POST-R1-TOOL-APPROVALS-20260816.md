# POST-R1 Policy-Aware Tool Execution Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Expose the existing authoritative tool, capability-policy, approval, sandbox, durable invocation, and audit boundaries
as one testable Ready operator workflow. This slice adds exact per-agent tool grants plus explicitly approved managed
workspace reads. It does not add model-selected tool calls, a general shell, network access, file writes, external
mutation, credentials, privilege, destructive operations, or physical control.

Requirements: `AF-ADMIN-005`, `AF-TOOL-002`, `AF-SEC-001..005`, `AF-AUD-001`.

## Security, durability, and portability disposition

- The default catalog now contains two immutable typed built-ins: bounded direct-child directory metadata and bounded
  strict-UTF8 file content. Their descriptor hashes include capability, risk, target, side effects, parameters,
  output sensitivity, handler identity, BuiltIn sandbox, denied network, output limits, and provenance.
- BuiltIn is an explicit execution kind, not a restricted-process fallback. Handlers start no process, receive no
  environment, and have no networking primitive. Process descriptors continue through their declared sandbox.
- Preview and execution independently require absolute existing workspace/target paths, prove containment, reject UNC
  workspace roots on Windows, reject traversal and every traversed link/reparse point, and enforce file/entry/output
  bounds. Unsupported handlers and isolation fail typed.
- A Ready profile change may add or remove one exact available tool capability and only the per-run invocation ceiling.
  Every other budget member, network posture, skills, model, memory, child, and learning authority stays unchanged.
- Catalog grant and invocation authority are separate. Invocation requires the current exact agent grant plus an
  authenticated approval bound to installation/agent versions, actor, tool/version/descriptor hash, canonical
  parameters, target, workspace, expiry, correlation, and request hash. Grants are consumed before execution.
- The shared planner produces both approval and execution identity. The browser holds only a bounded session preview;
  apply cannot submit replacement parameters. Exact denials persist without execution, and consumed previews fail.
- Authorization and terminal state remain durable and audited. Raw output is returned only in the authenticated
  response and never enters SQLite, audit, or durable replay; hashes and lengths remain as evidence.
- The CLI composition now registers the same authoritative catalog so existing Ready profile edits remain resolvable.
  Locked restore records the new project reference. No schema migration or platform-specific behavior was introduced.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx --no-build --no-restore
PASS — 405 product tests and 2 Agent Framework spike tests; 4 expected equipped/live tests skipped

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj \
  --filter FullyQualifiedName~WebSetupWizardTests
PASS — 2 complete Ready/setup journeys

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no vulnerable packages

./scripts/verify-no-secrets.ps1
PASS — 753 tracked and untracked repository files

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS
```

The Ready end-to-end journey proves malformed-disposition rejection, denial before grant, exact two-path
capability/budget preview, grant apply, catalog readback, path-escape denial before approval, RequireApproval
evaluation, BuiltIn/Denied evidence, one successful
directory listing, output hashing, consumed-preview denial, an exact denial that executes nothing, and revocation back
to an empty grant set with a zero invocation ceiling. Existing durable invocation tests continue to prove typed schema,
credential-shaped input, network escalation, approval consumption, idempotency, raw-output privacy, and audit integrity.

Live in-app-browser verification used the existing Ready local profile. The operator granted
`tool:workspace.read` to `local-agent`, reviewed the exact `tool:workspace.list@1.0.0` descriptor, canonical parameter
JSON, repository target/workspace, five-minute expiry, BuiltIn sandbox, and denied network; one single-use approval then
returned 32 direct repository entries and retained only the 1,692-byte output hash. The host remains available at
`http://127.0.0.1:5047/#tools` with a durable ten-invocation per-run ceiling; a Release-host restart reloaded both
descriptors and the agent grant. Model-selected tools remain disabled.

## Content evidence

```text
6364188537ea2adf45416793454aba90ffc5caf035077a1cc8131a5ee9331ba8  src/AgentForge.Host/Http/ReadyAdminToolEndpoints.cs
6c2b93027b6ef91e553472894d3b4727aa389608fe38bd3cfaf8645ab8793dbc  src/AgentForge.Tools/ToolInvocationPlanner.cs
a76e4c06c4f242d1e50686460f9a65ad4adc1c0fa0e00479cfdfe2b01c0c5079  src/AgentForge.Tools/BuiltInWorkspaceToolExecutor.cs
5e01cfdea1e4411b1df83bf0e10d710a752c48d2d1f55cce13100dbac180750c  src/AgentForge.Setup/SetupProfileEditor.cs
50429dbfb456fc3e6e10617b8bbd332dae6944dc26b32b54ae20c77f317bcb0c  src/AgentForge.Persistence/SqliteCapabilityApprovalRepository.cs
9dbf0f2370d6be2ea8d63f77289afc0c72af8e20d40ed69ead375f8060745673  src/AgentForge.Host/wwwroot/app.js
633b36a57cd09ccdd9b86cada1b2740353bec58c9ab20501328862abd691cab4  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

Use **Tools → Revoke from _agent_**, review the exact capability plus budget change, and apply it. This immediately
removes catalog authority and returns the ceiling to zero when no tool grants remain. Active approvals are exact,
expiring, and cannot authorize another parameter/target/workspace or changed policy version; already consumed grants
cannot be restored. Historical invocation/audit hashes remain append-only. Code rollback is the inverse of the feature
commit and requires no database migration. Do not edit SQLite approval, invocation, agent, or audit rows manually.
