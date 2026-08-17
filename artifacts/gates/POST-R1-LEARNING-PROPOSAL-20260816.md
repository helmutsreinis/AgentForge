# POST-R1 Learning Proposal and Governance Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Turn a classified Ready-state `NewSkill` signal into one isolated immutable AgentProposal package, expose its
durable candidate evidence, and make the existing verifier/critic/governor/canary/promotion/rollback lifecycle
testable without granting the proposer or the browser any direct registry, policy, tool, or agent authority.

Requirements: `AF-LEARN-001`, `AF-LEARN-004`, `AF-SKILL-002`, `AF-HOST-003`, `AF-SEC-003`.

The slice deliberately does not claim that the harness executed target or holdout suites. Web evidence receipts are
explicit operator attestations until the next isolated automated-evaluator slice replaces those inputs.

## Security, durability, and portability disposition

- Only an installation-scoped signal classified by the existing deterministic service as `NewSkill` is eligible.
- Candidate and proposal IDs derive from installation plus idempotency identity. A signal owns at most one candidate.
- The service writes only `SKILL.md` and `skill.harness.json` beneath a server-derived link-free candidate directory,
  rejects conflicting or unexpected content, and validates the result through the ordinary portable package loader.
- Packages enter the ordinary registry as immutable `AgentProposal` provenance and `Installed` status. Declared
  permissions are not grants. A deterministic PAX tar workspace is content-addressed separately.
- Worker, proposer, verifier, critic, and governor actor IDs are distinct and resolved server-side. Transition bodies
  cannot replace them, source content, provider topology, policy, tools, or permissions.
- Every transition requires the protected Ready session, origin, CSRF, idempotency key, current snapshot version, and
  the exact state-machine order. Evidence-bearing gates accept only a SHA-256 receipt; UI note text is hashed locally
  and never transmitted or persisted.
- Passing verification and approval remain inactive. Only a passing non-regressing canary makes the package Active;
  agent-level skill grants remain independently required. Rollback quarantines the candidate and restores an exact
  baseline when present.
- Exact proposal creation is restart-safe across the package-install and skill-proposal commit boundaries. An exact
  still-Proposed skill proposal replays; different or advanced state conflicts.
- Package requirements cover Windows and Linux and use no platform-specific implementation. No migration was needed.

## Verification evidence

```text
dotnet build AgentForge.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build -m:1 -p:UseSharedCompilation=false
PASS — 404 product tests and 2 Agent Framework spike tests; 4 expected equipped/live tests skipped

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Release --no-build \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap_security_discovers_models_resumes_and_completes_shared_setup
PASS — complete proposal, promotion, and rollback journey

dotnet test tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj -c Release --no-build \
  --filter FullyQualifiedName~Seed_and_user_skills_share_governance_snapshots_and_atomic_rollback
PASS — exact proposal replay and governed rollback

dotnet format AgentForge.slnx --verify-no-changes --no-restore
PASS

node --check src/AgentForge.Host/wwwroot/app.js
PASS

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no vulnerable packages

pwsh -NoProfile -File scripts/verify-no-secrets.ps1
PASS — 747 tracked and untracked files scanned

git diff --check
PASS
```

The deterministic Ready journey proves empty candidate listing, missing-CSRF denial, Memory-signal denial, malformed
skill denial, exact proposal replay, conflicting second-candidate denial, five distinct actors, inactive status,
workspace artifact readback, immutable AgentProposal provenance, hash-shaped evidence enforcement, stale-version denial,
verification, critique, approval, canary start, promotion, active-authority reporting, rollback, quarantine, latest-snapshot
query, and exclusion of quarantined packages from run options.

Live browser smoke against the existing `qwen3.8` installation created one bounded MissingCapability signal and one
inactive `skill:proposal.the.local.agent.needs.a` candidate. The queue rendered package/workspace hashes, five-role
separation, no authority, the next gate, and all four verification checks plus baseline/candidate metrics. It was left
`Proposed`; no promotion or agent grant was performed. Browser console warning/error count: zero.

## Content evidence

```text
16e9cd0ac3b9eb4582d8dc41c884eeae9e9f2aacef5f4433a9ca235efe58872c  src/AgentForge.Learning/LearningCandidateProposalService.cs
10d393c6202968e0fac9e5f711b87790b62d5be4edb94794ff09eb68dc1638a9  src/AgentForge.Host/Http/ReadyAdminLearningProposalEndpoints.cs
dfd40a52aadd80bf484f1d1670414e8c2175758ab7235f6c610df37f89962101  src/AgentForge.Skills/SkillGovernanceService.cs
cd8940df3be42920f629efc6a27e902daf186645d97ea40084c9a8fbda2d7da8  src/AgentForge.Host/wwwroot/app.js
d440792c5c79cec0ab16e3e42e279a8b7a0716eb1c9ecc2900a858e670a3ca01  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

Code rollback is the inverse of the single feature commit. No schema migration was introduced. Existing learning,
skill, proposal, artifact, and audit rows remain compatible because this slice uses the M5/M9 persistence model.

Do not delete a live proposal workspace or edit registry/candidate JSON. An inactive `Proposed` package is safe to
leave installed. If creation was interrupted, repeat the exact request identity; package and still-Proposed governance
records replay and candidate construction resumes. If a candidate was promoted, use its explicit rollback transition
with fresh evidence before reverting code. That transition quarantines the candidate and restores the exact recorded
baseline. A stale or conflicting retry must be reloaded and inspected, never forced.
