# AgentForge first-run setup design QA

- Source visual truth: `C:\Users\helmu\AppData\Local\Temp\agentforge-setup-audit-20260812\03-figma-board-final.png`
- Implementation screenshot: `artifacts/gates/POST-R1-SETUP-UX-FINAL-20260812.png`
- Review screenshot: `C:\Users\helmu\AppData\Local\Temp\agentforge-setup-audit-20260812\06-implemented-review.png`
- Mobile screenshot: `C:\Users\helmu\AppData\Local\Temp\agentforge-setup-audit-20260812\07-implemented-mobile.png`
- Combined comparison evidence: `artifacts/gates/POST-R1-SETUP-UX-COMPARISON-FINAL-20260812.png`
- Source pixels: 2440 × 2118 at source density. The source is an experience-audit board rather than a pixel mock, so it defines behavior, hierarchy, and recovery requirements instead of a CSS viewport.
- Implementation pixels: 1265 × 712 from a 1280 × 720 CSS viewport at device pixel ratio 1.
- Mobile viewport: 390 × 844 CSS pixels at device pixel ratio 1; document width was 375 pixels with no horizontal overflow.
- State: fresh protected first-run session, Connect step active, private OpenAI-compatible endpoint prefilled, no credential entered.

**Findings**

- No remaining P0, P1, or P2 difference from the approved audit direction.
- The internal setup bootstrap is absent from the UI. Session creation is automatic, HTTP-only-cookie backed, CSRF protected, and recoverable on refresh.
- The five-step information hierarchy matches the recommended flow: Connect, Choose, Verify, Agent, and Review.
- Live endpoint discovery returned five bounded models. `qwen3.6` was selected and verified through one bounded model probe in 347 ms.
- The review state presents endpoint, model, agent, time zone, authentication mode, and conservative effective-policy summary without exposing security tokens or credentials.

**Required Fidelity Surfaces**

- Fonts and typography: the existing Inter/system stack, weights, hierarchy, line height, and uppercase microcopy remain consistent with the control plane. Labels and supporting copy wrap cleanly at desktop and mobile sizes.
- Spacing and layout rhythm: the setup card follows the existing grid, radii, borders, and vertical rhythm. The desktop split layout and single-column mobile layout have no clipped persistent controls or horizontal overflow.
- Colors and visual tokens: the existing dark neutral, violet focus/action, green protected/success, amber progress, and red error tokens are preserved with readable contrast.
- Image quality and asset fidelity: the setup flow introduces no imagery. Existing brand and control-plane chrome are unchanged; no source imagery was replaced or approximated.
- Copy and content: operator-facing language describes the task and outcome. Internal nonce, CSRF, idempotency, and cookie implementation details are no longer presented as setup data.

**Focused Region Evidence**

- The Connect card was inspected at desktop and mobile sizes because it contains the densest form controls and longest helper copy.
- The Review card was inspected separately to confirm the endpoint and policy summary remain readable and that credentials are represented only as an authentication mode.

**Primary Interactions Tested**

- Automatic protected-session creation.
- Private endpoint model discovery without an API key.
- Model selection and live `qwen3.6` verification.
- Page refresh and active-step resume.
- Back navigation across all five steps.
- Agent naming and review rendering.
- Desktop and mobile reflow.
- Browser console warning/error check: none.
- Durable final completion is covered by the end-to-end setup test; it was not invoked in the visual preview so the user receives a clean first-run instance.

**Comparison History**

- Initial P2: returning from Review to Connect retained the Review instruction in the live status region.
- Fix: back navigation now supplies stage-specific guidance for Connect, Choose, Verify, and Agent.
- Post-fix evidence: the final Connect screenshot shows the correct protected-session status and no stale Review instruction.

**Implementation Checklist**

- [x] Hide internal bootstrap security controls.
- [x] Recover the active setup session after refresh.
- [x] Provide a visible exact model-ID fallback without weakening the verification step.
- [x] Discover and select endpoint models.
- [x] Verify the selected model before persistence.
- [x] Preview agent identity and conservative policy.
- [x] Preserve responsive and accessible form behavior.

**Follow-up Polish**

- P3: a future general administration UI can replace the preview-only sidebar destinations when those surfaces enter scope.

final result: passed
