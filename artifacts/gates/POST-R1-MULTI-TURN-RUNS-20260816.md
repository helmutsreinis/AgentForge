# POST-R1 Multi-turn Runs and Resume Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Turn Ready's streamed one-shot interaction into a bounded durable conversation with authenticated details, follow-up
turns, exact authority snapshots, cancellation, and safe restart/resume. This slice does not expose model-selected
tools, change policy, inject memory/search, or continue a completed model turn.

Requirements: `AF-RUN-002`, `AF-RUN-001`, `AF-TASK-001`, `AF-DATA-001`, `AF-AUD-001`, `AF-SEC-003`.

## Security, durability, and portability disposition

- The append-only pure state machine binds installation, actor, agent/provider versions, model, policy, budget, system
  context, skill snapshot, task/turn identity, idempotency, and content hashes. Every mutation appends a canonical hash
  chain; stale versions and duplicate turn authority conflict.
- System instructions, prompts, and answers are redacted before persistence and stored as bounded strict-UTF8
  content-addressed artifacts. Details recheck length, media type, UTF-8, and SHA-256. Audit stores hashes only.
- Every turn has its own leased orchestration task with two attempts, zero model tools, existing output/event/time
  limits, exact cancellation, and no completed-turn replay. Resume accepts only the current incomplete turn and only a
  Ready retry or expired abandoned lease.
- Model history accepts user/assistant single-text messages only and is capped at 20 completed turns/100,000
  characters; the aggregate is capped at 64 turns. Agent/provider edits invalidate continuation instead of silently
  changing authority.
- Migration 0028 adds only the portable relational snapshot table and creates no legacy conversation data. Artifact,
  JSON, hash, and state-machine implementations use existing cross-platform BCL and repository contracts.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS — no formatting changes required

dotnet build AgentForge.slnx -c Release --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build --no-restore
PASS — 420 product tests + 2 framework-spike tests; 4 expected environment-gated skips

dotnet test tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj \
  --filter FullyQualifiedName~RunConversationStateMachineTests
PASS — 3 chain, resume, duplicate-authority, and tamper cases

dotnet test tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj \
  --filter FullyQualifiedName~RunConversationPersistenceTests
PASS — redaction, content hashing, idempotency, restart, resume, and audit integrity

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — details, two-turn continuation, missing-CSRF denial, retryable interruption and HTTP resume

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable packages from configured sources

./scripts/verify-no-secrets.ps1
PASS — 772 tracked and untracked candidate files

node --check src/AgentForge.Host/wwwroot/app.js
PASS

git diff --check
PASS

dotnet ef migrations has-pending-model-changes --project src/AgentForge.Persistence \
  --startup-project src/AgentForge.Persistence --configuration Release --no-build
PASS — no model changes since migration 0028
```

## Live local-model and restart evidence

- Release host: healthy on `http://127.0.0.1:5047/`, existing Ready installation, loopback-only binding.
- Provider/model: credential-free private endpoint `http://192.168.1.89:8000/v1`, `qwen3.8`.
- Conversation `1aaa8c20-0ada-f239-050b-aeab2dacddf1`: turn 1 stored `ORBIT-47`; turn 2 asked for
  the prior code word and returned exactly `ORBIT-47`, proving prior completed history reached the model.
- After terminating and restarting the exact Release listener against the same data directory, the UI reopened snapshot
  v5 with both hash-verified completed turns. No completed task or model turn was repeated.

## Evidence hashes

```text
e143df8e93c3bb5e31fb8209510d85c0ad5e0c2b260bf82604e5bc92c99ed308  RunConversationRecords.cs
641cbd8439b701e912a78698b9d80321b3790c106c49e0e90f93f5e8186a09a1  RunConversationService.cs
338b11c9c3eef54889045750ba72c7abe65c9b8cfb0dcdb8a488466dbf5cf01b  ReadyAdminConversationEndpoints.cs
567f92fb1a53cf5fa87695703ccc95d9fb004775a8842c984da934763cd30423  20260816165307_DurableRunConversations.cs
b2cb3c595ad51e6581d1dbd1fadacf678732e274164cb42a2a7332d039bc3f69  RunConversationPersistenceTests.cs
2ed1f1337735083c3eac57d9f1aa93b3e4c04c14fe7ff8d944b6449b4717d41b  WebSetupWizardTests.cs
```

## Rollback and recovery

Stop the host and preserve the full data tree before downgrade. Migration 0028 can drop only conversation snapshot
metadata; shared immutable artifacts remain for a future verified garbage collector. A crash-orphaned current turn is
not force-completed: wait for its lease to expire, use Resume, and allow the exact task retry to produce a new terminal
receipt. A completed turn is immutable and never resumable. If agent/provider versions changed, start a new
conversation rather than weakening the stored authority pin.
