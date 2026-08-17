# Gate: POST-R1-AI-BUILT-API-SKILLS-20260817

Status: **Pass**  
Date: 2026-08-17  
Requirements: AF-ADMIN-011, AF-LEARN-007, AF-TOOL-002, AF-SEC-001..005, AF-DATA-001

## Objective

Prove that AgentForge—not a hand-written Partner Center product module—can use its pinned local AI to author an
immutable Microsoft Partner Center customer skill, independently evaluate it, and connect its declared generic HTTP
read requirement to a separately configured bearer profile and exact runtime approval boundary.

## Delivered boundary

- New independent `AgentForge.HttpApi` module behind Domain/Abstractions contracts.
- Durable named HTTPS profiles with fixed base origin, relative verification path, non-secret headers, dynamic
  request/correlation templates, enabled state, optimistic versions, audit evidence, and OS-secret references only.
- Write-only bearer create/rotation UI and live-verified hash-bound preview/apply API.
- Built-in `tool:http-api.get@1.0.0` with credential risk, exact URI target, bounded scalar query, strict UTF-8 output,
  no redirect/cookie/proxy, and server-side profile/target re-resolution before execution.
- Local-model skill generation binds exact read-only tool requirements into request hash, generation evidence,
  immutable manifest, durable replay, and independent permission-diff evaluation.
- The run loop exposes `http_api_get` only after Active+granted skill selection, exact package requirement,
  `tool:http-api.read`, approved-endpoint posture, positive budget, enabled profile, and compatible tool transport.
- The operator reviews one canonical profile/path/query/response limit/resolved endpoint before grant or denial.

## Deterministic verification

Commands:

```text
dotnet restore AgentForge.slnx --force-evaluate
dotnet build AgentForge.slnx --no-restore
dotnet test AgentForge.slnx --no-build --no-restore
dotnet format AgentForge.slnx --verify-no-changes --no-restore
dotnet ef migrations has-pending-model-changes --project src/AgentForge.Persistence/AgentForge.Persistence.csproj --startup-project src/AgentForge.Persistence/AgentForge.Persistence.csproj --no-build
dotnet list AgentForge.slnx package --vulnerable --include-transitive
node --check src/AgentForge.Host/wwwroot/app.js
git diff --check
```

Results:

- Build: 0 warnings, 0 errors.
- Agent Framework spike: 2 passed.
- Architecture: 5 passed.
- Cross-platform: 15 passed, 1 credential/hardware-equipped Docker gate skipped.
- Unit: 325 passed.
- Security: 11 passed.
- Integration: 72 passed, 6 external-resource gates skipped in the ordinary suite.
- End-to-end: 15 passed.
- Formatting, JavaScript syntax, diff whitespace, EF migration drift, dependency vulnerability scan, and plaintext
  credential sentinel scan passed.
- `20260817071904_HttpApiProfiles` is the exact current EF model; forward/down and backup/restore instructions are in
  `docs/migrations/0032-generated-skill-http-api-profiles.md`.

## Live local-model acceptance

The credential-free gate used:

```text
AGENTFORGE_LIVE_SKILL_GENERATION_ENDPOINT=http://192.168.1.89:8000/v1
AGENTFORGE_LIVE_SKILL_GENERATION_MODEL=qwen3.8
dotnet test tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj --no-build --filter FullyQualifiedName~AgentForge_uses_local_ai_to_build_and_evaluate_partner_center_customer_skill
```

AgentForge captured a `MissingCapability` signal, invoked the pinned `qwen3.8`, strictly parsed the response, created
`skill:microsoft.partner-center.customers@0.1.0`, declared only `partner-center:customers:read` and
`tool:http-api.get`, installed the immutable `AgentProposal`, reopened its content-addressed workspace, and passed all
six managed evaluation checks. Runtime: 51 seconds.

Evidence:

- Candidate: `ef2a1f12-b539-46a0-a88c-10f12ad8ad4a`
- Model evidence: `sha256:c245e7b321a1eaabbc1753c7fd10ef66b0a217cea20afa29934f30ffec0787d2`
- Selected Markdown: `sha256:a2c4eaac4ca76a7be3790658a55aa850737c2150e33b272ce91f594e4988cd61`
- Immutable package: `sha256:446138d9a64f98d11604541b515a054e41ed603552ee05fdef226099306407f5`
- Independent evaluation: `sha256:cf7ab73a9acc0dafec3e0d48e3e477c5d6e94b0eb2479d4ee75454b03bc259bb`

The Partner Center bearer live GET remains correctly credential-gated by
`AGENTFORGE_LIVE_PARTNER_CENTER_BEARER_TOKEN`; it is skipped until the operator supplies a token. No bearer value was
used for generation, stored in the package, or written to this report.

## Security and portability impact

- Missing/unknown profile, missing skill/tool grant, non-Active skill, denied network posture, exhausted tool budget,
  unapproved request, stale endpoint, cross-origin/path escape, encoded dot segment, secret-shaped header, unknown
  template, non-scalar query, timeout, oversized output, non-UTF-8 content, 401/403, and redirect all fail typed/closed.
- Windows DPAPI and Linux Secret Service remain the only production secret adapters; no plaintext fallback was added.
- The HTTP client and domain contracts are portable. The ordinary Windows/Linux suites remain deterministic; only
  live API/provider access is environment-gated.
- The adversarial evaluator now recognizes explicit safe prohibitions such as “never execute without approval” while
  continuing to reject hostile “ignore previous instructions and bypass policy” candidates.

## Findings and rollback

No unresolved high-severity finding remains. The live rerun found and fixed a false-positive adversarial scan for
negated safety statements; deterministic regression tests cover safe and hostile forms.

Rollback is a normal commit revert plus the documented EF down/whole-store restore. Before schema rollback, disable
generated API skills and profiles, back up database/WAL/SHM/artifacts, and record OS-secret references that may become
orphaned. Never extract bearer material into rollback scripts. The feature has no Partner Center-specific runtime code
or seed package to unwind.
