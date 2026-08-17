# Gate POST-R1-LAN-REMOTE-20260813

Decision: **Pass**

## Scope

- AF-HOST-002..003, AF-ADMIN-001, AF-SEC-001, and AF-SEC-003.
- Preserve loopback-only administration by default while making explicitly enabled HTTPS LAN access usable.
- Require a bounded temporary remote access code before the server mints its normal protected administrator
  session; never expose or persist the OS-protected administrator credential.
- Retain exact-origin, CSRF, rate-limit, installation-scope, and idempotency enforcement for all remote use.

## Verification evidence

| Command/check | Result |
|---|---|
| `dotnet build AgentForge.slnx --no-restore -c Release` | Pass; 0 warnings, 0 errors |
| Focused `ProductionApiTests` | Pass; 3/3 including insecure binding and missing/short access-code startup denial |
| Focused `WebSetupWizardTests` | Pass; 2/2 complete workspace journeys |
| Complete Release product suites plus framework spike | Pass; 403 product tests plus 2 spike tests; 4 named live/equipped skips; 0 failures |
| `dotnet format AgentForge.slnx --no-restore --verify-no-changes` | Pass |
| `dotnet list AgentForge.slnx package --vulnerable --include-transitive` | Pass; no vulnerable packages |
| `node --check src/AgentForge.Host/wwwroot/app.js` | Pass |
| `git diff --check` | Pass |
| Actual LAN HTTPS root | Pass; `https://192.168.1.100:5443/` returned 200 |
| Remote session without access code | Pass; denied with 403 |
| Remote session with exact access code | Pass; returned 200 and a Secure/HttpOnly/SameSite=Strict cookie |
| Remote agent/model journey | Pass; loaded `local-agent`, invoked `qwen3.6`, returned exact `AGENTFORGE_LAN_OK`, and persisted Completed |
| Access-code persistence scan | Pass; the live code was absent from the repository and installation data directory |

The complete suite was run with the installed .NET 10.0.400 feature band selected temporarily for the nested
MSBuild/Roslyn workspace fixture. The checked-in `global.json` was restored unchanged to 10.0.302. The four
skips remain the Docker-equipped sandbox, credential-gated compatible-provider stream, and two live PostgreSQL
cases.

## Security and portability review

Remote mode remains disabled in checked-in defaults. Enabling it requires a non-loopback HTTPS URL, one or
more exact HTTPS origins, and a 20-256 character access code. The access code is sent only in a dedicated
session-creation header, fixed-time hash compared, held only in process/browser memory, and cleared from the
browser after success. The browser receives the same short-lived Secure, HttpOnly, SameSite=Strict session plus
independent CSRF token used by the existing workspace. Subsequent requests cannot reuse the access code as an
authorization mechanism.

Remote safe reads may omit the `Origin` header only when their scheme/authority exactly matches a configured
origin. Mutations still require an exact configured Origin. Known forwarded headers are limited to one hop and
accepted only from IPv4/IPv6 loopback, supporting a local TLS reverse proxy without allowing network clients to
forge scheme, host, or address. Request size and rate bounds remain unchanged.

The live listener uses a seven-day self-signed certificate with only the exact LAN IP and workstation DNS name
in its SAN for operator testing. Production deployment requires a managed trusted certificate. The host OS has
an existing explicit inbound block for the AgentForge executable; replacing it with a TCP/5443, Private,
LocalSubnet, exact-program allow rule requires administrator elevation and is deliberately not bypassed.

## Evidence SHA-256

- `src/AgentForge.Host/Program.cs`: `69e6351a6da30a7badd008c594cdfe7f83d530b4ee572bf9a417ded7edbb820f`
- `src/AgentForge.Host/Http/HostSecurityOptions.cs`: `fb64a8adcd6c1957b2b0cad01034350b9939deababde254619ee0770a49fc83b`
- `src/AgentForge.Host/Http/RemoteAccessMiddleware.cs`: `63565c8c38a76069329bb4ffd3de1ab364e8da277e163f6c0729406915036e78`
- `src/AgentForge.Host/Http/ReadyAdminEndpoints.cs`: `6a1cbd7dece19b693d49eb9de4132ea23e3ef4d41b0a08cde85aab4ed47f7d02`
- `src/AgentForge.Host/wwwroot/app.js`: `06b60362ea3df994901b2256813f82fbc3b8b37c0a4fd7333b447778f0b600d0`
- `src/AgentForge.Host/wwwroot/index.html`: `abc0e5ad826d22d2851cad9b3f1425ed6455f2ffe520cf967d75d24161cc7097`
- `src/AgentForge.Host/appsettings.json`: `93276ded518422c8063387a90f4fe1343e2008527796493010ab8baf884801ad`
- `tests/AgentForge.IntegrationTests/ProductionApiTests.cs`: `7ab259611140dfb7751aab91985d0f0f8a87bd0c15973dca265ef9b2eb5dd61e`
- Baseline commit: `1c1c959d2a155809fb18733bc246d1bad8ca4210`

## Rollback

Stop the HTTPS host, remove only the exact temporary firewall allow rule if the operator added it, and revert
this slice as one commit. The temporary access code disappears with the process; delete the temporary test
certificate files after shutdown. No schema, protected secret, or durable-data rollback is required. Existing
test runs remain valid hash-only orchestration evidence.
