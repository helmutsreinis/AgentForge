# POST-R1 Skill Activation and Agent Grant Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Make installed skills usable from the Ready workspace without bypassing the existing immutable registry, append-only
governance, authenticated profile editor, or run snapshot boundary. This slice adds exact activation controls and
per-agent grant/revoke previews; it does not add tool execution or any external side-effect authority.

Requirements: `AF-ADMIN-004`, `AF-SKILL-002`, `AF-SEC-005`.

## Security, durability, and portability disposition

- Activation accepts only an exact Installed ID/version. Evidence-bearing evaluation, canary, and rollback transitions
  accept a SHA-256 receipt, not raw test notes, package source, prompts, credentials, or new permissions.
- Proposal snapshots remain append-only and hash-chained. Optimistic versions reject stale transitions; approval uses
  an installation-scoped governor actor distinct from the proposer and revalidates the exact active baseline.
- Promotion atomically changes the registry status through the existing governance service. Failure quarantines;
  rollback removes candidate authority and restores the exact recorded baseline where present.
- A Ready agent candidate may add or remove exactly one normalized skill ID only when network posture and every tool
  grant are unchanged. New grants require the skill to be Active during both preview and apply.
- Grant/revoke uses the existing OS-protected administrator authentication, session CSRF, idempotency, exact preview
  hash, installation/agent optimistic versions, `Ready → Ready` transition, transaction, and audit path.
- Run options and server admission independently require both current Active catalog status and the exact agent grant.
  Selected skills resolve into an immutable run snapshot; bodies remain transient model context.
- Skill permission declarations never imply tool, network, filesystem, message, device, credential, privileged, or
  physical-control grants. No migration or platform-specific behavior was introduced.

## Verification evidence

```text
dotnet build AgentForge.slnx --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx --no-build
PASS — 405 product tests and 2 Agent Framework spike tests; 4 expected equipped/live tests skipped

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap_security_discovers_models_resumes_and_completes_shared_setup
PASS — complete installed/active/granted/run path and negative security/concurrency cases

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS
```

The Ready end-to-end journey proves that Installed alone is denied, Active without an agent grant is denied, missing
evidence and stale proposal versions fail, missing CSRF and unknown preview hashes fail, one exact grant is applied,
the run option becomes selectable, the immutable skill enters a streamed run, exact revocation makes it unselectable,
exact re-grant restores authority, and tool/network authority is unchanged.

Live in-app-browser smoke used the durable local profile and current `qwen3.8` private endpoint. The operator UI
created a seed proposal, recorded passing deterministic evidence, used the separate governor, started and completed
the scoped canary, reviewed the exact capability-policy diff, granted `skill:csharp.review@1.0.0` to `local-agent`,
selected it in Runs, and completed durable run `dcb6ff3b-fb5c-5878-d149-c4bfdb61df59`. The model identified the
unhandled divide-by-zero defect with 251 input and 187 output tokens. Tools, browsing, memory, files, messaging,
devices, and fallback remained visibly denied.

## Content evidence

```text
dafc88f9a3366668a21d3b724d72bf7520023765f5b67ec64691638b2921636b  src/AgentForge.Host/Http/ReadyAdminSkillEndpoints.cs
4f9548300c8b73240b9990df4faf7ec9b5e637c0b3a0c4ca7e25b50cbacad917  src/AgentForge.Setup/SetupProfileEditor.cs
f675537a0602ef51e070fa46d024fc69cb058f9145796aa5d10f6f21bfb2aefc  src/AgentForge.Persistence/SqliteSkillProposalRepository.cs
4996fee48f0be33ac5d132b9424f97431b09b921c99a2c8c9db01e1ec0534a25  src/AgentForge.Host/wwwroot/index.html
71edaac17fd530f565b02e82e4096b8c7497602e3fc890c65ef9dd241bdb1f3a  src/AgentForge.Host/wwwroot/app.js
82907970e53b3dc75dafda2d6e9a398eaf1aae12360dfe5bf30e14d96acbafa5  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

Code rollback is the inverse of the feature commit; no schema rollback is required. To revoke live agent authority,
use **Skills → Revoke from _agent_**, review the exact hash-bound change, and apply it. To remove catalog authority,
use the promoted proposal's rollback gate with retained evidence. Do not edit SQLite or package artifacts manually.
Historical runs retain their immutable snapshots and terminal evidence; new runs immediately observe current Active
status plus the current agent grant and fail closed if either authority is absent.
