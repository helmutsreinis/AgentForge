# AgentForge Production R1 Security Review

Review date: 2026-08-12  
Release posture: local, single-operator, loopback-only by default  
Decision: **Pass — no unresolved High or Critical finding**

## Review scope

This review covers the R1 source, locked dependency graph, default configuration, REST/SSE and web-setup boundaries, model and MCP egress, tool/process/container execution, secrets, audit/trajectory export, skills, plugins, coding worktrees, channels, devices, recursive learning, persistence, backup/restore, and Windows/Linux packaging. It is an engineering security review backed by the repository's deterministic suites and equipped CI gates; it is not a claim of an independent penetration test.

## Required evidence

| Control | Result |
|---|---|
| Dependency audit | Every solution project reports no known vulnerable direct or transitive package. |
| Secret scan | Repository scan passes; no credential signature is present in tracked or untracked source/evidence. |
| Security regression | 11 deterministic security tests pass, including secret-shaped values, injection, path/link, request-bound approval, and hostile content boundaries. |
| Architecture | Domain/infrastructure and feature-module dependency rules pass. |
| Coverage | 86.59% product; critical thresholds pass at 90.46% policy, 96.9% state machines, 95.65% audit, and 90.91% promotion/rollback. |
| Container isolation | Digest-pinned denied-network Docker adapter has deterministic construction tests and a named real-container release-CI gate. |
| Release defaults | Loopback binding, non-root container, hardened services, checksums, full manifest, SPDX SBOM, and self-contained package smoke are verified. |
| Acceptance | `artifacts/acceptance/R1-scenarios.json` machine-defines all 25 scenarios and rejects missing, failing, or unapproved skipped evidence. |

## Finding disposition

| ID | Severity | Disposition | Evidence or boundary |
|---|---|---|---|
| R1-SUPPLY-001 | High | Resolved | The SQLite native dependency advisory found during M1 was removed by the locked 2.1.12 pin; the final full dependency audit is clean. |
| R1-EXEC-001 | High | Resolved | High-risk/untrusted execution cannot use the restricted host. It requires the digest-pinned container request with denied network, filesystem/resource/process controls, or returns typed `UnsupportedCapability`. |
| R1-PLUGIN-001 | High | Resolved | Production in-process trust requires a configured P-256 public key and exact canonical manifest signature; all other packages route to the constrained worker or fail closed. |
| R1-REMOTE-001 | High | Resolved | Default binding is loopback. Non-loopback startup requires explicit remote mode, HTTPS, authentication, exact origins, rate limits, and network exposure configuration. |
| R1-TOCTOU-001 | Medium | Accepted for R1 | Restricted-host path checks can race a same-user filesystem replacement and cannot claim strong filesystem/network isolation. Its declared capability is limited; higher-risk calls require the container adapter. |
| R1-LOCAL-001 | Medium | Accepted for R1 | A malicious process already running as the same OS user can attack local credentials, files, loopback traffic, or the Docker daemon. R1 is explicitly a single-operator local product and does not claim protection from a fully compromised operator account. |
| R1-LINUX-001 | Low | Accepted | Linux secrets require an available Secret Service session and `secret-tool`; absence is typed and never falls back to plaintext. |
| R1-LIVE-001 | Low | Accepted | Provider/search/channel/PostgreSQL/device live tests are credential, service, or hardware gated. Deterministic protocol/error/security fixtures remain mandatory on every run and live gates do not weaken unavailable integrations. |

## Release conditions

- Keep the default loopback-only posture unless the remote-mode checklist is fully configured.
- Configure the digest-pinned container adapter before enabling any boundary that declares container isolation.
- Treat Docker-daemon access as privileged and admit only reviewed plugin signing keys.
- Restore only backups that pass the complete manifest and file-hash verification.
- Do not convert typed unavailable isolation, secret-store, credential, or hardware results into fallback behavior.

No finding rated High or Critical remains open under the stated R1 boundaries.
