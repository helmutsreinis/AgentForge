# POST-R1 Pinned Local-Model Skill Generation Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Replace the Ready workflow's fixed NewSkill scaffold with a strictly parsed procedure authored by the source run
agent's current pinned private model. The resulting package remains inert and enters the automated evaluator. This
slice does not let the model choose agents/providers/permissions, call tools, activate a skill, critique itself,
approve itself, run a canary, or revise an existing skill.

Requirements: `AF-LEARN-006`, `AF-LEARN-001..002`, `AF-MODEL-002`, `AF-SEC-003,005`.

## Security, durability, and portability disposition

- The authenticated endpoint derives the source task from immutable signal causation, verifies its latest terminal
  snapshot hash, and derives the agent ID server-side. The current agent must be local-only/no-fallback with explicit
  proposal-workspace learning authority.
- Only an exact credential-free vLLM/OpenAI-compatible text profile may be used. The existing model adapter separately
  permits only loopback/literal-private endpoints. Generation sends zero tools and exposes no environment, process,
  file, network, message, device, credential, fallback, or active skill capability.
- Inputs are preflighted through the sensitive-data redactor. The fixed instruction treats summaries and guidance as
  untrusted data; security still depends on strict output validation and downstream governance rather than obedience.
- Output must finish cleanly and parse as one exact JSON object with one bounded Markdown value and six required
  headings. Duplicate/extra shape, fences, missing headings, package markers/frontmatter, script tags, control bytes,
  and sensitive output fail without installing a candidate.
- The selected Markdown and a hash-only receipt are included in the ordinary immutable package and workspace. Receipt
  identity covers source evidence, candidate, skill/version, agent/provider/model versions, stable request, provider
  evidence, raw response, selected body, redaction count, and finish reason. A matching durable replay reads this
  receipt instead of calling the model again.
- The automated evaluator now verifies marked-body/provenance identity as a sixth check. Passing generation and
  evaluation still grants no active or per-agent authority.
- Implementation uses portable BCL JSON/hash/tar primitives and existing provider/package contracts. No schema or
  platform-specific dependency was added.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx --no-build --no-restore
PASS — 415 product tests and 2 spike tests; 4 expected credential/container/PostgreSQL skips

dotnet test tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj \
  --filter FullyQualifiedName~SkillCandidateDraftParserTests
PASS — 7 strict structured-output cases

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — sensitive/malformed generation denial, provenance response, six-check evaluation, exact replay

dotnet test tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj \
  --filter FullyQualifiedName~RecursiveLearningPersistenceTests
PASS — private authority, generation receipt, six-check evaluation, and cross-scope durable replay with one model call

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable direct or transitive packages

./scripts/verify-no-secrets.ps1
PASS — 760 tracked and untracked repository files scanned

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS
```

Live in-app-browser verification used `local-agent` version 3 and its pinned credential-free `qwen3.8` provider. From a
new terminal-run `MissingCapability` signal, the model generated a static PowerShell safety/portability review under
the declared `repository:read` boundary. The immutable generation receipt recorded model evidence
`sha256:d4ad532b0d43c084d73765905bb19ab0627b4c5309b9d1fe9c7d2be34f1d833a`, raw-response hash
`sha256:425cf2c18a72d902bec35bcaeda70295dc983b15d92f0d213feee4d185c3fe34`, and selected-body hash
`sha256:20020f4a137d64892a9eaaa78e76f1b35394764a25fcd9c2448edb2b895bf970`. The managed evaluator then passed all six
checks and stored receipt `sha256:a6207a2e49af9d55a2f1761c99014404b55e7c16ef4765880e269ebcc40a4ec3`.
The candidate is `Verified` but remains inactive, ungranted, and awaiting a separate critic. The updated Release host
is running at `http://127.0.0.1:5047/#learning`.

## Content hashes

```text
59c1155edd3a33160895bcd481d737779229b963fd0d214c4b1cf564cd042361  src/AgentForge.Domain/Learning/SkillCandidateDraft.cs
ddd0af32c4b713ac4c04ddcfaefeff51ca0ae5c1ed38ed37bebf13b5a0949efd  src/AgentForge.Learning/LocalModelSkillCandidateGenerator.cs
a8b092cc59e994064a3b700485f6bb9bb8242e72c197efee88bc0b638c4649e6  src/AgentForge.Learning/LearningCandidateProposalService.cs
594f8adecf943c620e64958404b24efd3621c7746d18e3d222c3bd6bfb5956c4  src/AgentForge.Learning/IsolatedLearningCandidateEvaluator.cs
5f709c0aac40b37138e4bdbcd5c2686ed893812ae2522988bcfd1d7200a805fc  src/AgentForge.Host/Http/ReadyAdminLearningProposalEndpoints.cs
7a222329794e71d1d8e63c4f6e0c370e0d648a1f8075d2308bf2d38f5e462391  src/AgentForge.Host/wwwroot/app.js
29c131e62170352777250d239976e4187cea8b70b3ea9c47213b7d4573590971  tests/AgentForge.UnitTests/SkillCandidateDraftParserTests.cs
4e60f16f86e0841d1bda106107d64e1eb61325cd0011a3b02e8646dd4f241d40  tests/AgentForge.IntegrationTests/RecursiveLearningPersistenceTests.cs
d73b3b624e2c300ea12a2dd5e1d2c9d489284f078ce0e11951807461493702d3  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

Malformed or denied generations install nothing and may be retried with a new idempotency key after correcting input
or provider availability. An installed candidate is immutable; create new classified evidence/version rather than
editing its body or receipt. A matching durable retry reuses the exact candidate without another model call, while a
changed request conflicts. Verified candidates remain inert unless later independent gates pass. Code rollback is the
inverse of the feature commit and needs no migration; existing model-generated package, workspace, evaluation, and
audit hashes remain append-only historical evidence.
