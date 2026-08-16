# POST-R1 Automated Isolated Learning Evaluator Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Replace operator-attested candidate verification with a deterministic server-owned evaluation over the exact immutable
proposal artifact. This slice evaluates package integrity, deterministic loading, hostile authority-escalation input,
and exact bounded permission declarations. It does not execute candidate-authored programs, call a model, activate a
skill, grant it to an agent, approve the candidate, or run its canary.

Requirements: `AF-LEARN-005`, `AF-LEARN-001..002`, `AF-SEC-005`, `AF-AUD-001`.

## Security, durability, and portability disposition

- The API accepts only candidate ID and current version. It derives installation and verifier identity server-side and
  rejects the legacy manual `verify` transition; browser booleans, scores, hashes, content, and permissions are ignored
  as unknown JSON and cannot choose an evaluation outcome.
- The evaluator recomputes artifact length and SHA-256, accepts bounded PAX regular files only, rejects rooted/traversal,
  link, duplicate, unexpected, oversized, and over-count entries, and writes solely beneath a unique evaluator root.
- The ordinary portable loader runs twice over the isolated files. Canonical bytes and hashes must match each other,
  immutable candidate state, and the exact Installed `AgentProposal` registry descriptor.
- A deterministic hostile corpus rejects policy/approval bypass, secret disclosure, exfiltration, system-prompt access,
  and self-grant instructions. Only exact declarations ending in the read-only `:read`, `.read`, `:metadata`, or
  `.metadata` forms can pass automatically; every unknown or wider permission fails closed.
- The evaluator has no process, environment, provider, network, tool, credential, or active skill authority. Executable
  candidate tests remain out of scope until a digest-pinned container test contract is separately gated.
- The JSON receipt contains bounded checks and immutable hashes, is itself content-addressed, and supplies its hash to
  the existing verifier transition and audit chain. A pass only reaches `Verified`; critic, governor, canary,
  activation, and per-agent grant boundaries remain independent.
- The implementation uses BCL tar/JSON/hash APIs and the existing portable loader. It has no schema migration or
  platform-specific dependency and runs on Windows and Linux.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx --no-build --no-restore
PASS — 407 product tests and 2 Agent Framework spike tests; 4 expected equipped/live tests skipped

dotnet test tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj \
  --filter FullyQualifiedName~RecursiveLearningPersistenceTests
PASS — automatic accept, high-risk permission and hostile-instruction reject, path-escape reject, receipt readback, sandbox cleanup

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — complete Ready journey with legacy manual denial, automated five-check receipt, and exact replay

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no vulnerable packages

./scripts/verify-no-secrets.ps1
PASS — 756 tracked and untracked repository files

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS
```

Live in-app-browser verification used the existing durable Ready profile after a Release-host restart. The previously
inert PowerShell-validation proposal exposed only **Run isolated evaluation**; its confirmation view accepted no pass
fields or evidence note. The server advanced it to `Verified` with five visible PASS receipts, score 100/100, evaluator
identity `agentforge-managed-isolated-v1`, and evidence
`sha256:e7fc1dc02a913b3e6e40420dc666f07a7fd0d6c8bb5d7277bd02ba454d64a9c1`. It remains inactive and ungranted while
awaiting an independent critique. The updated local Release host is running at `http://127.0.0.1:5047/#learning`.

## Content evidence

```text
24d3a8205d0c8efc140003f3f2084d4c436fd3af3b70f3d3be26097bdac6f894  src/AgentForge.Learning/IsolatedLearningCandidateEvaluator.cs
d8565559102e409609cec4b09ca0210e0e90faa6217d4364fffec413ee0b9559  src/AgentForge.Host/Http/ReadyAdminLearningProposalEndpoints.cs
8cb106d6fd6fa01aee668cb62b37f794b585590b4e15b42e74363979cfb67e1c  src/AgentForge.Host/wwwroot/app.js
27d273bdd15c8a3fe8876afb08bc2057cfaf1c0b5b076be822a48e20e7472e21  tests/AgentForge.IntegrationTests/RecursiveLearningPersistenceTests.cs
aca7a997e7be4981bacdb067ee87c9e4913a6913a2dd485533c202c3b6fd124c  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

An automatically rejected candidate stays inert and terminal; create a new candidate from new classified evidence
rather than editing its package or receipt. A Verified candidate still has no active authority and may be rejected by
the independent critic or withheld by the governor. Temporary evaluator directories are deleted after every outcome;
bounded leftovers after an OS cleanup failure live only under `learning/evaluation-sandboxes` and may be removed by a
stopped-host recovery operation after confirming the resolved data-directory path. Code rollback is the inverse of the
feature commit and requires no database migration; historical artifact, candidate, skill-proposal, and audit evidence
remains append-only.
