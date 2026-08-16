# POST-R1 Scalable Response Budget Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Replace the deliberately conservative Ready-run response presets with a governed, scalable output budget suitable
for a private local model. The operator can version one agent's generated-output ceiling and choose either a preset
or an exact per-run limit without changing any tool, network, skill, memory, child-agent, learning, provider-topology,
input, turn, tool-invocation, or wall-clock authority.

Requirements: `AF-ADMIN-003`, `AF-HOST-003`, `AF-SEC-003`.

## Security, durability, and portability disposition

- A Ready profile candidate may change only identity/instruction fields and `AgentBudget.MaxOutputTokens`. The setup
  edit service, not the browser endpoint alone, proves all other budget members are byte-for-byte unchanged.
- The durable ceiling is bounded to 256–262,144 tokens and is committed through the existing authenticated,
  hash-bound, optimistic, idempotent, audited `Ready → Ready` profile flow.
- Each streamed run accepts a server-validated integer limit no greater than the current durable agent ceiling or
  the 262,144-token hard cap. The exact accepted value enters task budget, snapshot hash, transient SSE configuration,
  interaction request, and idempotency request hash.
- Presets are convenience values only: Concise 512, Balanced 2,048, Detailed 8,192, Extended 16,384, and Maximum at
  the current agent ceiling. Native form validation accepts any whole token count; server validation remains decisive.
- The local compatible adapter is aligned to the same token cap and a 270-second interactive wall-clock cap. Event
  and response-character limits scale with the request but remain hard-bounded at 300,000 events and 2,097,152
  characters; the provider transport retains its independent 16 MiB response boundary.
- A larger output allowance grants no tools, browsing, fallback, files, messaging, devices, credentials, or skills.
  Raw prompts and output remain transient. No migration or platform-specific behavior was introduced.

## Verification evidence

```text
dotnet build AgentForge.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build -m:1 -p:UseSharedCompilation=false
PASS — 405 product tests and 2 Agent Framework spike tests; 4 expected equipped/live tests skipped

dotnet test tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj -c Release --no-build \
  --filter FullyQualifiedName~LocalModelInteractionServiceTests
PASS — 5 focused interaction-service tests, including 32,768 tokens / 270 seconds

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Release --no-build \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap_security_discovers_models_resumes_and_completes_shared_setup
PASS — profile ceiling, presets, exact run admission, over-ceiling denial, completion, and unchanged authority

dotnet format AgentForge.slnx --verify-no-changes --no-restore
PASS

node --check src/AgentForge.Host/wwwroot/app.js
PASS

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no vulnerable packages

pwsh -NoProfile -File scripts/verify-no-secrets.ps1
PASS — 748 tracked and untracked files scanned

git diff --check
PASS
```

The deterministic Ready journey applies a 32,768-token ceiling, projects all five presets, admits an exact
12,000-token streamed request, rejects 40,000 with `BudgetExceeded`, completes durable evidence, and proves the
turn, input, tool, network, memory, child, and learning posture is unchanged.

Live in-app-browser smoke upgraded the existing `local-agent` from 4,000 to 32,768 through an exact `agent.budget`
preview and audited apply. Maximum resolved to 32,768, Extended to 16,384, and an exact 12,000-token run reached the
configured `qwen3.8` private endpoint. The model returned `RESPONSE BUDGET READY` and stopped normally after seven
output tokens, proving the budget is an upper bound rather than a forced response length. Browser warning/error count:
zero. The smoke also exposed and closed a native numeric-step validation defect plus the legacy 4,096-token/120-second
local-adapter caps before commit.

## Content evidence

```text
90a90d516a8a20e53de34f7db31835dcadc3aa22da44639e8cd43b50fb527cf9  src/AgentForge.Models/LocalModelInteractionService.cs
7e17f13550c8e792781cb7979d25e3a77a66ede0c5d130c4f81641d7d5204031  src/AgentForge.Setup/SetupProfileEditor.cs
5a25f46448d34f136932a6a6b10b5164cc748a646c1de042d5b0aa65476a10fd  src/AgentForge.Host/Http/ReadyAdminAgentEditEndpoints.cs
c9b77b5491f155f62d4a466c8d0b4c741f971ef81ac0c93e0a02a02a829b6892  src/AgentForge.Host/Http/ReadyAdminEndpoints.cs
808c3dfafcae5966675b783cc855b6fd832d9c2496a65299fcdbcff3f540aa4e  src/AgentForge.Host/wwwroot/index.html
b81d395633d16aac5227484468568108716d85a398497f6868fe9046ab12400d  src/AgentForge.Host/wwwroot/app.js
7e3fac0447b2e3445059d08cc2b7b4dab658058fdf4fbbfad3ef201c819e5517  tests/AgentForge.UnitTests/LocalModelInteractionServiceTests.cs
4d606f1c8cfe21507ddfb320ad2a982a0d71382e45c2098374295719eaf5feeb  tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs
```

## Rollback and recovery

Code rollback is the inverse of the single feature commit. No schema migration was introduced. Existing agents with
ceilings at or below the old values remain valid and receive the larger preset catalog clamped to their own ceiling.

To lower a live agent ceiling, use **Agents → Edit agent**, preview the exact `agent.budget` diff, and apply it; do not
edit SQLite. Existing terminal receipts remain valid because they bind the accepted run budget in their historical
snapshot hash. A run that exceeds a newly lowered ceiling is rejected before task admission. If a private provider has
a smaller context/output capability, choose a lower durable or per-run limit; a provider-side rejection must remain a
typed failed receipt and must never cause fallback or authority expansion.
