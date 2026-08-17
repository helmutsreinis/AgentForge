# POST-R1 Ready Profile Administration Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Add credential-safe provider creation, complete agent creation, and complete Ready-state agent-policy editing after the
specialized skill and tool authority gates. This slice does not rotate an existing provider endpoint/type/credential,
rewrite historical run snapshots, continue an old conversation under changed authority, or grant model-selected tool
execution.

Requirements: `AF-ADMIN-006`, `AF-HOST-003`, `AF-SEC-003..005`.

## Security, durability, and portability disposition

- Every provider and agent mutation requires the protected Ready operator session, same-origin/HTTPS-remote controls,
  CSRF, bounded idempotency, installation scope, and invocation-scoped local-administrator authentication.
- Provider preview and apply separately place the submitted credential behind a transient OS-secret reference, perform
  a bounded live model probe, and delete that secret. Preview retains only a SHA-256 credential fingerprint; apply
  rejects a substituted credential and commits only the final OS-backed reference.
- A complete agent candidate contains routing/locality/fallback, memory, network, exact tool and Active-skill grants,
  run and child budgets, learning posture, and identity. The ordinary conservative evaluator and Ready-specific bounds
  run at preview and apply, and every newly added grant is resolved against current authority.
- Preview hashes bind normalized candidates, credential fingerprint where relevant, actor/correlation, and exact
  installation/provider/agent versions. Apply is atomic and audited in the still-Ready installation. Stale state,
  changed credentials, changed grants, duplicate names, and conflicting idempotency fail closed.
- Version-impact responses state that old immutable run snapshots remain evidence while changed provider/agent
  authority requires a new conversation. No schema migration or platform-specific policy implementation was added.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx -c Release --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build --no-restore
PASS — 420 product tests + 2 framework-spike tests; 4 expected environment-gated skips

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Debug \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — credentialed provider preview, wrong-credential denial, exact create, complete agent create/edit, and durable readback

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable direct or transitive packages

./scripts/verify-no-secrets.ps1
PASS — 774 tracked and untracked candidate files

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS

dotnet ef migrations has-pending-model-changes --project src/AgentForge.Persistence \
  --startup-project src/AgentForge.Persistence --configuration Release --no-build
PASS — no model changes
```

## Live Ready-workspace evidence

- The Release host is healthy at `http://127.0.0.1:5047/` against the existing Ready data directory and pinned
  credential-free private `qwen3.8` provider.
- Agents visibly lists the provider and agent separately, exposes **New provider**, **New agent**, and **Edit agent**,
  and renders the complete policy form rather than isolated output/grant controls.
- A non-persisting `preview-agent` review reached an exact complete-policy hash. The preview was explicitly discarded;
  the durable installation still contains one provider and one agent.
- The final UI asset converts object-valued enum fields into operator-readable policy names in the review. The host was
  rebuilt and restarted after that presentation-only correction.

## Evidence hashes

```text
6af97efe295f301b77d77a07fe27b67288445add6dba89609ec0a6232b8c9143  SetupProfileEditRecords.cs
f9f48249c1df10a4881774a4e40a85bf453970562600ba50d40f1bd4d0eff816  ISetupProfileEditor.cs
210858773932694ca3132bfbb1f122eda345729dfec1607a92c263f7ae040f87  SetupProfileEditor.cs
1f112097196997f984f7d62e9648bed2fc25b6aca9905202b0a0de7383eca712  ReadyAdminProfileCreateEndpoints.cs
c2f4bdb16e7dc044544b11cf8982c2ecdad6a05083d16a992aec3c9caee88e7a  ReadyAdminAgentEditEndpoints.cs
985d1fb720342da1713aa6a40ecd659acd45828bad5da168c578e9620ca117f5  ReadyAdminSessionManager.cs
41b4f21ce47fe3cb48239ec936520f2791daabb712f9155aab18e6752e1ce775  app.js
8b3a62acb5385d6003b0924cd6b4a73211beff681b735e9444419a3957d0bc08  index.html
1abd2c0808b80a1e411798554810e81574726f793a6bb8d6e46b00558a7d44bf  WebSetupWizardTests.cs
```

## Rollback and recovery

Code rollback requires no database migration. Created providers/agents are ordinary append-only audited profile
history and must not be removed by editing SQLite. A failed provider create attempts to remove its transient or
uncommitted OS-secret reference; run doctor if the platform secret adapter itself reports cleanup failure. If an
operator edits an agent/provider and needs the previous configuration, use the authenticated recovery/export/restore
workflow and start a new conversation rather than weakening the stored authority pin.
