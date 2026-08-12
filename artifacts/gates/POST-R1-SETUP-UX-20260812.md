# Gate POST-R1-SETUP-UX-20260812

Decision: **Pass**

## Scope

- AF-SET-006, AF-SET-002, AF-SET-003, AF-HOST-003, and AF-SEC-003
- Replace the user-facing one-time nonce with an automatic resumable protected session.
- Discover a bounded model catalog from the configured base endpoint, require exact selection from
  current evidence or an explicit manual-ID fallback, and verify the selected model before durable setup.
- Support deliberate no-auth loopback/private compatible providers without fabricated credentials.
- Align bootstrap `LocalOnly` evaluation with runtime loopback/private routing semantics.
- Deliver and visually verify the five-step Connect, Choose, Verify, Agent, Review journey.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet build AgentForge.slnx --no-restore -c Release` | Pass; 0 warnings, 0 errors |
| `dotnet test AgentForge.slnx --no-build --no-restore -c Release -m:1` | Pass; 398 product tests plus 2 framework-spike tests; 4 named live/equipped skips; 0 failures |
| Focused discovery/setup/provider unit suite | Pass; 21/21 |
| Focused web-setup end-to-end journeys | Pass; 2/2 including catalog and manual-ID paths |
| `dotnet format AgentForge.slnx --no-restore --verify-no-changes` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| In-app browser desktop and mobile flow | Pass; no horizontal overflow and no console warnings/errors |
| Live ASUS catalog | Pass; five models discovered from `http://192.168.1.89:8000/v1/models` |
| Live ASUS verification | Pass; `qwen3.6` returned one compatible bounded probe response in 347 ms |
| Refresh recovery | Pass; active browser resumed at Agent step without nonce re-entry |
| Private-LAN restart recovery | Pass; persisted `Configuring` installation resumed at Agent, completed with `192.168.1.89`, and reached Ready |
| Visual comparison | Pass; `design-qa.md` records no remaining P0/P1/P2 finding |

The four skipped cases remain the existing credential-gated compatible-adapter stream, two live
PostgreSQL cases, and the Docker-equipped container sandbox. This slice executes an independently
authorized bounded setup probe against the operator-provided LAN model and does not broaden runtime
model invocation.

## Security and portability review

Every setup endpoint remains loopback and exact-origin guarded. The one-time grant remains random and
single-use but is never returned to or accepted from the operator. The protected cookie is HttpOnly,
SameSite=Strict, path-scoped, and limited to 30 minutes; the independent CSRF token and request-hash-bound
idempotency key remain mandatory. Mutation execution is serialized and Ready state remains exact-replay-only.

Catalog and verification traffic is isolated behind `IModelCatalogDiscoveryService`. Manual model
entry is explicit session state and still requires exact selection plus the same probe before persistence.
The production
adapter accepts only installed compatible types, derives exact paths from one bounded base endpoint,
allows plaintext only on loopback/private networks, resolves through the existing destination policy,
disables redirects/cookies/proxies/decompression, and bounds time, headers, body, JSON, model count, and
identifier length. Remote bodies and credentials never appear in problem evidence.

`SecretReference.NoCredential` is a typed sentinel, not credential material. Setup validation, minimum-
viability completion, and hosted compatible-adapter construction each restrict it to loopback/private
vLLM or generic OpenAI-compatible profiles. Public endpoints still require HTTPS and an OS-backed secret
reference. No schema or package change is required.

Windows build/test and live UI evidence pass locally. The implementation uses BCL HTTP/JSON primitives and
existing cross-platform destination policy; the complete suites retain Windows/Linux fixture coverage.

## Visual evidence

- Source audit: `C:\Users\helmu\AppData\Local\Temp\agentforge-setup-audit-20260812\03-figma-board-final.png`
- Implemented Connect/manual fallback: `artifacts/gates/POST-R1-SETUP-UX-FINAL-20260812.png`
- Combined comparison: `artifacts/gates/POST-R1-SETUP-UX-COMPARISON-FINAL-20260812.png`
- Implemented Review: `C:\Users\helmu\AppData\Local\Temp\agentforge-setup-audit-20260812\06-implemented-review.png`
- Implemented mobile: `C:\Users\helmu\AppData\Local\Temp\agentforge-setup-audit-20260812\07-implemented-mobile.png`
- Recovered private-LAN completion: `artifacts/gates/POST-R1-SETUP-LAN-RECOVERY-20260812.png`

Evidence SHA-256:

- `src/AgentForge.Host/Setup/WebSetupEndpoints.cs`: `ebd82abf2551fffdd55c788d8cb81091c440c27d32b1739fcc02bbfb99e741be`
- `src/AgentForge.Host/Setup/WebSetupSessionManager.cs`: `fc01779291e6f03798b70bbffc920a7f35ce288c16b1dd3385e3d6781e270731`
- `src/AgentForge.Models/OpenAiCompatibleModelDiscoveryService.cs`: `8d42b29fb47ac80af1f3071823f33a71cc85b0bdbc1d9e54e39a23c01cbaeca5`
- `src/AgentForge.Host/wwwroot/index.html`: `70d32f33e380e3f59e942a7302133c4af709476d904b9bb9485387394d3af831`
- `src/AgentForge.Host/wwwroot/app.js`: `660ae0e6929769c8c2cac8a2e4b2a5fd2e2c0c2a48d6f2df1f29e0a224c1bebe`
- `src/AgentForge.Host/wwwroot/styles.css`: `d85b99d90e00de0c2370010f52acd0a8b18d9e3e1af88a7d1cebc1b8f0b57382`
- Source audit screenshot: `cf8e3d6aaa05ccdb639cd3741b4df98d6c242121a9d1c125a5eb15e66e1390f1`
- Implemented Connect screenshot: `e9258bd3c29b2be03e0c798a84c951681d86a2596122403c1fc15008ac4ff52b`
- Combined visual comparison: `6e17ec23d4ee03f5a9538073c9ae620d83e7060c7b84632c1083617561015ba0`
- Implemented Review screenshot: `c710951da08f726e084c7d5e3a465083a9b933143959a9eb2cb3931111f00f5e`
- Implemented mobile screenshot: `edca1b06e47f1a45d07e3806579ae9d3fec200589557c6eae794265a6e86f62d`
- `src/AgentForge.Setup/ConservativeAgentDefinitionEvaluator.cs`: `ad135df390dcee64e858446d494b7c3d366114e0f857f83827e3ca62b507c27c`
- `tests/AgentForge.UnitTests/AgentDefinitionEvaluatorTests.cs`: `501858801250dc655f5803a2d5371c200bff144f3e9dea13f06de04a586d7e20`
- `tests/AgentForge.EndToEndTests/WebSetupWizardTests.cs`: `f5b71eab763f7e779dc797d43a594fd0c4681944d810bad3fc4a745d43a8524b`
- Recovered private-LAN completion screenshot: `885810abd77b5362374edcf05f072b256e99237f403b4bf282d34d853b1e8cab`

## Rollback

Revert this post-R1 slice as one commit. The prior loopback wizard and CLI data remain schema-compatible.
Existing no-credential profiles created by this slice must be edited to an OS-backed credential or removed
through a verified full-installation restore before reverting, because the earlier setup validator does not
understand the typed sentinel. Stop the host first to invalidate in-memory setup sessions. Do not delete or
rewrite an operator data directory.
