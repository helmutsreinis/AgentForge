# POST-R1 Brave Search UI Consistency Gate — 2026-08-16

Result: **Pass**

## Objective and scope

Bring the Brave Search configuration and exact-review forms onto the same form components, spacing, typography,
control sizing, checkbox treatment, button hierarchy, and responsive grid used throughout the Ready portal. This is
a presentation-only follow-up to `AF-ADMIN-009`; it does not change search authority, credential handling, request
contracts, or persisted data.

## Implementation disposition

- Both Brave forms use the shared `run-composer` and compact composition classes instead of browser-default form
  rendering.
- The enabled control is a bounded portal toggle row with a clear title and supporting explanation.
- The write-only credential remains full width. Safe Search and country use the shared two-column form row, and
  search language remains a full-width field with concise helper text.
- Verification is presented as the primary action with the same explanatory action row used by other governed
  workflows. Existing mobile form-row and composer-action breakpoints stack the controls without a Brave-specific
  competing layout.
- Exact-review grids use shrinkable columns, preview hashes wrap at the panel edge, and action groups may wrap; long
  verification labels, values, and hashes therefore cannot widen the confirmation form beyond the research card.

## Verification evidence

```text
node --check src/AgentForge.Host/wwwroot/app.js
PASS

dotnet format AgentForge.slnx --no-restore --verify-no-changes
PASS

dotnet test tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj -c Debug \
  --filter FullyQualifiedName~Loopback_wizard_hides_bootstrap
PASS — 1 test; host and all referenced projects built successfully

git diff --check
PASS
```

## Browser evidence

- The running Context page loaded the updated static assets without mutating the installation.
- The Brave configuration card rendered the same bordered inputs, uppercase field labels, helper text, checkbox
  treatment, panel surfaces, spacing rhythm, and primary action style as the surrounding portal forms.
- At the active desktop viewport, every control remained inside the research column with no overlap or horizontal
  overflow. The accessibility snapshot retained explicit labels for the enabled control, key, Safe Search, country,
  language, and verification action.
- The confirmation-layout regression is contract-tested through the served stylesheet: shrinkable review tracks and
  the bounded, wrapping preview-hash rule must remain present.

## Evidence hashes

```text
fb095c86f3695ce4beecb277a712e4e69a53014da3bdad87700c3453cc3e1fde  index.html
948e2f6a80eef8dcfe37df7f89ab968b418a6052252feb0721fc4b739c55dbde  styles.css
2c523d5190353a6932119269e959473e9eaf22fd6be6ab6cb3a9a9b80f7ba71b  WebSetupWizardTests.cs
```

## Rollback

Reverting this commit restores the previous Brave form markup and its browser-default rendering. No database,
secret-store, API, audit, or research-result rollback is required.
