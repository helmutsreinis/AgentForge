# POST-R1 Governed Brave Search Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Integrate Brave Search as an operator-configured core research provider without granting models ambient network
authority. An operator can verify, enable, disable, configure, and rotate the credential from the Context UI; a run
may use the provider only through the existing exact-preview and exact-approval research flow.

Requirement: `AF-ADMIN-009`.

## Upstream contract

- AgentForge calls only Brave's fixed `https://api.search.brave.com/res/v1/web/search` endpoint.
- The credential is materialized for one bounded invocation in the `X-Subscription-Token` header and is then
  cleared. It is never placed in query strings, configuration responses, audit events, logs, or durable profiles.
- Result counts are clamped to Brave's documented maximum of 20. Safe Search, country, and search-language values
  are normalized before use.
- The HTTP adapter rejects redirects, proxy use, cookies, non-HTTPS endpoints, oversized responses, and unbounded
  execution.

## Security, durability, and recovery disposition

- Migration 0031 adds tenant-scoped, versioned `search_provider_profiles`. Durable records contain an opaque
  OS-backed secret reference, not the API key.
- Initial configuration and every rotation require a successful live probe before persistence. Preview binds a
  credential fingerprint; apply repeats the probe and rejects a missing, changed, or stale credential.
- Rotation stages a new protected secret, commits the versioned profile and redacted audit event atomically, then
  removes the superseded reference. Failed staging removes the new reference.
- Disabling a profile removes Brave from provider selection without destroying the protected credential, allowing
  a later verified re-enable. Configuration responses expose neither secret references nor credential fingerprints.
- Research previews bind the exact provider evidence hash. Key rotation, enablement, Safe Search, country,
  language, or profile-version changes invalidate an outstanding approval and partition the search cache.
- Untrusted result content cannot change policy. Only bounded, normalized, citation-bearing research receipts are
  attached to a run after explicit operator approval.

## Verification evidence

```text
dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet build AgentForge.slnx -c Release --no-restore
PASS — 0 warnings, 0 errors

dotnet test AgentForge.slnx -c Release --no-build --no-restore
PASS — 428 product tests + 2 framework-spike tests;
5 expected environment-gated skips (Docker, Brave live, OpenAI-compatible live, PostgreSQL x2)

dotnet test tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj -c Release \
  --filter FullyQualifiedName~SearchServiceTests
PASS — 9 tests

dotnet test tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj -c Release \
  --filter FullyQualifiedName~Brave_search_profile
PASS — durable opaque reference and versioned rotation policy

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Release \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — configuration, rotation, stale approval rejection, and provider catalog

dotnet ef migrations has-pending-model-changes --project src/AgentForge.Persistence \
  --startup-project src/AgentForge.Persistence --configuration Release --no-build
PASS — no pending changes

node --check src/AgentForge.Host/wwwroot/app.js
PASS

./scripts/verify-no-secrets.ps1
PASS — 800 repository candidates

dotnet list AgentForge.slnx package --vulnerable --include-transitive
PASS — no known vulnerable packages
```

## Live and browser evidence

- A credential-gated live test is available through `AGENTFORGE_LIVE_BRAVE_SEARCH_API_KEY`. It is skipped unless an
  operator explicitly supplies a credential to the test process.
- The credential disclosed in chat was not copied into source, commands, environment variables, logs, or tests and
  was not used for the gate. It must be revoked and replaced before operator testing.
- The Release host upgraded the existing Ready installation through migration 0031 and returned Healthy.
- Context UI smoke verification confirmed the write-only key field, enabled control, Safe Search selector, optional
  country and language, configuration status, verification preview, apply action, and empty-key rotation guidance.
  The smoke test did not mutate the current installation.

## Evidence hashes

```text
687413654df05a58793464f189bb67d96bad67aeed771ac93b9808fdee1632b3  BraveSearchProviderConfigurationService.cs
c2f7ed1bed530919a79b7519bb0bf060b7e15b2f4bc8b1a7209663861398f0a9  ManagedBraveSearchProvider.cs
0ccfd3748ab676009335d4846cc52e2fc87f7201a25123d5887f0b0617c16688  20260816202657_BraveSearchProviderProfiles.cs
5ca31b83615bd50763dc78a049de2be67a0357704bd6c84d4e2a860c12902817  ReadyAdminContextEndpoints.cs
f729802b952c8d410b73eb474b3a3e081b9d771bc7ded0fc7ccfa564b7a5e40a  app.js
69bfbc64bb361644b6cd8c1a87d0d313d08125e8b78b5ad178b269cea8e9486d  SearchServiceTests.cs
```

## Rollback and recovery

Prefer the reversible UI disable action first; it removes Brave from all new research previews while retaining the
protected key for a later verified re-enable. Before a schema downgrade, stop the host and back up the complete data
directory. Remove the provider credential through an upgrade-compatible administrative path before applying
migration 0031 down; the down migration drops only search-provider profiles. Research receipts, citations, run
transcripts, and other providers remain intact. If a post-commit old-secret deletion fails, the active profile still
points exclusively to the new reference; the orphaned protected reference can be removed during secret-store
maintenance without changing the durable profile.
