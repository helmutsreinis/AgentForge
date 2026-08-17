"use strict";

const elements = {
  refresh: document.querySelector("#refresh-button"),
  updated: document.querySelector("#last-updated"),
  statePill: document.querySelector("#state-pill"),
  stateLabel: document.querySelector("#state-label"),
  stateDescription: document.querySelector("#state-description"),
  hostIndicator: document.querySelector("#host-indicator"),
  hostCopy: document.querySelector("#host-copy"),
  readinessIndicator: document.querySelector("#readiness-indicator"),
  readinessCopy: document.querySelector("#readiness-copy"),
  runtimeMode: document.querySelector("#runtime-mode"),
  sandboxIndicator: document.querySelector("#sandbox-indicator"),
  sandboxCopy: document.querySelector("#sandbox-copy"),
  sandboxKind: document.querySelector("#sandbox-kind"),
  setupBadge: document.querySelector("#setup-badge"),
  heroAction: document.querySelector("#hero-action"),
  heroActionLabel: document.querySelector("#hero-action-label"),
  viewKicker: document.querySelector("#view-kicker"),
  viewTitle: document.querySelector("#view-title"),
  setupMessage: document.querySelector("#setup-message"),
  progress: document.querySelector("#setup-progress"),
  providerForm: document.querySelector("#provider-form"),
  modelForm: document.querySelector("#model-form"),
  verifyForm: document.querySelector("#verify-form"),
  agentForm: document.querySelector("#agent-form"),
  reviewForm: document.querySelector("#review-form"),
  complete: document.querySelector("#setup-complete"),
  submit: document.querySelector("#setup-submit"),
  credential: document.querySelector("#provider-credential"),
  model: document.querySelector("#provider-model"),
  modelDetails: document.querySelector("#model-details"),
  manualModelPanel: document.querySelector("#manual-model-panel"),
  manualModel: document.querySelector("#manual-model"),
  modelSummary: document.querySelector("#model-summary"),
  verifySummary: document.querySelector("#verify-summary"),
  reviewSummary: document.querySelector("#review-summary"),
  agentsMessage: document.querySelector("#agents-message"),
  agentList: document.querySelector("#agent-list"),
  providerList: document.querySelector("#provider-list"),
  newProvider: document.querySelector("#new-provider"),
  newAgent: document.querySelector("#new-agent"),
  providerCreatePanel: document.querySelector("#provider-create-panel"),
  providerCreateClose: document.querySelector("#provider-create-close"),
  providerCreateMessage: document.querySelector("#provider-create-message"),
  providerCreateForm: document.querySelector("#provider-create-form"),
  providerCreateName: document.querySelector("#provider-create-name"),
  providerCreateType: document.querySelector("#provider-create-type"),
  providerCreateEndpoint: document.querySelector("#provider-create-endpoint"),
  providerCreateModel: document.querySelector("#provider-create-model"),
  providerCreateCredential: document.querySelector("#provider-create-credential"),
  providerCreateReview: document.querySelector("#provider-create-review"),
  providerCreateWarning: document.querySelector("#provider-create-warning"),
  providerCreateChanges: document.querySelector("#provider-create-changes"),
  providerCreatePreviewHash: document.querySelector("#provider-create-preview-hash"),
  providerCreateCredentialHelp: document.querySelector("#provider-create-credential-help"),
  providerCreateDiscard: document.querySelector("#provider-create-discard"),
  providerCreateApply: document.querySelector("#provider-create-apply"),
  agentEditor: document.querySelector("#agent-editor"),
  agentEditorTitle: document.querySelector("#agent-editor-title"),
  agentEditorContext: document.querySelector("#agent-editor-context"),
  agentEditorClose: document.querySelector("#agent-editor-close"),
  agentEditorMessage: document.querySelector("#agent-editor-message"),
  agentModelForm: document.querySelector("#agent-model-form"),
  agentProviderSummary: document.querySelector("#agent-provider-summary"),
  agentEditModel: document.querySelector("#agent-edit-model"),
  agentDiscoverModels: document.querySelector("#agent-discover-models"),
  agentProfileForm: document.querySelector("#agent-profile-form"),
  agentEditName: document.querySelector("#agent-edit-name"),
  agentEditExpertise: document.querySelector("#agent-edit-expertise"),
  agentEditMission: document.querySelector("#agent-edit-mission"),
  agentEditLanguage: document.querySelector("#agent-edit-language"),
  agentEditTimezone: document.querySelector("#agent-edit-timezone"),
  agentEditStyle: document.querySelector("#agent-edit-style"),
  agentEditMaxOutput: document.querySelector("#agent-edit-max-output"),
  agentEditWorkspace: document.querySelector("#agent-edit-workspace"),
  agentEditProvider: document.querySelector("#agent-edit-provider"),
  agentEditLocality: document.querySelector("#agent-edit-locality"),
  agentEditFallback: document.querySelector("#agent-edit-fallback"),
  agentEditMemoryScope: document.querySelector("#agent-edit-memory-scope"),
  agentEditRetention: document.querySelector("#agent-edit-retention"),
  agentEditNetwork: document.querySelector("#agent-edit-network"),
  agentEditSearch: document.querySelector("#agent-edit-search"),
  agentEditHttpApi: document.querySelector("#agent-edit-http-api"),
  agentEditWorkspaceRead: document.querySelector("#agent-edit-workspace-read"),
  agentEditToolGrants: document.querySelector("#agent-edit-tool-grants"),
  agentEditSkillGrants: document.querySelector("#agent-edit-skill-grants"),
  agentEditMaxTurns: document.querySelector("#agent-edit-max-turns"),
  agentEditMaxTools: document.querySelector("#agent-edit-max-tools"),
  agentEditMaxInput: document.querySelector("#agent-edit-max-input"),
  agentEditContextDiscovered: document.querySelector("#agent-edit-context-discovered"),
  agentEditContextEffective: document.querySelector("#agent-edit-context-effective"),
  agentEditContextOverride: document.querySelector("#agent-edit-context-override"),
  agentEditContextCompression: document.querySelector("#agent-edit-context-compression"),
  agentEditContextThreshold: document.querySelector("#agent-edit-context-threshold"),
  agentEditContextTarget: document.querySelector("#agent-edit-context-target"),
  agentEditContextRecentTurns: document.querySelector("#agent-edit-context-recent-turns"),
  agentEditMaxWall: document.querySelector("#agent-edit-max-wall"),
  agentEditChildDepth: document.querySelector("#agent-edit-child-depth"),
  agentEditChildren: document.querySelector("#agent-edit-children"),
  agentEditChildConcurrency: document.querySelector("#agent-edit-child-concurrency"),
  agentEditChildTokens: document.querySelector("#agent-edit-child-tokens"),
  agentEditLearning: document.querySelector("#agent-edit-learning"),
  agentEditMutableScope: document.querySelector("#agent-edit-mutable-scope"),
  agentProfileSubmit: document.querySelector("#agent-profile-submit"),
  agentEditReview: document.querySelector("#agent-edit-review"),
  agentEditReviewTitle: document.querySelector("#agent-edit-review-title"),
  agentEditReviewWarning: document.querySelector("#agent-edit-review-warning"),
  agentEditChanges: document.querySelector("#agent-edit-changes"),
  agentEditPreviewHash: document.querySelector("#agent-edit-preview-hash"),
  agentEditCancel: document.querySelector("#agent-edit-cancel"),
  agentEditApply: document.querySelector("#agent-edit-apply"),
  runsMessage: document.querySelector("#runs-message"),
  runList: document.querySelector("#run-list"),
  runForm: document.querySelector("#run-form"),
  runName: document.querySelector("#run-name"),
  runAgent: document.querySelector("#run-agent"),
  runDepth: document.querySelector("#run-depth"),
  runTokenLimit: document.querySelector("#run-token-limit"),
  runInstructions: document.querySelector("#run-instructions"),
  runSystemPreview: document.querySelector("#run-system-preview"),
  runSkills: document.querySelector("#run-skills"),
  runModelSummary: document.querySelector("#run-model-summary"),
  runRestrictions: document.querySelector("#run-restrictions"),
  runOutput: document.querySelector("#run-output"),
  runOutputState: document.querySelector("#run-output-state"),
  runOutputText: document.querySelector("#run-output-text"),
  runOutputMeta: document.querySelector("#run-output-meta"),
  runContextMeter: document.querySelector("#run-context-meter"),
  runContextLabel: document.querySelector("#run-context-label"),
  runContextValue: document.querySelector("#run-context-value"),
  runContextProgress: document.querySelector("#run-context-progress"),
  runContextNote: document.querySelector("#run-context-note"),
  cancelInteraction: document.querySelector("#cancel-interaction"),
  runSearchApproval: document.querySelector("#run-search-approval"),
  runToolApprovalTitle: document.querySelector("#run-tool-approval-title"),
  runSearchQuery: document.querySelector("#run-search-query"),
  runSearchPreview: document.querySelector("#run-search-preview"),
  runSearchPreviewHash: document.querySelector("#run-search-preview-hash"),
  runSearchWarning: document.querySelector("#run-search-warning"),
  runSearchDeny: document.querySelector("#run-search-deny"),
  runSearchApprove: document.querySelector("#run-search-approve"),
  runSearchApply: document.querySelector("#run-search-apply"),
  runDetails: document.querySelector("#run-details"),
  runDetailsTitle: document.querySelector("#run-details-title"),
  runDetailsMeta: document.querySelector("#run-details-meta"),
  runTranscript: document.querySelector("#run-transcript"),
  closeRunDetails: document.querySelector("#close-run-details"),
  runContinueForm: document.querySelector("#run-continue-form"),
  runFollowUp: document.querySelector("#run-follow-up"),
  runFollowUpDepth: document.querySelector("#run-follow-up-depth"),
  runFollowUpTokens: document.querySelector("#run-follow-up-tokens"),
  runContinueHelp: document.querySelector("#run-continue-help"),
  continueRun: document.querySelector("#continue-run"),
  resumeRunTurn: document.querySelector("#resume-run-turn"),
  runHistoryCount: document.querySelector("#run-history-count"),
  runSearch: document.querySelector("#run-search"),
  runStateFilter: document.querySelector("#run-state-filter"),
  runPageSize: document.querySelector("#run-page-size"),
  runPagePrevious: document.querySelector("#run-page-previous"),
  runPageNext: document.querySelector("#run-page-next"),
  runPageSummary: document.querySelector("#run-page-summary"),
  runMemoryQuery: document.querySelector("#run-memory-query"),
  runResearchReceipt: document.querySelector("#run-research-receipt"),
  schedulesMessage: document.querySelector("#schedules-message"),
  scheduleList: document.querySelector("#schedule-list"),
  scheduleForm: document.querySelector("#schedule-form"),
  scheduleAgent: document.querySelector("#schedule-agent"),
  scheduleName: document.querySelector("#schedule-name"),
  schedulePrompt: document.querySelector("#schedule-prompt"),
  scheduleInstructions: document.querySelector("#schedule-instructions"),
  scheduleDepth: document.querySelector("#schedule-depth"),
  scheduleTokenLimit: document.querySelector("#schedule-token-limit"),
  scheduleTrigger: document.querySelector("#schedule-trigger"),
  scheduleInterval: document.querySelector("#schedule-interval"),
  scheduleOneShot: document.querySelector("#schedule-one-shot"),
  scheduleCron: document.querySelector("#schedule-cron"),
  scheduleHour: document.querySelector("#schedule-hour"),
  scheduleMinute: document.querySelector("#schedule-minute"),
  scheduleTimezone: document.querySelector("#schedule-timezone"),
  scheduleOverlap: document.querySelector("#schedule-overlap"),
  scheduleMisfire: document.querySelector("#schedule-misfire"),
  scheduleGrace: document.querySelector("#schedule-grace"),
  scheduleCatchup: document.querySelector("#schedule-catchup"),
  scheduleParallel: document.querySelector("#schedule-parallel"),
  scheduleJitter: document.querySelector("#schedule-jitter"),
  scheduleAttempts: document.querySelector("#schedule-attempts"),
  scheduleRetry: document.querySelector("#schedule-retry"),
  scheduleFailureLimit: document.querySelector("#schedule-failure-limit"),
  scheduleReview: document.querySelector("#schedule-review"),
  scheduleReviewClose: document.querySelector("#schedule-review-close"),
  schedulePreviewDetails: document.querySelector("#schedule-preview-details"),
  schedulePreviewHash: document.querySelector("#schedule-preview-hash"),
  scheduleApply: document.querySelector("#schedule-apply"),
  memoryAgent: document.querySelector("#memory-agent"),
  memorySearchForm: document.querySelector("#memory-search-form"),
  memoryQuery: document.querySelector("#memory-query"),
  memoryCreateForm: document.querySelector("#memory-create-form"),
  memoryKind: document.querySelector("#memory-kind"),
  memoryRetention: document.querySelector("#memory-retention"),
  memoryCorrection: document.querySelector("#memory-correction"),
  memoryContent: document.querySelector("#memory-content"),
  memoryMessage: document.querySelector("#memory-message"),
  memoryList: document.querySelector("#memory-list"),
  researchAgent: document.querySelector("#research-agent"),
  researchForm: document.querySelector("#research-form"),
  researchQuery: document.querySelector("#research-query"),
  researchLimit: document.querySelector("#research-limit"),
  researchProviders: document.querySelector("#research-providers"),
  researchReview: document.querySelector("#research-review"),
  researchReviewClose: document.querySelector("#research-review-close"),
  researchPreviewDetails: document.querySelector("#research-preview-details"),
  researchPreviewHash: document.querySelector("#research-preview-hash"),
  researchMessage: document.querySelector("#research-message"),
  researchResults: document.querySelector("#research-results"),
  braveConfigState: document.querySelector("#brave-config-state"),
  braveConfigSummary: document.querySelector("#brave-config-summary"),
  braveConfigForm: document.querySelector("#brave-config-form"),
  braveConfigEnabled: document.querySelector("#brave-config-enabled"),
  braveConfigKey: document.querySelector("#brave-config-key"),
  braveConfigSafe: document.querySelector("#brave-config-safe"),
  braveConfigCountry: document.querySelector("#brave-config-country"),
  braveConfigLanguage: document.querySelector("#brave-config-language"),
  braveConfigReview: document.querySelector("#brave-config-review"),
  braveConfigDetails: document.querySelector("#brave-config-details"),
  braveConfigPreviewHash: document.querySelector("#brave-config-preview-hash"),
  braveConfigDiscard: document.querySelector("#brave-config-discard"),
  braveConfigMessage: document.querySelector("#brave-config-message"),
  skillsMessage: document.querySelector("#skills-message"),
  skillList: document.querySelector("#skill-list"),
  installSeedSkill: document.querySelector("#install-seed-skill"),
  httpApiConfigState: document.querySelector("#http-api-config-state"),
  httpApiConfigSummary: document.querySelector("#http-api-config-summary"),
  httpApiConfigForm: document.querySelector("#http-api-config-form"),
  httpApiConfigEnabled: document.querySelector("#http-api-config-enabled"),
  httpApiProfileId: document.querySelector("#http-api-profile-id"),
  httpApiDisplayName: document.querySelector("#http-api-display-name"),
  httpApiBaseEndpoint: document.querySelector("#http-api-base-endpoint"),
  httpApiProbePath: document.querySelector("#http-api-probe-path"),
  httpApiStaticHeaders: document.querySelector("#http-api-static-headers"),
  httpApiBearerToken: document.querySelector("#http-api-bearer-token"),
  httpApiConfigReview: document.querySelector("#http-api-config-review"),
  httpApiConfigDetails: document.querySelector("#http-api-config-details"),
  httpApiConfigPreviewHash: document.querySelector("#http-api-config-preview-hash"),
  httpApiConfigDiscard: document.querySelector("#http-api-config-discard"),
  httpApiConfigMessage: document.querySelector("#http-api-config-message"),
  skillProposalList: document.querySelector("#skill-proposal-list"),
  skillGateForm: document.querySelector("#skill-gate-form"),
  skillGateTitle: document.querySelector("#skill-gate-title"),
  skillGateSource: document.querySelector("#skill-gate-source"),
  skillGateFields: document.querySelector("#skill-gate-fields"),
  skillGateExplanation: document.querySelector("#skill-gate-explanation"),
  skillGateClose: document.querySelector("#skill-gate-close"),
  submitSkillGate: document.querySelector("#submit-skill-gate"),
  skillGrantForm: document.querySelector("#skill-grant-form"),
  skillGrantTitle: document.querySelector("#skill-grant-title"),
  skillGrantSource: document.querySelector("#skill-grant-source"),
  skillGrantChanges: document.querySelector("#skill-grant-changes"),
  skillGrantWarning: document.querySelector("#skill-grant-warning"),
  skillGrantPreviewHash: document.querySelector("#skill-grant-preview-hash"),
  skillGrantClose: document.querySelector("#skill-grant-close"),
  applySkillGrant: document.querySelector("#apply-skill-grant"),
  toolsMessage: document.querySelector("#tools-message"),
  toolList: document.querySelector("#tool-list"),
  toolGrantForm: document.querySelector("#tool-grant-form"),
  toolGrantTitle: document.querySelector("#tool-grant-title"),
  toolGrantSource: document.querySelector("#tool-grant-source"),
  toolGrantChanges: document.querySelector("#tool-grant-changes"),
  toolGrantWarning: document.querySelector("#tool-grant-warning"),
  toolGrantPreviewHash: document.querySelector("#tool-grant-preview-hash"),
  toolGrantClose: document.querySelector("#tool-grant-close"),
  applyToolGrant: document.querySelector("#apply-tool-grant"),
  toolInvocationForm: document.querySelector("#tool-invocation-form"),
  toolAgent: document.querySelector("#tool-agent"),
  toolSelector: document.querySelector("#tool-selector"),
  toolDisposition: document.querySelector("#tool-disposition"),
  toolWorkspace: document.querySelector("#tool-workspace"),
  toolParameterFields: document.querySelector("#tool-parameter-fields"),
  toolRequestSummary: document.querySelector("#tool-request-summary"),
  previewToolInvocation: document.querySelector("#preview-tool-invocation"),
  toolApprovalForm: document.querySelector("#tool-approval-form"),
  toolApprovalTitle: document.querySelector("#tool-approval-title"),
  toolApprovalSource: document.querySelector("#tool-approval-source"),
  toolApprovalDetails: document.querySelector("#tool-approval-details"),
  toolApprovalWarning: document.querySelector("#tool-approval-warning"),
  toolApprovalPreviewHash: document.querySelector("#tool-approval-preview-hash"),
  toolApprovalClose: document.querySelector("#tool-approval-close"),
  applyToolInvocation: document.querySelector("#apply-tool-invocation"),
  toolOutput: document.querySelector("#tool-output"),
  toolOutputState: document.querySelector("#tool-output-state"),
  toolOutputText: document.querySelector("#tool-output-text"),
  toolOutputMeta: document.querySelector("#tool-output-meta"),
  learningForm: document.querySelector("#learning-form"),
  learningSourceRun: document.querySelector("#learning-source-run"),
  learningKind: document.querySelector("#learning-kind"),
  learningOccurrences: document.querySelector("#learning-occurrences"),
  learningSummary: document.querySelector("#learning-summary"),
  captureLearning: document.querySelector("#capture-learning"),
  learningMessage: document.querySelector("#learning-message"),
  learningList: document.querySelector("#learning-list"),
  learningProposalForm: document.querySelector("#learning-proposal-form"),
  learningProposalTitle: document.querySelector("#learning-proposal-title"),
  learningProposalSource: document.querySelector("#learning-proposal-source"),
  learningProposalSkillId: document.querySelector("#learning-proposal-skill-id"),
  learningProposalVersion: document.querySelector("#learning-proposal-version"),
  learningProposalDescription: document.querySelector("#learning-proposal-description"),
  learningProposalGuidance: document.querySelector("#learning-proposal-guidance"),
  learningProposalPermissions: document.querySelector("#learning-proposal-permissions"),
  learningProposalHttpApi: document.querySelector("#learning-proposal-http-api"),
  learningProposalClose: document.querySelector("#learning-proposal-close"),
  createLearningProposal: document.querySelector("#create-learning-proposal"),
  learningCandidateList: document.querySelector("#learning-candidate-list"),
  learningGateForm: document.querySelector("#learning-gate-form"),
  learningGateTitle: document.querySelector("#learning-gate-title"),
  learningGateSource: document.querySelector("#learning-gate-source"),
  learningGateFields: document.querySelector("#learning-gate-fields"),
  learningGateExplanation: document.querySelector("#learning-gate-explanation"),
  learningGateClose: document.querySelector("#learning-gate-close"),
  submitLearningGate: document.querySelector("#submit-learning-gate"),
  accessModeTitle: document.querySelector("#access-mode-title"),
  accessModeDetail: document.querySelector("#access-mode-detail"),
};

const admin = {
  csrfToken: null,
  installationId: null,
  actorId: null,
  remoteAccessCode: "",
  agents: [],
  providers: [],
  providerInstallationVersion: null,
  providerCreatePreview: null,
  agentEdit: null,
  agentEditMode: "edit",
  agentEditPreview: null,
  runs: [],
  runOptions: null,
  skillRegistry: null,
  selectedSkillProposal: null,
  skillGateAction: null,
  skillGrantPreview: null,
  toolCatalog: null,
  toolGrantPreview: null,
  toolInvocationPreview: null,
  runPage: 1,
  runPageSize: 8,
  pendingLearningTaskId: null,
  learningSignals: [],
  learningCandidates: [],
  selectedLearningSignal: null,
  selectedLearningCandidate: null,
  learningGateAction: null,
  lastLearningEvaluation: null,
  lastLearningGeneration: null,
  activeTaskId: null,
  selectedRunDetails: null,
  schedules: [],
  schedulePreview: null,
  scheduleRequest: null,
  memories: [],
  researchProviders: [],
  researchPreview: null,
  researchReceipt: null,
  braveConfiguration: null,
  braveConfigurationPreview: null,
  httpApiProfiles: [],
  httpApiConfigurationPreview: null,
  pendingSearchApproval: null,
};
const isLoopbackBrowser = ["localhost", "127.0.0.1", "::1", "[::1]"].includes(window.location.hostname);
if (!isLoopbackBrowser) {
  elements.accessModeTitle.textContent = "Protected LAN control";
  elements.accessModeDetail.textContent = "HTTPS session · private model route";
}

const setup = {
  csrfToken: null,
  begun: false,
  providerConfigured: false,
  modelTested: false,
  agentCreated: false,
  currentStage: 1,
  models: [],
  provider: {
    name: "primary",
    providerType: "openai-compatible",
    endpoint: sessionStorage.getItem("agentforge.setup.endpoint") || "http://127.0.0.1:8000/v1",
    model: null,
  },
  agent: { name: "local-agent", timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC" },
};

async function readJson(path, options = {}) {
  const response = await fetch(path, {
    method: options.method || "GET",
    credentials: "same-origin",
    headers: { Accept: "application/json", ...(options.headers || {}) },
    body: options.body,
  });
  const payload = await response.json();
  return { ok: response.ok, status: response.status, payload };
}

function setIndicator(element, state, label) {
  element.className = `mini-state ${state}`;
  element.textContent = label;
}

function renderInstallation(result) {
  const state = result.payload.installationState ?? "Unknown";
  const ready = result.payload.ready === true;
  elements.setupBadge.hidden = ready;
  elements.heroAction.href = ready ? "#agents" : "#setup";
  elements.heroActionLabel.textContent = ready ? "Open agent workspace" : "View setup guide";
  elements.stateLabel.textContent = ready ? "Installation ready" : `${state} · Setup required`;
  elements.statePill.classList.toggle("ready", ready);
  elements.stateDescription.textContent = ready
    ? "The durable installation is ready. Runtime operations remain behind authentication and policy."
    : "Connect a provider, choose a model, and create your first local agent to unlock authenticated runtime operations.";
  elements.runtimeMode.textContent = ready ? "Authenticated" : "Setup only";
}

function workspaceStatus(element, message, state = "") {
  element.textContent = message;
  element.className = `workspace-message ${state}`;
}

async function ensureAdminSession() {
  let result = await readJson("/api/v1/admin/session");
  if (!result.ok) {
    if (!isLoopbackBrowser && !admin.remoteAccessCode) {
      admin.remoteAccessCode = window.prompt("Enter the temporary AgentForge LAN access code shown on the host PC:")?.trim() ?? "";
      if (!admin.remoteAccessCode) throw new Error("A temporary LAN access code is required.");
    }
    result = await readJson("/api/v1/admin/session", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Origin: window.location.origin,
        ...(admin.remoteAccessCode ? { "X-AgentForge-Remote-Access-Code": admin.remoteAccessCode } : {}),
      },
      body: "{}",
    });
  }
  if (!result.ok) throw new Error(result.payload.detail ?? "The local operator session could not be opened.");
  admin.remoteAccessCode = "";
  admin.csrfToken = result.payload.csrfToken;
  admin.installationId = result.payload.installationId;
  admin.actorId = result.payload.actorId;
  return result.payload;
}

async function adminRead(path) {
  await ensureAdminSession();
  const result = await readJson(path);
  if (!result.ok) throw new Error(result.payload.detail ?? "The operator request failed.");
  return result.payload;
}

async function adminMutation(path, payload = {}) {
  await ensureAdminSession();
  const result = await readJson(path, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": crypto.randomUUID(),
      "X-CSRF-Token": admin.csrfToken,
      Origin: window.location.origin,
    },
    body: JSON.stringify(payload),
  });
  if (!result.ok) throw new Error(result.payload.detail ?? "The operator mutation failed.");
  return result.payload;
}

async function adminStreamMutation(path, payload, onEvent) {
  await ensureAdminSession();
  const response = await fetch(path, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "text/event-stream",
      "Content-Type": "application/json",
      "Idempotency-Key": crypto.randomUUID(),
      "X-CSRF-Token": admin.csrfToken,
      Origin: window.location.origin,
    },
    body: JSON.stringify(payload),
  });
  if (!response.ok) {
    const problem = await response.json();
    throw new Error(problem.detail ?? "The streaming interaction could not start.");
  }
  if (!response.body) throw new Error("This browser did not expose the response stream.");

  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8", { fatal: true });
  let buffer = "";
  try {
    while (true) {
      const { value, done } = await reader.read();
      buffer += decoder.decode(value || new Uint8Array(), { stream: !done }).replaceAll("\r\n", "\n");
      let boundary = buffer.indexOf("\n\n");
      while (boundary >= 0) {
        const block = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        const eventName = block.split("\n").find(line => line.startsWith("event: "))?.slice(7);
        const data = block.split("\n").filter(line => line.startsWith("data: ")).map(line => line.slice(6)).join("\n");
        if (eventName && data) await onEvent(eventName, JSON.parse(data));
        boundary = buffer.indexOf("\n\n");
      }
      if (done) break;
    }
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally {
    reader.releaseLock();
  }
}

function makeElement(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function addMeta(container, label, value) {
  const item = document.createElement("div");
  item.append(makeElement("span", "", label), makeElement("strong", "", String(value)));
  container.append(item);
}

function stateChip(state) {
  return makeElement("span", `state-chip ${String(state).toLowerCase()}`, state);
}

async function loadAgents(message = "Loading persisted agent identities…") {
  workspaceStatus(elements.agentsMessage, message);
  try {
    const agentSelectors = [elements.runAgent, elements.scheduleAgent, elements.memoryAgent, elements.researchAgent];
    const selectedAgentIds = new Map(agentSelectors.map(select => [select, select.value]));
    const [payload, providerPayload] = await Promise.all([
      adminRead("/api/v1/admin/agents"),
      adminRead("/api/v1/admin/providers"),
    ]);
    admin.agents = payload.agents;
    admin.providers = providerPayload.providers;
    admin.providerInstallationVersion = providerPayload.installationVersion;
    elements.agentList.replaceChildren();
    elements.providerList.replaceChildren();
    for (const select of agentSelectors) select.replaceChildren();
    for (const provider of admin.providers) {
      const card = makeElement("article", "resource-card compact-card");
      const title = makeElement("div", "resource-title");
      const copy = document.createElement("div");
      copy.append(
        makeElement("h3", "", provider.name),
        makeElement("p", "resource-subtitle", `${provider.providerType} · v${provider.version}`));
      title.append(copy, stateChip("Ready"));
      const meta = makeElement("div", "resource-meta");
      addMeta(meta, "Model", provider.model);
      addMeta(meta, "Authentication", provider.authentication);
      addMeta(meta, "Agents", provider.sharedBy.length);
      card.append(title, makeElement("p", "resource-description", provider.endpoint), meta);
      elements.providerList.append(card);
    }
    for (const agent of payload.agents) {
      for (const select of agentSelectors) {
        const option = document.createElement("option");
        option.value = agent.id;
        option.textContent = `${agent.name} · policy v${agent.version}`;
        select.append(option);
      }

      const card = makeElement("article", "resource-card");
      const title = makeElement("div", "resource-title");
      const titleCopy = document.createElement("div");
      titleCopy.append(
        makeElement("h3", "", agent.name),
        makeElement("p", "resource-subtitle", `${agent.id} · version ${agent.version}`));
      title.append(titleCopy, stateChip("Ready"));
      const description = makeElement("p", "resource-description", agent.mission || agent.expertise || "No mission recorded.");
      const meta = makeElement("div", "resource-meta");
      addMeta(meta, "Data locality", agent.dataLocality);
      addMeta(meta, "Tool network", `${agent.networkPosture} · model route separate`);
      addMeta(meta, "Tool budget", agent.budget.maxToolInvocations);
      addMeta(meta, "Input / output", `${agent.budget.maxInputTokens.toLocaleString()} / ${agent.budget.maxOutputTokens.toLocaleString()}`);
      addMeta(meta, "Context window", `${agent.budget.effectiveContextWindowTokens.toLocaleString()} · ${agent.budget.contextWindowSource}`);
      addMeta(meta, "Memory", `${agent.memoryScope} · ${agent.retentionDays} days`);
      addMeta(meta, "Learning", `${agent.learningMode} · ${agent.mutableSkillScope}`);
      const actions = makeElement("div", "resource-actions");
      const edit = makeElement("button", "secondary-action", "Edit agent");
      edit.type = "button";
      edit.addEventListener("click", () => openAgentEditor(agent.id));
      actions.append(edit);
      card.append(title, description, meta, actions);
      elements.agentList.append(card);
    }
    for (const select of agentSelectors) {
      const previous = selectedAgentIds.get(select);
      if (payload.agents.some(agent => agent.id === previous)) select.value = previous;
    }
    if (!payload.agents.length) elements.agentList.append(makeElement("div", "empty-state", "No agents are configured."));
    elements.newAgent.disabled = admin.providers.length === 0;
    workspaceStatus(elements.agentsMessage,
      `${payload.agents.length} ${payload.agents.length === 1 ? "agent" : "agents"} and ${admin.providers.length} ${admin.providers.length === 1 ? "provider" : "providers"} loaded from durable state.`, "ok");
    return payload.agents;
  } catch (error) {
    workspaceStatus(elements.agentsMessage, error instanceof Error ? error.message : "Agents could not be loaded.", "error");
    return [];
  }
}

function discardProviderCreatePreview() {
  admin.providerCreatePreview = null;
  elements.providerCreateReview.hidden = true;
  elements.providerCreateChanges.replaceChildren();
  elements.providerCreatePreviewHash.textContent = "";
  elements.providerCreateCredentialHelp.textContent = "";
}

function openProviderCreate() {
  closeAgentEditor();
  discardProviderCreatePreview();
  const seed = admin.providers[0];
  elements.providerCreateName.value = "";
  elements.providerCreateType.value = seed?.providerType ?? "openai-compatible";
  elements.providerCreateEndpoint.value = seed?.endpoint ?? "http://127.0.0.1:8000/v1";
  elements.providerCreateModel.value = seed?.model ?? "";
  elements.providerCreateCredential.value = "";
  elements.providerCreatePanel.hidden = false;
  workspaceStatus(elements.providerCreateMessage,
    "Enter a distinct connection. Verification creates no provider and grants no agent authority.");
  elements.providerCreatePanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function closeProviderCreate() {
  elements.providerCreateCredential.value = "";
  discardProviderCreatePreview();
  elements.providerCreatePanel.hidden = true;
}

async function previewProviderCreate(event) {
  event.preventDefault();
  setBusy(elements.providerCreateForm, true);
  discardProviderCreatePreview();
  workspaceStatus(elements.providerCreateMessage, "Live-probing the exact endpoint and model…");
  const credential = elements.providerCreateCredential.value;
  try {
    const payload = await adminMutation("/api/v1/admin/providers/create/preview", {
      expectedInstallationVersion: admin.providerInstallationVersion,
      name: elements.providerCreateName.value.trim(),
      providerType: elements.providerCreateType.value,
      endpoint: elements.providerCreateEndpoint.value.trim(),
      model: elements.providerCreateModel.value.trim(),
      credential: credential || null,
    });
    admin.providerCreatePreview = { hash: payload.previewHash, usesCredential: credential.length > 0 };
    elements.providerCreateWarning.textContent = payload.warning;
    renderChanges(elements.providerCreateChanges, payload.changes);
    elements.providerCreatePreviewHash.textContent = `Bound preview ${payload.previewHash}`;
    elements.providerCreateCredential.value = "";
    elements.providerCreateCredentialHelp.textContent = credential
      ? "Credential cleared. Re-enter the same API key immediately before Create; a different key will fail the bound preview."
      : "This private provider was verified without an API key.";
    elements.providerCreateReview.hidden = false;
    workspaceStatus(elements.providerCreateMessage,
      `Connection verified in ${Math.round(payload.verification.durationMilliseconds)} ms.`, "ok");
  } catch (error) {
    elements.providerCreateCredential.value = "";
    workspaceStatus(elements.providerCreateMessage,
      error instanceof Error ? error.message : "Provider verification failed.", "error");
  } finally {
    setBusy(elements.providerCreateForm, false);
  }
}

async function applyProviderCreate() {
  if (!admin.providerCreatePreview) return;
  const credential = elements.providerCreateCredential.value;
  if (admin.providerCreatePreview.usesCredential && !credential) {
    workspaceStatus(elements.providerCreateMessage,
      "Re-enter the same API key before creating this provider.", "error");
    elements.providerCreateCredential.focus();
    return;
  }
  elements.providerCreateApply.disabled = true;
  try {
    await adminMutation("/api/v1/admin/providers/create/apply", {
      previewHash: admin.providerCreatePreview.hash,
      credential: credential || null,
    });
    elements.providerCreateCredential.value = "";
    closeProviderCreate();
    await loadAgents("Refreshing providers and agents…");
    workspaceStatus(elements.agentsMessage, "Provider created, validated, and audited. It has no agent authority yet.", "ok");
  } catch (error) {
    elements.providerCreateCredential.value = "";
    workspaceStatus(elements.providerCreateMessage,
      error instanceof Error ? error.message : "Provider creation failed.", "error");
  } finally {
    elements.providerCreateApply.disabled = false;
  }
}

function renderChanges(container, changes) {
  container.replaceChildren();
  for (const change of changes) {
    const item = document.createElement("div");
    item.append(
      makeElement("span", "", change.path),
      makeElement("strong", "", `${formatPolicyChange(change.path, change.before)} → ${formatPolicyChange(change.path, change.after)}`));
    container.append(item);
  }
  if (!changes.length) container.append(makeElement("div", "", "No effective changes."));
}

function formatPolicyChange(path, value) {
  if (value === null || value === undefined || value === "") return "Not set";
  try {
    const parsed = typeof value === "object"
      ? value
      : String(value).startsWith("{") || String(value).startsWith("[")
        ? JSON.parse(value)
        : null;
    if (parsed === null) return String(value);
    if (path === "agent.modelPolicy") {
      const locality = ["LocalOnly", "CloudAllowed"][parsed.dataLocality] ?? parsed.dataLocality;
      return `provider ${parsed.primaryProviderProfileId?.value ?? "unknown"} · ${locality} · fallback ${parsed.allowFallback ? "allowed" : "denied"}`;
    }
    if (path === "agent.memoryPolicy") {
      return `${["Task", "Agent", "Operator"][parsed.scope] ?? parsed.scope} · ${parsed.retentionDays} days`;
    }
    if (path === "agent.capabilityPolicy") {
      const network = ["Denied", "LoopbackOnly"][parsed.networkPosture] ?? parsed.networkPosture;
      return `${network} · tools ${(parsed.toolGrants ?? []).join(", ") || "none"} · skills ${(parsed.skillGrants ?? []).join(", ") || "none"}`;
    }
    if (path === "agent.budget") {
      return `${parsed.maxTurns} turns · ${parsed.maxToolInvocations} tools · ${parsed.maxInputTokens}/${parsed.maxOutputTokens} tokens · ${parsed.maxWallClockSeconds}s`;
    }
    if (path === "agent.childLimits") {
      return `${parsed.maxChildren} children · depth ${parsed.maxDepth} · concurrency ${parsed.maxConcurrency} · ${parsed.maxTotalTokens} tokens`;
    }
    if (path === "agent.learningPolicy") {
      const mode = ["Off", "Observe", "Propose", "ScopedAuto"][parsed.mode] ?? parsed.mode;
      const scope = ["None", "ProposalWorkspaceOnly", "ApprovedSkillClasses"][parsed.mutableSkillScope] ?? parsed.mutableSkillScope;
      return `${mode} · ${scope}`;
    }
    return JSON.stringify(parsed);
  } catch {
    return String(value);
  }
}

function setAgentEditorStatus(message, state = "") {
  workspaceStatus(elements.agentEditorMessage, message, state);
}

function populateAgentModelOptions(models, selected) {
  elements.agentEditModel.replaceChildren();
  const ids = new Set(models.map(model => model.id));
  if (selected && !ids.has(selected)) models = [{ id: selected, ownedBy: "configured" }, ...models];
  for (const model of models) {
    const option = document.createElement("option");
    option.value = model.id;
    option.dataset.maximumContextTokens = model.maximumContextTokens || "";
    const owner = model.ownedBy ? ` · ${model.ownedBy}` : "";
    const context = model.maximumContextTokens
      ? ` · ${model.maximumContextTokens.toLocaleString()} context`
      : "";
    option.textContent = `${model.id}${owner}${context}`;
    elements.agentEditModel.append(option);
  }
  elements.agentEditModel.value = selected;
}

function setDiscoveredContext(tokens, model) {
  const value = Number(tokens) > 0 ? Number(tokens) : null;
  elements.agentEditContextDiscovered.dataset.tokens = value ? String(value) : "";
  elements.agentEditContextDiscovered.dataset.model = model || "";
  elements.agentEditContextDiscovered.textContent = value
    ? `${value.toLocaleString()} tokens · ${model || "selected model"}`
    : "Not exposed by this endpoint";
  refreshEffectiveContext();
}

function refreshEffectiveContext() {
  const discovered = Number(elements.agentEditContextDiscovered.dataset.tokens) || null;
  const override = elements.agentEditContextOverride.value
    ? Number(elements.agentEditContextOverride.value)
    : null;
  const fallback = (Number(elements.agentEditMaxInput.value) || 0) +
    (Number(elements.agentEditMaxOutput.value) || 0);
  const effective = override || discovered || fallback;
  const invalid = discovered && override && override > discovered;
  elements.agentEditContextOverride.setCustomValidity(invalid
    ? `The override cannot exceed the discovered ${discovered.toLocaleString()}-token ceiling.`
    : "");
  const source = override ? "operator override" : discovered ? "endpoint discovery" : "combined input/output fallback";
  elements.agentEditContextEffective.textContent = effective
    ? `Effective context: ${effective.toLocaleString()} tokens · ${source}`
    : "Effective context is unavailable";
}

function discardAgentEditPreview() {
  admin.agentEditPreview = null;
  elements.agentEditReview.hidden = true;
  elements.agentEditChanges.replaceChildren();
  elements.agentEditPreviewHash.textContent = "";
}

function closeAgentEditor() {
  admin.agentEdit = null;
  admin.agentEditMode = "edit";
  discardAgentEditPreview();
  elements.agentEditor.hidden = true;
}

function populateProviderOptions(providers, selectedId) {
  const selectedValue = typeof selectedId === "object" && selectedId !== null
    ? selectedId.value
    : selectedId;
  elements.agentEditProvider.replaceChildren();
  for (const provider of providers) {
    const option = document.createElement("option");
    option.value = provider.id;
    option.dataset.version = provider.version;
    option.textContent = `${provider.name} · ${provider.model} · v${provider.version}`;
    elements.agentEditProvider.append(option);
  }
  elements.agentEditProvider.value = selectedValue ?? providers[0]?.id ?? "";
}

function writeGrantList(element, grants) {
  element.value = (grants ?? []).join("\n");
}

function readGrantList(element) {
  return element.value.split(/\r?\n|,/).map(value => value.trim()).filter(Boolean);
}

function setAgentGrant(capabilityId, enabled) {
  const grants = new Set(readGrantList(elements.agentEditToolGrants));
  if (enabled) grants.add(capabilityId);
  else grants.delete(capabilityId);
  writeGrantList(elements.agentEditToolGrants, [...grants].sort());
}

function syncFriendlyCapabilitiesFromPolicy() {
  const grants = readGrantList(elements.agentEditToolGrants);
  elements.agentEditSearch.checked = grants.includes("tool:search.web");
  elements.agentEditHttpApi.checked = grants.includes("tool:http-api.read");
  elements.agentEditWorkspaceRead.checked = grants.includes("tool:workspace.read");
}

function updateFriendlyCapability(capabilityId, enabled) {
  setAgentGrant(capabilityId, enabled);
  const grants = readGrantList(elements.agentEditToolGrants);
  if (capabilityId === "tool:search.web" || capabilityId === "tool:http-api.read") {
    if (enabled) elements.agentEditNetwork.value = "ApprovedEndpointsOnly";
    else if (elements.agentEditNetwork.value === "ApprovedEndpointsOnly" &&
      !grants.includes("tool:search.web") && !grants.includes("tool:http-api.read")) {
      elements.agentEditNetwork.value = "Denied";
    }
  }
  if (grants.length === 0) elements.agentEditMaxTools.value = 0;
  else if (Number(elements.agentEditMaxTools.value) === 0) elements.agentEditMaxTools.value = 5;
}

function populateCompletePolicy(agent, providers) {
  populateProviderOptions(providers, agent.modelPolicy.primaryProviderProfileId);
  elements.agentEditLocality.value = agent.modelPolicy.dataLocality;
  elements.agentEditFallback.checked = agent.modelPolicy.allowFallback;
  elements.agentEditMemoryScope.value = agent.memoryPolicy.scope;
  elements.agentEditRetention.value = agent.memoryPolicy.retentionDays;
  elements.agentEditNetwork.value = agent.capabilityPolicy.networkPosture;
  writeGrantList(elements.agentEditToolGrants, agent.capabilityPolicy.toolGrants);
  syncFriendlyCapabilitiesFromPolicy();
  writeGrantList(elements.agentEditSkillGrants, agent.capabilityPolicy.skillGrants);
  elements.agentEditMaxTurns.value = agent.budget.maxTurns;
  elements.agentEditMaxTools.value = agent.budget.maxToolInvocations;
  elements.agentEditMaxInput.value = agent.budget.maxInputTokens;
  elements.agentEditMaxOutput.value = agent.budget.maxOutputTokens;
  elements.agentEditMaxWall.value = agent.budget.maxWallClockSeconds;
  setDiscoveredContext(agent.budget.discoveredContextWindowTokens, agent.budget.discoveredContextModel);
  elements.agentEditContextOverride.value = agent.budget.contextWindowOverrideTokens ?? "";
  elements.agentEditContextCompression.checked = agent.budget.contextCompressionEnabled ?? true;
  elements.agentEditContextThreshold.value = agent.budget.contextCompressionThresholdPercent ?? 80;
  elements.agentEditContextTarget.value = agent.budget.contextCompressionTargetPercent ?? 50;
  elements.agentEditContextRecentTurns.value = agent.budget.contextProtectedRecentTurns ?? 4;
  refreshEffectiveContext();
  elements.agentEditChildDepth.value = agent.childLimits.maxDepth;
  elements.agentEditChildren.value = agent.childLimits.maxChildren;
  elements.agentEditChildConcurrency.value = agent.childLimits.maxConcurrency;
  elements.agentEditChildTokens.value = agent.childLimits.maxTotalTokens;
  elements.agentEditLearning.value = agent.learningPolicy.mode;
  elements.agentEditMutableScope.value = agent.learningPolicy.mutableSkillScope;
}

function openAgentCreate() {
  if (!admin.providers.length) return;
  closeProviderCreate();
  admin.agentEditMode = "create";
  admin.agentEdit = { installationVersion: admin.providerInstallationVersion, providers: admin.providers, agent: null };
  discardAgentEditPreview();
  elements.agentEditor.hidden = false;
  elements.agentModelForm.hidden = true;
  elements.agentEditorTitle.textContent = "Create agent";
  elements.agentEditorContext.textContent = `Installation v${admin.providerInstallationVersion} · no inherited authority`;
  elements.agentEditName.value = "";
  elements.agentEditExpertise.value = "";
  elements.agentEditMission.value = "";
  elements.agentEditLanguage.value = "en";
  elements.agentEditTimezone.value = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  elements.agentEditStyle.value = "concise and evidence-backed";
  elements.agentEditWorkspace.value = "";
  populateCompletePolicy({
    modelPolicy: { primaryProviderProfileId: admin.providers[0].id, dataLocality: "LocalOnly", allowFallback: false },
    memoryPolicy: { scope: "Agent", retentionDays: 30 },
    capabilityPolicy: { networkPosture: "Denied", toolGrants: [], skillGrants: [] },
    budget: {
      maxTurns: 64,
      maxToolInvocations: 0,
      maxInputTokens: 16000,
      maxOutputTokens: 32768,
      maxWallClockSeconds: 270,
      discoveredContextWindowTokens: null,
      discoveredContextModel: null,
      contextWindowOverrideTokens: null,
      contextCompressionEnabled: true,
      contextCompressionThresholdPercent: 80,
      contextCompressionTargetPercent: 50,
      contextProtectedRecentTurns: 4,
    },
    childLimits: { maxDepth: 0, maxChildren: 0, maxConcurrency: 0, maxTotalTokens: 0 },
    learningPolicy: { mode: "Propose", mutableSkillScope: "ProposalWorkspaceOnly" },
  }, admin.providers);
  elements.agentProfileSubmit.textContent = "Preview new agent";
  setAgentEditorStatus("Define the complete starting policy. Nothing is persisted until the exact preview is applied.");
  elements.agentEditor.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function openAgentEditor(agentId) {
  closeProviderCreate();
  admin.agentEditMode = "edit";
  elements.agentEditor.hidden = false;
  elements.agentModelForm.hidden = false;
  discardAgentEditPreview();
  setAgentEditorStatus("Loading the versioned agent profile…");
  try {
    const payload = await adminRead(`/api/v1/admin/agents/${agentId}/edit`);
    admin.agentEdit = payload;
    elements.agentEditorTitle.textContent = `Edit ${payload.agent.name}`;
    elements.agentEditorContext.textContent = `Agent v${payload.agent.version} · installation v${payload.installationVersion}`;
    elements.agentProviderSummary.replaceChildren(
      makeElement("strong", "", `${payload.provider.name} · ${payload.provider.model}`),
      makeElement("span", "", payload.provider.endpoint),
      makeElement("span", "", payload.provider.sharedBy.length > 1
        ? `Shared by ${payload.provider.sharedBy.map(agent => agent.name).join(", ")}`
        : "Pinned only to this agent"));
    populateAgentModelOptions([], payload.provider.model);
    elements.agentEditName.value = payload.agent.name;
    elements.agentEditExpertise.value = payload.agent.expertise ?? "";
    elements.agentEditMission.value = payload.agent.mission ?? "";
    elements.agentEditLanguage.value = payload.agent.preferredLanguage;
    elements.agentEditTimezone.value = payload.agent.timeZone;
    elements.agentEditStyle.value = payload.agent.responseStyle;
    elements.agentEditWorkspace.value = payload.agent.defaultWorkspace ?? "";
    populateCompletePolicy(payload.agent, payload.providers);
    elements.agentProfileSubmit.textContent = "Review and save changes";
    setAgentEditorStatus("Agent loaded. Fetching the endpoint's current model and context catalog…", "ok");
    elements.agentEditor.scrollIntoView({ behavior: "smooth", block: "start" });
    await discoverAgentModels(true);
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "The agent editor could not be opened.", "error");
  }
}

async function discoverAgentModels(automatic = false) {
  if (!admin.agentEdit) return;
  setBusy(elements.agentModelForm, true);
  setAgentEditorStatus("Discovering models from the pinned endpoint…");
  try {
    const payload = await adminMutation(
      `/api/v1/admin/agents/${admin.agentEdit.agent.id}/models/discover`, {});
    populateAgentModelOptions(payload.models, admin.agentEdit.provider.model);
    const selected = payload.models.find(model => model.id === payload.selectedModel);
    setDiscoveredContext(selected?.maximumContextTokens, selected?.id || payload.selectedModel);
    setAgentEditorStatus(`${payload.models.length} model ${payload.models.length === 1 ? "identifier" : "identifiers"} discovered safely${selected?.maximumContextTokens ? `; ${selected.maximumContextTokens.toLocaleString()} context tokens reported.` : "."}`, "ok");
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "Model discovery failed.", automatic ? "" : "error");
  } finally {
    setBusy(elements.agentModelForm, false);
  }
}

function renderAgentEditPreview(kind, payload) {
  admin.agentEditPreview = { kind, hash: payload.previewHash };
  elements.agentEditReviewTitle.textContent = kind === "model"
    ? "Review model update"
    : kind === "create" ? "Review new agent" : "Review complete policy";
  elements.agentEditReviewWarning.textContent = payload.warning ||
    "Only the displayed exact policy will be committed.";
  renderChanges(elements.agentEditChanges, payload.changes);
  elements.agentEditPreviewHash.textContent = `Bound preview ${payload.previewHash}`;
  elements.agentEditReview.hidden = false;
  elements.agentEditReview.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

async function previewAgentModel(event) {
  event.preventDefault();
  if (!admin.agentEdit) return;
  setBusy(elements.agentModelForm, true);
  discardAgentEditPreview();
  setAgentEditorStatus("Live-probing the selected model and preparing an exact change preview…");
  try {
    const payload = await adminMutation(
      `/api/v1/admin/agents/${admin.agentEdit.agent.id}/model/preview`, {
        expectedInstallationVersion: admin.agentEdit.installationVersion,
        expectedProviderVersion: admin.agentEdit.provider.version,
        model: elements.agentEditModel.value,
      });
    renderAgentEditPreview("model", payload);
    setAgentEditorStatus(`Model verified in ${Math.round(payload.verification.durationMilliseconds)} ms. Review before applying.`, "ok");
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "The model update could not be previewed.", "error");
  } finally {
    setBusy(elements.agentModelForm, false);
  }
}

async function previewAgentProfile(event) {
  event.preventDefault();
  if (!admin.agentEdit) return;
  setBusy(elements.agentProfileForm, true);
  discardAgentEditPreview();
  setAgentEditorStatus("Validating the complete policy and preparing its exact authority diff…");
  try {
    const selectedProvider = admin.agentEdit.providers.find(
      provider => provider.id === elements.agentEditProvider.value);
    const policy = {
      name: elements.agentEditName.value.trim(),
      expertise: elements.agentEditExpertise.value.trim() || null,
      mission: elements.agentEditMission.value.trim() || null,
      preferredLanguage: elements.agentEditLanguage.value.trim(),
      timeZone: elements.agentEditTimezone.value.trim(),
      responseStyle: elements.agentEditStyle.value.trim(),
      defaultWorkspace: elements.agentEditWorkspace.value.trim() || null,
      primaryProviderId: elements.agentEditProvider.value,
      dataLocality: elements.agentEditLocality.value,
      allowFallback: elements.agentEditFallback.checked,
      memoryScope: elements.agentEditMemoryScope.value,
      retentionDays: Number(elements.agentEditRetention.value),
      networkPosture: elements.agentEditNetwork.value,
      toolGrants: readGrantList(elements.agentEditToolGrants),
      skillGrants: readGrantList(elements.agentEditSkillGrants),
      maxTurns: Number(elements.agentEditMaxTurns.value),
      maxToolInvocations: Number(elements.agentEditMaxTools.value),
      maxInputTokens: Number(elements.agentEditMaxInput.value),
      maxOutputTokens: Number(elements.agentEditMaxOutput.value),
      maxWallClockSeconds: Number(elements.agentEditMaxWall.value),
      contextWindowOverrideTokens: elements.agentEditContextOverride.value
        ? Number(elements.agentEditContextOverride.value)
        : null,
      clearContextWindowOverride: !elements.agentEditContextOverride.value,
      contextCompressionEnabled: elements.agentEditContextCompression.checked,
      contextCompressionThresholdPercent: Number(elements.agentEditContextThreshold.value),
      contextCompressionTargetPercent: Number(elements.agentEditContextTarget.value),
      contextProtectedRecentTurns: Number(elements.agentEditContextRecentTurns.value),
      maxChildDepth: Number(elements.agentEditChildDepth.value),
      maxChildren: Number(elements.agentEditChildren.value),
      maxChildConcurrency: Number(elements.agentEditChildConcurrency.value),
      maxChildTokens: Number(elements.agentEditChildTokens.value),
      learningMode: elements.agentEditLearning.value,
      mutableSkillScope: elements.agentEditMutableScope.value,
    };
    const creating = admin.agentEditMode === "create";
    const path = creating
      ? "/api/v1/admin/agents/create/preview"
      : `/api/v1/admin/agents/${admin.agentEdit.agent.id}/profile/preview`;
    const payload = await adminMutation(path, creating ? {
      expectedInstallationVersion: admin.agentEdit.installationVersion,
      expectedProviderVersion: Number(selectedProvider?.version),
      ...policy,
    } : {
        expectedInstallationVersion: admin.agentEdit.installationVersion,
        expectedAgentVersion: admin.agentEdit.agent.version,
        expectedPrimaryProviderVersion: Number(selectedProvider?.version),
        ...policy,
      });
    renderAgentEditPreview(creating ? "create" : "profile", payload);
    setAgentEditorStatus("The exact complete-policy diff is ready for review.", "ok");
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "The profile update could not be previewed.", "error");
  } finally {
    setBusy(elements.agentProfileForm, false);
  }
}

async function applyAgentEditPreview() {
  if (!admin.agentEdit || !admin.agentEditPreview) return;
  elements.agentEditApply.disabled = true;
  const { kind, hash } = admin.agentEditPreview;
  const agentId = admin.agentEdit.agent?.id;
  setAgentEditorStatus("Revalidating and atomically applying the approved preview…");
  try {
    const result = await adminMutation(
      kind === "create"
        ? "/api/v1/admin/agents/create/apply"
        : `/api/v1/admin/agents/${agentId}/${kind === "model" ? "model" : "profile"}/apply`,
      { previewHash: hash });
    discardAgentEditPreview();
    await loadAgents("Refreshing the updated durable agent…");
    await openAgentEditor(kind === "create" ? result.agent.id : agentId);
    await loadRunOptions();
    setAgentEditorStatus(`${kind === "model" ? "Model" : kind === "create" ? "Agent" : "Complete policy"} committed and audited.`, "ok");
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "The approved edit could not be applied.", "error");
  } finally {
    elements.agentEditApply.disabled = false;
  }
}

function renderRunOptions(payload) {
  admin.runOptions = payload;
  elements.runSystemPreview.textContent = payload.agent.systemInstruction;
  elements.runModelSummary.textContent = `${payload.provider.name} · ${payload.provider.model} · ${payload.provider.endpoint}`;

  const selectedDepth = elements.runDepth.value || "balanced";
  elements.runDepth.replaceChildren();
  for (const depth of payload.responseDepths) {
    const option = document.createElement("option");
    option.value = depth.id;
    option.dataset.tokens = depth.maximumOutputTokens;
    option.textContent = `${depth.label} · up to ${depth.maximumOutputTokens.toLocaleString()} tokens`;
    elements.runDepth.append(option);
  }
  elements.runDepth.value = payload.responseDepths.some(item => item.id === selectedDepth)
    ? selectedDepth
    : "balanced";
  elements.runTokenLimit.max = payload.maximumOutputTokens;
  const selectedOption = payload.responseDepths.find(item => item.id === elements.runDepth.value);
  const currentLimit = Number(elements.runTokenLimit.value);
  elements.runTokenLimit.value = Number.isInteger(currentLimit) && currentLimit >= 1 &&
    currentLimit <= payload.maximumOutputTokens
    ? currentLimit
    : selectedOption.maximumOutputTokens;

  elements.runSkills.replaceChildren();
  if (!payload.skills.length) {
    elements.runSkills.append(makeElement("span", "option-empty", "No skill packages are installed. Install one in Skills, then promote and grant it before use."));
  } else {
    for (const skill of payload.skills) {
      const option = makeElement("label", `skill-option${skill.selectable ? "" : " unavailable"}`);
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = skill.id;
      const formBusy = elements.runForm.getAttribute("aria-busy") === "true";
      input.disabled = !skill.selectable || formBusy;
      if (!skill.selectable) input.dataset.policyDisabled = "true";
      if (formBusy) input.dataset.busyWasDisabled = "false";
      const copy = document.createElement("span");
      const reason = skill.selectable
        ? skill.description
        : `${skill.description} · ${skill.status !== "Active" ? "not Active" : "not granted to this agent"}`;
      copy.append(
        makeElement("strong", "", `${skill.id}@${skill.version}`),
        makeElement("small", "", reason));
      option.append(input, copy, stateChip(skill.status));
      elements.runSkills.append(option);
    }
  }

  elements.runRestrictions.replaceChildren();
  const labels = {
    modelRoute: "Model route",
    tools: "Tools",
    browsing: "Browsing",
    memory: "Memory",
    files: "Files",
    messaging: "Messaging",
    devices: "Devices",
    fallback: "Fallback",
  };
  for (const [key, value] of Object.entries(payload.restrictions)) {
    addMeta(elements.runRestrictions, labels[key] || key, value);
  }
}

async function loadRunOptions() {
  if (!elements.runAgent.value) {
    admin.runOptions = null;
    elements.runSystemPreview.textContent = "No configured agent is available.";
    elements.runSkills.replaceChildren(makeElement("span", "option-empty", "No agent is available for skill policy evaluation."));
    elements.runModelSummary.textContent = "No model route is available.";
    elements.runRestrictions.replaceChildren();
    return;
  }
  try {
    const payload = await adminRead(`/api/v1/admin/agents/${elements.runAgent.value}/run-options`);
    renderRunOptions(payload);
  } catch (error) {
    admin.runOptions = null;
    elements.runSystemPreview.textContent = "Run configuration could not be loaded.";
    elements.runSkills.replaceChildren(makeElement("span", "option-empty", "Skill options are unavailable until agent policy loads."));
    elements.runModelSummary.textContent = "The pinned model route could not be loaded.";
    elements.runRestrictions.replaceChildren();
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "Run options could not be loaded.", "error");
  }
}

function formatRunTime(value) {
  const instant = new Date(value);
  return Number.isNaN(instant.valueOf()) ? "Unknown" : instant.toLocaleString();
}

function renderRunHistory() {
  const terminalStates = ["Completed", "Failed", "Canceled", "DeadLettered"];
  const query = elements.runSearch.value.trim().toLowerCase();
  const state = elements.runStateFilter.value;
  const filtered = admin.runs.filter(run => {
    const agent = admin.agents.find(item => item.id === run.agentId);
    const matchesQuery = !query || [run.name, run.taskId, run.state, agent?.name || run.agentId]
      .some(value => String(value).toLowerCase().includes(query));
    const matchesState = state === "all" ||
      (state === "active" ? !terminalStates.includes(run.state) : run.state === state);
    return matchesQuery && matchesState;
  });
  admin.runPageSize = Number(elements.runPageSize.value) || 8;
  const pageCount = Math.max(1, Math.ceil(filtered.length / admin.runPageSize));
  admin.runPage = Math.min(Math.max(1, admin.runPage), pageCount);
  const first = (admin.runPage - 1) * admin.runPageSize;
  const page = filtered.slice(first, first + admin.runPageSize);

  elements.runList.replaceChildren();
  for (const run of page) {
    const card = makeElement("article", "resource-card compact");
    const content = document.createElement("div");
    const title = makeElement("div", "resource-title");
    const titleCopy = document.createElement("div");
    const agent = admin.agents.find(item => item.id === run.agentId);
    titleCopy.append(
      makeElement("h3", "", run.name),
      makeElement("p", "resource-subtitle", `${run.taskId} · ${agent?.name || run.agentId}`));
    title.append(titleCopy, stateChip(run.state));
    const meta = makeElement("div", "resource-meta");
    addMeta(meta, "Pattern", run.pattern);
    if (run.turnCount) addMeta(meta, "Turns", run.turnCount.toString());
    addMeta(meta, "Updated", formatRunTime(run.updatedAt));
    addMeta(meta, "Snapshot", `v${run.version} · ${run.snapshotHash.slice(0, 18)}…`);
    content.append(title, meta);
    card.append(content);
    const actions = makeElement("div", "resource-actions");
    if (run.conversationId) {
      const details = makeElement("button", "secondary-action", "Open conversation");
      details.type = "button";
      details.addEventListener("click", () => openRunDetails(run.conversationId));
      actions.append(details);
    }
    if (!terminalStates.includes(run.state) && run.state !== "NeedsResume") {
      const cancel = makeElement("button", "secondary-action", "Cancel run");
      cancel.type = "button";
      cancel.addEventListener("click", async () => {
        cancel.disabled = true;
        try {
          await adminMutation(`/api/v1/admin/runs/${run.latestTaskId || run.taskId}/cancel`);
          await loadRuns("Run canceled. Refreshing snapshots…", false);
        } catch (error) {
          workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "Run cancellation failed.", "error");
          cancel.disabled = false;
        }
      });
      actions.append(cancel);
    } else if (run.state === "NeedsResume") {
      const resume = makeElement("button", "setup-submit", "Resume turn");
      resume.type = "button";
      resume.addEventListener("click", async () => {
        await openRunDetails(run.conversationId);
        elements.resumeRunTurn.focus();
      });
      actions.append(resume);
    } else if (run.sourceTaskId || !run.conversationId) {
      const learn = makeElement("button", "secondary-action", "Capture learning");
      learn.type = "button";
      learn.addEventListener("click", async () => {
        admin.pendingLearningTaskId = run.sourceTaskId || run.taskId;
        if (window.location.hash === "#learning") await loadLearning();
        else window.location.hash = "#learning";
      });
      actions.append(learn);
    }
    card.append(actions);
    elements.runList.append(card);
  }
  if (!page.length) {
    const emptyCopy = admin.runs.length
      ? "No run receipts match the current search and status filters."
      : "No runs yet. Configure and start a bounded local-model run above.";
    elements.runList.append(makeElement("div", "empty-state", emptyCopy));
  }
  elements.runHistoryCount.textContent = query || state !== "all"
    ? `${filtered.length} matched · ${admin.runs.length} total`
    : `${admin.runs.length} ${admin.runs.length === 1 ? "run" : "runs"}`;
  elements.runPageSummary.textContent = `Page ${admin.runPage} of ${pageCount}`;
  elements.runPagePrevious.disabled = admin.runPage <= 1;
  elements.runPageNext.disabled = admin.runPage >= pageCount;
}

async function openRunDetails(conversationId) {
  try {
    workspaceStatus(elements.runsMessage, "Loading hash-verified conversation artifacts…");
    const payload = await adminRead(`/api/v1/admin/runs/${conversationId}`);
    admin.selectedRunDetails = payload;
    elements.runDetails.hidden = false;
    elements.runDetailsTitle.textContent = payload.run.name;
    elements.runDetailsMeta.replaceChildren();
    addMeta(elements.runDetailsMeta, "Conversation", payload.run.conversationId);
    addMeta(elements.runDetailsMeta, "State", payload.run.state);
    addMeta(elements.runDetailsMeta, "Model", `${payload.provider.model} · provider v${payload.provider.version}`);
    addMeta(elements.runDetailsMeta, "Turns", payload.turns.length.toString());
    if (payload.context) {
      addMeta(elements.runDetailsMeta, "Context usage", `${payload.context.estimatedInputTokens.toLocaleString()} / ${payload.context.capacityTokens.toLocaleString()} tokens · ${payload.context.occupancyPercent}%`);
      addMeta(elements.runDetailsMeta, "Context policy", `${payload.context.source} · compress at ${payload.context.thresholdPercent}% to ${payload.context.targetPercent}%`);
    }
    addMeta(elements.runDetailsMeta, "Policy", `${payload.policySnapshotHash.slice(0, 20)}…`);
    addMeta(elements.runDetailsMeta, "Skills", payload.skillIds.length ? payload.skillIds.join(", ") : "None");
    elements.runTranscript.replaceChildren();
    for (const item of payload.turns) {
      const turn = makeElement("article", "conversation-turn");
      const user = makeElement("div", "conversation-message user");
      user.append(makeElement("span", "", `Turn ${item.sequence} · You`), makeElement("p", "", item.prompt));
      turn.append(user);
      if (item.response) {
        const assistant = makeElement("div", "conversation-message assistant");
        assistant.append(makeElement("span", "", "Agent"), makeElement("p", "", item.response));
        turn.append(assistant);
      }
      const usage = item.usage
        ? `${item.usage.inputTokens.toLocaleString()} in · ${item.usage.outputTokens.toLocaleString()} out`
        : "usage unavailable";
      turn.append(makeElement("div", "conversation-turn-footer",
        `Turn ${item.sequence} · ${item.state} · ${item.responseDepth} · ${usage} · ${formatRunTime(item.updatedAt)}`));
      elements.runTranscript.append(turn);
    }
    const latest = payload.turns[payload.turns.length - 1];
    const pendingSearch = (payload.toolCalls || []).find(item =>
      item.turnId === latest.id && item.state === "AwaitingApproval");
    if (pendingSearch) showRunSearchApproval(payload.run.conversationId, latest.id, pendingSearch);
    else if (admin.pendingSearchApproval?.conversationId === payload.run.conversationId) resetRunSearchApproval();
    const agent = admin.agents.find(item => item.id === payload.run.agentId);
    const maximum = Math.min(262144, Number(agent?.budget?.maxOutputTokens || 262144));
    elements.runFollowUpTokens.max = maximum;
    if (Number(elements.runFollowUpTokens.value) > maximum) elements.runFollowUpTokens.value = maximum;
    elements.continueRun.disabled = !payload.run.canContinue;
    elements.runFollowUp.disabled = !payload.run.canContinue;
    elements.resumeRunTurn.hidden = !payload.run.resumable || Boolean(pendingSearch);
    elements.resumeRunTurn.dataset.turnId = latest.id;
    elements.runContinueHelp.textContent = pendingSearch
      ? "This turn is safely paused. Review the exact external read above before the model can continue."
      : payload.run.resumable
      ? "This turn has durable input but no final assistant artifact. Resume will reclaim only an expired or ready retry lease."
      : payload.run.canContinue
        ? "A follow-up creates a new durable task and supplies bounded prior turns to the exact pinned model."
        : "This conversation cannot accept another turn in its current state.";
    workspaceStatus(elements.runsMessage, `${payload.turns.length} hash-verified conversation ${payload.turns.length === 1 ? "turn" : "turns"} loaded.`, "ok");
    elements.runDetails.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "Conversation details could not be loaded.", "error");
  }
}

function renderActiveContext(payload) {
  if (!payload) return;
  elements.runContextMeter.hidden = false;
  elements.runContextProgress.value = Math.max(0, Math.min(100, Number(payload.occupancyPercent || 0)));
  const reserved = Number(payload.reservedOutputTokens || 0);
  elements.runContextValue.textContent = `${Number(payload.estimatedInputTokens || 0).toLocaleString()} input${reserved ? ` + ${reserved.toLocaleString()} reserved output` : ""} / ${Number(payload.capacityTokens || 0).toLocaleString()} · ${payload.occupancyPercent}%`;
  elements.runContextNote.textContent = payload.compressed
    ? `${payload.compressedTurnCount} older turn${payload.compressedTurnCount === 1 ? "" : "s"} compressed; ${payload.protectedTurnCount} recent turn${payload.protectedTurnCount === 1 ? "" : "s"} protected.`
    : payload.compressionEnabled
      ? `Compression starts at ${payload.thresholdPercent}% and targets ${payload.targetPercent}% · ${payload.source}.`
      : `Compression disabled · ${payload.source}.`;
}

function resetRunSearchApproval() {
  admin.pendingSearchApproval = null;
  elements.runSearchApproval.hidden = true;
  elements.runSearchPreview.hidden = true;
  elements.runSearchPreviewHash.hidden = true;
  elements.runSearchApply.hidden = true;
  elements.runSearchApprove.hidden = false;
  elements.runSearchDeny.hidden = false;
}

function showRunSearchApproval(conversationId, turnId, toolCall) {
  const args = toolCall.arguments || {};
  admin.pendingSearchApproval = {
    conversationId,
    turnId,
    toolCallId: toolCall.toolCallId,
    toolName: toolCall.toolName,
    arguments: args,
    previewHash: null,
    disposition: null,
  };
  elements.runSearchApproval.hidden = false;
  if (toolCall.toolName === "search_web") {
    const maximum = Number(args.maximumResults || 5);
    elements.runToolApprovalTitle.textContent = "Agent requests Brave Search";
    elements.runSearchQuery.textContent = `“${args.query || ""}” · up to ${maximum} cited result${maximum === 1 ? "" : "s"}`;
  } else {
    const query = args.query && typeof args.query === "object" ? JSON.stringify(args.query) : "{}";
    elements.runToolApprovalTitle.textContent = "Agent requests configured API data";
    elements.runSearchQuery.textContent = `${args.profileId || "unknown profile"} · GET ${args.relativePath || "invalid path"} · ${query}`;
  }
  elements.runSearchPreview.replaceChildren();
  elements.runSearchPreview.hidden = true;
  elements.runSearchPreviewHash.hidden = true;
  elements.runSearchApply.hidden = true;
  elements.runSearchApprove.hidden = false;
  elements.runSearchDeny.hidden = false;
  elements.runSearchWarning.textContent = "No network request has been made. Review the exact operation before deciding.";
}

async function previewRunSearch(disposition) {
  const pending = admin.pendingSearchApproval;
  if (!pending) return;
  setBusy(elements.runSearchApproval, true);
  try {
    const preview = await adminMutation(
      `/api/v1/admin/runs/${pending.conversationId}/turns/${pending.turnId}/tool/preview`,
      { disposition, approvalSeconds: 300 });
    pending.previewHash = preview.previewHash;
    pending.disposition = disposition;
    elements.runSearchPreview.replaceChildren();
    addMeta(elements.runSearchPreview, "Decision", disposition === "deny" ? "Deny; no network request" : `Approve once · ${preview.operation}`);
    for (const [label, value] of Object.entries(preview.parameters || {})) addMeta(elements.runSearchPreview, label, String(value));
    addMeta(elements.runSearchPreview, "Destination", preview.endpoint);
    addMeta(elements.runSearchPreview, "Credential", "OS-backed; never sent to the model");
    addMeta(elements.runSearchPreview, "Expires", new Date(preview.expiresAt).toLocaleTimeString());
    elements.runSearchPreview.hidden = false;
    elements.runSearchPreviewHash.textContent = preview.previewHash;
    elements.runSearchPreviewHash.hidden = false;
    elements.runSearchWarning.textContent = preview.warning;
    elements.runSearchApprove.hidden = true;
    elements.runSearchDeny.hidden = true;
    elements.runSearchApply.hidden = false;
    elements.runSearchApply.textContent = disposition === "deny" ? "Confirm denial" : "Approve and continue";
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "The exact tool decision could not be previewed.", "error");
  } finally {
    setBusy(elements.runSearchApproval, false);
  }
}

async function applyRunSearch() {
  const pending = admin.pendingSearchApproval;
  if (!pending?.previewHash) return;
  setBusy(elements.runSearchApproval, true);
  try {
    const result = await adminMutation(
      `/api/v1/admin/runs/${pending.conversationId}/turns/${pending.turnId}/tool/apply`,
      { previewHash: pending.previewHash });
    workspaceStatus(elements.runsMessage,
      result.denied ? "External read denied. Returning the decision to the agent…" : "Approved result attached with durable evidence. Resuming the agent…",
      "ok");
    const conversationId = pending.conversationId;
    resetRunSearchApproval();
    await openRunDetails(conversationId);
    await resumeRunConversation();
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "The tool decision could not be applied.", "error");
  } finally {
    setBusy(elements.runSearchApproval, false);
  }
}

async function streamConversationTurn(path, body, startingMessage) {
  elements.runOutput.hidden = false;
  elements.runOutputState.textContent = "Starting";
  elements.runOutputState.className = "state-chip running";
  elements.runOutputText.textContent = "";
  elements.runOutputMeta.textContent = startingMessage;
  elements.runContextMeter.hidden = true;
  elements.cancelInteraction.hidden = true;
  admin.activeTaskId = null;
  resetRunSearchApproval();
  let terminalEvent = null;
  let providerLabel = "Pinned local model";
  let usageLabel = "Token usage pending";
  await adminStreamMutation(path, body, async (eventName, payload) => {
    if (eventName === "run-started") {
      admin.activeTaskId = payload.taskId;
      providerLabel = `${payload.provider.name} · ${payload.provider.model}`;
      elements.runOutputState.textContent = payload.resumed ? "Resumed" : "Running";
      elements.runOutputMeta.textContent = `${providerLabel} · turn ${payload.configuration.turn} · ${payload.configuration.responseDepth}`;
      elements.cancelInteraction.hidden = false;
    } else if (eventName === "context-status") {
      renderActiveContext(payload);
    } else if (eventName === "model-started") {
      elements.runOutputMeta.textContent = `${providerLabel} · context redactions ${payload.contextRedactionCount}`;
    } else if (eventName === "output-delta") {
      elements.runOutputText.textContent += payload.text || "";
      elements.runOutputText.scrollTop = elements.runOutputText.scrollHeight;
    } else if (eventName === "usage" && payload.usage) {
      usageLabel = `${payload.usage.inputTokens.toLocaleString()} input · ${payload.usage.outputTokens.toLocaleString()} output tokens`;
      elements.runOutputMeta.textContent = `${providerLabel} · ${usageLabel}`;
    } else if (eventName === "completed") {
      terminalEvent = eventName;
      elements.runOutputState.textContent = "Completed";
      elements.runOutputState.className = "state-chip completed";
      elements.runOutputMeta.textContent = `${providerLabel} · ${usageLabel} · ${payload.finishReason}`;
      elements.cancelInteraction.hidden = true;
    } else if (eventName === "approval-required") {
      terminalEvent = eventName;
      elements.runOutputState.textContent = "Approval required";
      elements.runOutputState.className = "state-chip warning";
      elements.runOutputMeta.textContent = `${providerLabel} · waiting for exact Brave query approval`;
      elements.cancelInteraction.hidden = true;
      showRunSearchApproval(payload.run.conversationId, payload.run.nodes.at(-1).id, payload.toolCall);
    } else if (eventName === "failed") {
      terminalEvent = eventName;
      elements.runOutputState.textContent = payload.resumable ? "Needs resume" : "Failed";
      elements.runOutputState.className = "state-chip failed";
      elements.runOutputMeta.textContent = `${providerLabel} · ${payload.code}`;
      elements.cancelInteraction.hidden = true;
    } else if (eventName === "canceled") {
      terminalEvent = eventName;
      elements.runOutputState.textContent = "Canceled";
      elements.runOutputState.className = "state-chip canceled";
      elements.cancelInteraction.hidden = true;
    }
  });
  if (!terminalEvent) throw new Error("The model stream closed without a durable turn receipt.");
  const selectedId = admin.selectedRunDetails?.run?.conversationId;
  await loadRuns("Conversation turn stored. Refreshing durable history…", false);
  if (selectedId) await openRunDetails(selectedId);
}

async function continueRunConversation(event) {
  event.preventDefault();
  const details = admin.selectedRunDetails;
  if (!details?.run?.conversationId || !details.run.canContinue) return;
  const prompt = elements.runFollowUp.value.trim();
  if (!prompt) {
    workspaceStatus(elements.runsMessage, "Enter a follow-up objective before continuing.", "error");
    return;
  }
  setBusy(elements.runContinueForm, true);
  try {
    await streamConversationTurn(
      `/api/v1/admin/runs/${details.run.conversationId}/turns-stream`,
      {
        prompt,
        responseDepth: elements.runFollowUpDepth.value,
        maximumOutputTokens: Number(elements.runFollowUpTokens.value),
      },
      "Persisting the next turn and opening the pinned local-model stream…");
    elements.runFollowUp.value = "";
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "The follow-up turn failed.", "error");
  } finally {
    admin.activeTaskId = null;
    setBusy(elements.runContinueForm, false);
  }
}

async function resumeRunConversation() {
  const details = admin.selectedRunDetails;
  const turnId = elements.resumeRunTurn.dataset.turnId;
  if (!details?.run?.conversationId || !turnId) return;
  setBusy(elements.runContinueForm, true);
  try {
    await streamConversationTurn(
      `/api/v1/admin/runs/${details.run.conversationId}/turns/${turnId}/resume-stream`,
      {},
      "Reclaiming the durable turn lease and rebuilding bounded model context…");
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "The interrupted turn could not resume.", "error");
  } finally {
    admin.activeTaskId = null;
    setBusy(elements.runContinueForm, false);
  }
}

async function loadRuns(message = "Loading durable run snapshots…", resetPage = true) {
  workspaceStatus(elements.runsMessage, message);
  try {
    if (!admin.agents.length) await loadAgents();
    await loadRunOptions();
    const payload = await adminRead("/api/v1/admin/runs");
    admin.runs = payload.runs;
    if (resetPage) admin.runPage = 1;
    renderRunHistory();
    workspaceStatus(elements.runsMessage, `${payload.runs.length} latest run ${payload.runs.length === 1 ? "snapshot" : "snapshots"} loaded.`, "ok");
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "Runs could not be loaded.", "error");
  }
}

async function createRun(event) {
  event.preventDefault();
  setBusy(elements.runForm, true);
  elements.runOutput.hidden = false;
  elements.runOutputState.textContent = "Starting";
  elements.runOutputState.className = "state-chip running";
  elements.runOutputText.textContent = "";
  elements.runOutputMeta.textContent = "Preparing a durable run and opening the model stream…";
  elements.runContextMeter.hidden = true;
  elements.cancelInteraction.hidden = true;
  elements.cancelInteraction.disabled = false;
  admin.activeTaskId = null;
  resetRunSearchApproval();
  try {
    const prompt = document.querySelector("#run-prompt").value.trim();
    const skillIds = [...elements.runSkills.querySelectorAll("input[type='checkbox']:checked")]
      .map(input => input.value);
    workspaceStatus(elements.runsMessage, "Invoking the pinned local model within the displayed governed boundaries…");
    let providerLabel = "Pinned local model";
    let usageLabel = "Token usage pending";
    let terminalMessage = "Local model stream ended.";
    let terminalEvent = null;
    let completedConversationId = null;
    await adminStreamMutation(
      `/api/v1/admin/agents/${elements.runAgent.value}/test-chat-stream`,
      {
        prompt,
        name: elements.runName.value.trim() || null,
        runInstructions: elements.runInstructions.value.trim() || null,
        responseDepth: elements.runDepth.value,
        maximumOutputTokens: Number(elements.runTokenLimit.value),
        skillIds,
        memoryQuery: elements.runMemoryQuery.value.trim() || null,
        researchReceiptHash: elements.runResearchReceipt.value || null,
      },
      async (eventName, payload) => {
        if (eventName === "run-started") {
          admin.activeTaskId = payload.taskId;
          completedConversationId = payload.conversationId || null;
          providerLabel = `${payload.provider.name} · ${payload.provider.model}`;
          elements.runOutputState.textContent = "Running";
          const skillLabel = payload.configuration.skillIds.length
            ? ` · ${payload.configuration.skillIds.length} skill ${payload.configuration.skillIds.length === 1 ? "snapshot" : "snapshots"}`
            : "";
          const contextCount = (payload.configuration.memoryCount || 0) + (payload.configuration.citationCount || 0);
          const contextLabel = contextCount ? ` · ${contextCount} attached context item${contextCount === 1 ? "" : "s"}` : "";
          elements.runOutputMeta.textContent = `${providerLabel} · ${payload.configuration.responseDepth} · up to ${payload.configuration.maximumOutputTokens.toLocaleString()} tokens${skillLabel}${contextLabel}`;
          elements.cancelInteraction.hidden = false;
        } else if (eventName === "context-status") {
          renderActiveContext(payload);
        } else if (eventName === "model-started") {
          elements.runOutputMeta.textContent = `${providerLabel} · context redactions ${payload.contextRedactionCount}`;
        } else if (eventName === "output-delta") {
          elements.runOutputText.textContent += payload.text || "";
          elements.runOutputText.scrollTop = elements.runOutputText.scrollHeight;
        } else if (eventName === "usage" && payload.usage) {
          usageLabel = `${payload.usage.inputTokens.toLocaleString()} input · ${payload.usage.outputTokens.toLocaleString()} output tokens`;
          elements.runOutputMeta.textContent = `${providerLabel} · ${usageLabel}`;
        } else if (eventName === "completed") {
          terminalEvent = eventName;
          terminalMessage = "Local model answered. Durable completion receipt loaded below.";
          elements.runOutputState.textContent = "Completed";
          elements.runOutputState.className = "state-chip completed";
          elements.runOutputMeta.textContent = `${providerLabel} · ${usageLabel} · ${payload.finishReason}`;
          elements.cancelInteraction.hidden = true;
        } else if (eventName === "approval-required") {
          terminalEvent = eventName;
          terminalMessage = "The run is paused for an exact Brave Search decision.";
          elements.runOutputState.textContent = "Approval required";
          elements.runOutputState.className = "state-chip warning";
          elements.runOutputMeta.textContent = `${providerLabel} · waiting for exact search approval`;
          elements.cancelInteraction.hidden = true;
          showRunSearchApproval(payload.run.conversationId, payload.run.nodes.at(-1).id, payload.toolCall);
        } else if (eventName === "canceled") {
          terminalEvent = eventName;
          terminalMessage = "Interaction canceled. Durable cancellation receipt loaded below.";
          elements.runOutputState.textContent = "Canceled";
          elements.runOutputState.className = "state-chip canceled";
          elements.runOutputMeta.textContent = `${providerLabel} · canceled by operator`;
          elements.cancelInteraction.hidden = true;
        } else if (eventName === "failed") {
          terminalEvent = eventName;
          terminalMessage = payload.resumable
            ? "The provider interruption is durable and can be resumed from run details."
            : payload.message || "The model interaction failed.";
          elements.runOutputState.textContent = payload.resumable ? "Needs resume" : "Failed";
          elements.runOutputState.className = "state-chip failed";
          elements.runOutputMeta.textContent = `${providerLabel} · ${payload.code}`;
          elements.cancelInteraction.hidden = true;
        }
      });
    if (!terminalEvent) throw new Error("The model stream closed without a durable terminal receipt.");
    await loadRuns(terminalMessage);
    if (completedConversationId) await openRunDetails(completedConversationId);
  } catch (error) {
    elements.runOutputState.textContent = "Failed";
    elements.runOutputState.className = "state-chip failed";
    elements.cancelInteraction.hidden = true;
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "The local model test failed.", "error");
  } finally {
    admin.activeTaskId = null;
    setBusy(elements.runForm, false);
  }
}

elements.cancelInteraction.addEventListener("click", async () => {
  if (!admin.activeTaskId) return;
  elements.cancelInteraction.disabled = true;
  elements.runOutputState.textContent = "Canceling";
  try {
    await adminMutation(`/api/v1/admin/runs/${admin.activeTaskId}/cancel`);
  } catch (error) {
    workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "Interaction cancellation failed.", "error");
    elements.cancelInteraction.disabled = false;
  }
});

function scheduleWebRequest(edit) {
  const trigger = elements.scheduleTrigger.value;
  const oneShot = elements.scheduleOneShot.value
    ? new Date(elements.scheduleOneShot.value).toISOString()
    : null;
  return {
    expectedInstallationVersion: edit.installationVersion,
    expectedAgentVersion: edit.agent.version,
    expectedProviderVersion: edit.provider.version,
    agentId: edit.agent.id,
    name: elements.scheduleName.value.trim(),
    prompt: elements.schedulePrompt.value.trim(),
    runInstructions: elements.scheduleInstructions.value.trim() || null,
    responseDepth: elements.scheduleDepth.value,
    maximumOutputTokens: Number(elements.scheduleTokenLimit.value),
    skillIds: [],
    triggerKind: trigger,
    oneShotAt: trigger === "OneShot" ? oneShot : null,
    intervalSeconds: trigger === "Interval" ? Number(elements.scheduleInterval.value) : null,
    cronExpression: trigger === "Cron" ? elements.scheduleCron.value.trim() : null,
    calendarHour: trigger === "Calendar" ? Number(elements.scheduleHour.value) : null,
    calendarMinute: trigger === "Calendar" ? Number(elements.scheduleMinute.value) : null,
    calendarDays: [],
    calendarDayOfMonth: null,
    timeZoneId: elements.scheduleTimezone.value.trim(),
    misfirePolicy: elements.scheduleMisfire.value,
    overlapPolicy: elements.scheduleOverlap.value,
    misfireGraceSeconds: Number(elements.scheduleGrace.value),
    maximumCatchUp: Number(elements.scheduleCatchup.value),
    maximumParallelRuns: Number(elements.scheduleParallel.value),
    maximumJitterSeconds: Number(elements.scheduleJitter.value),
    maximumAttempts: Number(elements.scheduleAttempts.value),
    retryDelaySeconds: Number(elements.scheduleRetry.value),
    maximumConsecutiveFailures: Number(elements.scheduleFailureLimit.value),
    expiresAt: null,
  };
}

function renderSchedules() {
  elements.scheduleList.replaceChildren();
  for (const schedule of admin.schedules) {
    const card = makeElement("article", "resource-card");
    const heading = makeElement("div", "resource-title");
    const copy = document.createElement("div");
    copy.append(makeElement("h3", "", schedule.name),
      makeElement("p", "resource-subtitle", `${schedule.id} · version ${schedule.version}`));
    heading.append(copy, stateChip(schedule.state));
    const meta = makeElement("div", "resource-meta");
    addMeta(meta, "Trigger", schedule.trigger.kind);
    addMeta(meta, "Next due", schedule.nextDueAt ? new Date(schedule.nextDueAt).toLocaleString() : "Not queued");
    addMeta(meta, "Completed", schedule.completedCount);
    addMeta(meta, "Failed", schedule.failedCount);
    addMeta(meta, "Queued / running", `${schedule.queued} / ${schedule.running}`);
    addMeta(meta, "Time zone", schedule.timeZoneId);
    const actions = makeElement("div", "resource-actions");
    const mutate = async (action, label) => {
      workspaceStatus(elements.schedulesMessage, `${label}…`);
      try {
        await adminMutation(`/api/v1/admin/schedules/${schedule.id}/${action}`, { expectedVersion: schedule.version });
        await loadSchedules(`${label} completed. Refreshing durable state…`);
      } catch (error) {
        workspaceStatus(elements.schedulesMessage, error instanceof Error ? error.message : `${label} failed.`, "error");
      }
    };
    const runNow = makeElement("button", "secondary-action", "Run now");
    runNow.type = "button";
    runNow.addEventListener("click", () => mutate("run-now", "Queueing an immediate occurrence"));
    actions.append(runNow);
    if (schedule.state === "Active") {
      const pause = makeElement("button", "secondary-action", "Pause");
      pause.type = "button";
      pause.addEventListener("click", () => mutate("pause", "Pausing schedule"));
      actions.append(pause);
    } else if (schedule.state === "Paused") {
      const resume = makeElement("button", "secondary-action", "Resume");
      resume.type = "button";
      resume.addEventListener("click", () => mutate("resume", "Resuming schedule"));
      actions.append(resume);
    }
    card.append(heading, meta, actions);
    elements.scheduleList.append(card);
  }
  if (!admin.schedules.length) elements.scheduleList.append(makeElement("div", "empty-state", "No durable schedules exist yet."));
}

async function loadSchedules(message = "Loading durable schedules…") {
  workspaceStatus(elements.schedulesMessage, message);
  try {
    if (!admin.agents.length) await loadAgents();
    const payload = await adminRead("/api/v1/admin/schedules");
    admin.schedules = payload.schedules;
    renderSchedules();
    workspaceStatus(elements.schedulesMessage, `${payload.schedules.length} durable schedule${payload.schedules.length === 1 ? "" : "s"} loaded.`, "ok");
  } catch (error) {
    workspaceStatus(elements.schedulesMessage, error instanceof Error ? error.message : "Schedules could not be loaded.", "error");
  }
}

async function previewSchedule(event) {
  event.preventDefault();
  setBusy(elements.scheduleForm, true);
  try {
    const edit = await adminRead(`/api/v1/admin/agents/${elements.scheduleAgent.value}/edit`);
    const request = scheduleWebRequest(edit);
    const preview = await adminMutation("/api/v1/admin/schedules/preview", request);
    admin.scheduleRequest = request;
    admin.schedulePreview = preview;
    elements.schedulePreviewDetails.replaceChildren();
    addMeta(elements.schedulePreviewDetails, "Agent", `${preview.agent.name} · v${preview.agent.version}`);
    addMeta(elements.schedulePreviewDetails, "Model", `${preview.provider.model} · provider v${preview.provider.version}`);
    addMeta(elements.schedulePreviewDetails, "Trigger", preview.trigger.kind);
    addMeta(elements.schedulePreviewDetails, "Next occurrences", preview.nextOccurrences.map(value => new Date(value).toLocaleString()).join(" · "));
    elements.schedulePreviewHash.textContent = preview.previewHash;
    elements.scheduleReview.hidden = false;
    workspaceStatus(elements.schedulesMessage, preview.warning, "ok");
    elements.scheduleReview.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    workspaceStatus(elements.schedulesMessage, error instanceof Error ? error.message : "The schedule preview failed.", "error");
  } finally {
    setBusy(elements.scheduleForm, false);
  }
}

function discardSchedulePreview() {
  admin.schedulePreview = null;
  admin.scheduleRequest = null;
  elements.scheduleReview.hidden = true;
  elements.schedulePreviewDetails.replaceChildren();
  elements.schedulePreviewHash.textContent = "";
}

async function applySchedule(event) {
  event.preventDefault();
  if (!admin.schedulePreview || !admin.scheduleRequest) return;
  setBusy(elements.scheduleReview, true);
  try {
    await adminMutation("/api/v1/admin/schedules/apply", {
      previewHash: admin.schedulePreview.previewHash,
      schedule: admin.scheduleRequest,
    });
    discardSchedulePreview();
    elements.schedulePrompt.value = "";
    await loadSchedules("Schedule created. Loading immutable execution state…");
  } catch (error) {
    workspaceStatus(elements.schedulesMessage, error instanceof Error ? error.message : "The schedule could not be created.", "error");
  } finally {
    setBusy(elements.scheduleReview, false);
  }
}

function selectedContextAgent(select) {
  return admin.agents.find(agent => agent.id === select.value) ?? null;
}

function renderMemories() {
  elements.memoryList.replaceChildren();
  for (const memory of admin.memories) {
    const card = makeElement("article", "resource-card compact-card");
    const heading = makeElement("div", "resource-title");
    heading.append(makeElement("h3", "", memory.kind), stateChip("Active"));
    const meta = makeElement("div", "resource-meta");
    addMeta(meta, "Scope", memory.scopeId);
    addMeta(meta, "Expires", new Date(memory.expiresAtUtc).toLocaleDateString());
    addMeta(meta, "Source", memory.source.kind);
    const actions = makeElement("div", "resource-actions");
    const remove = makeElement("button", "secondary-action danger-action", "Delete");
    remove.type = "button";
    remove.addEventListener("click", async () => {
      const agent = selectedContextAgent(elements.memoryAgent);
      if (!agent) return;
      try {
        await adminMutation(`/api/v1/admin/memory/${memory.id}/delete`, {
          expectedAgentVersion: agent.version,
          agentId: agent.id,
        });
        admin.memories = admin.memories.filter(item => item.id !== memory.id);
        renderMemories();
        workspaceStatus(elements.memoryMessage, "Memory deleted from its exact scope.", "ok");
      } catch (error) {
        workspaceStatus(elements.memoryMessage, error instanceof Error ? error.message : "Memory deletion failed.", "error");
      }
    });
    actions.append(remove);
    card.append(heading, makeElement("p", "resource-description", memory.content), meta, actions);
    elements.memoryList.append(card);
  }
  if (!admin.memories.length) elements.memoryList.append(makeElement("div", "empty-state", "No matching unexpired memory entries."));
}

async function searchMemory(event) {
  event?.preventDefault();
  const query = elements.memoryQuery.value.trim();
  if (!query) return;
  workspaceStatus(elements.memoryMessage, "Searching the exact configured memory scope…");
  try {
    const payload = await adminRead(`/api/v1/admin/memory?agentId=${encodeURIComponent(elements.memoryAgent.value)}&query=${encodeURIComponent(query)}&maximumResults=20`);
    admin.memories = payload.memories;
    renderMemories();
    workspaceStatus(elements.memoryMessage, `${payload.memories.length} scoped memor${payload.memories.length === 1 ? "y" : "ies"} found.`, "ok");
  } catch (error) {
    workspaceStatus(elements.memoryMessage, error instanceof Error ? error.message : "Memory search failed.", "error");
  }
}

async function createMemory(event) {
  event.preventDefault();
  const agent = selectedContextAgent(elements.memoryAgent);
  if (!agent) return;
  setBusy(elements.memoryCreateForm, true);
  try {
    await adminMutation("/api/v1/admin/memory", {
      expectedAgentVersion: agent.version,
      agentId: agent.id,
      kind: elements.memoryKind.value,
      content: elements.memoryContent.value.trim(),
      isCorrection: elements.memoryCorrection.checked,
      retentionDays: Number(elements.memoryRetention.value),
    });
    if (!elements.memoryQuery.value.trim()) {
      elements.memoryQuery.value = elements.memoryContent.value.trim().split(/\s+/).slice(0, 4).join(" ").slice(0, 256);
    }
    elements.memoryContent.value = "";
    await searchMemory();
    workspaceStatus(elements.memoryMessage, "Redacted memory saved and available to explicit run retrieval.", "ok");
  } catch (error) {
    workspaceStatus(elements.memoryMessage, error instanceof Error ? error.message : "Memory creation failed.", "error");
  } finally {
    setBusy(elements.memoryCreateForm, false);
  }
}

function renderResearchProviders() {
  elements.researchProviders.replaceChildren();
  for (const provider of admin.researchProviders) {
    const option = makeElement("label", "skill-option");
    const input = document.createElement("input");
    input.type = "checkbox";
    input.value = provider.id;
    input.checked = true;
    option.append(input, makeElement("span", "", `${provider.id} · ${provider.kind}`));
    elements.researchProviders.append(option);
  }
  if (!admin.researchProviders.length) {
    elements.researchProviders.append(makeElement("span", "option-empty", "No search provider is configured. Add a provider adapter before live research."));
  }
}

function renderBraveConfiguration(configuration) {
  admin.braveConfiguration = configuration;
  elements.braveConfigEnabled.checked = configuration.isEnabled;
  elements.braveConfigSafe.value = configuration.safeSearch;
  elements.braveConfigCountry.value = configuration.countryCode || "";
  elements.braveConfigLanguage.value = configuration.searchLanguage || "en";
  elements.braveConfigKey.placeholder = configuration.configured
    ? "Leave blank to retain current key; enter a replacement to rotate"
    : "Write-only Brave subscription token";
  const state = configuration.configured
    ? (configuration.isEnabled ? "READY" : "DISABLED")
    : "NOT CONFIGURED";
  elements.braveConfigState.textContent = state;
  elements.braveConfigState.className = `state-chip ${configuration.configured && configuration.isEnabled ? "ready" : "archived"}`;
  elements.braveConfigSummary.textContent = configuration.configured
    ? `Version ${configuration.version} · ${configuration.safeSearch} safe search · OS-backed secret${configuration.updatedAtUtc ? ` · updated ${new Date(configuration.updatedAtUtc).toLocaleString()}` : ""}`
    : "Add a write-only API key to enable cited Brave web research.";
  workspaceStatus(elements.braveConfigMessage,
    configuration.secretStore.isAvailable ? configuration.warning : (configuration.secretStore.reason || "OS-backed secret storage is unavailable."),
    configuration.secretStore.isAvailable ? "" : "error");
}

async function previewBraveConfiguration(event) {
  event.preventDefault();
  setBusy(elements.braveConfigForm, true);
  try {
    const preview = await adminMutation("/api/v1/admin/research/providers/brave/configuration/preview", {
      expectedVersion: admin.braveConfiguration?.version ?? null,
      isEnabled: elements.braveConfigEnabled.checked,
      safeSearch: elements.braveConfigSafe.value,
      countryCode: elements.braveConfigCountry.value.trim(),
      searchLanguage: elements.braveConfigLanguage.value.trim(),
      apiKey: elements.braveConfigKey.value || null,
    });
    admin.braveConfigurationPreview = preview;
    elements.braveConfigDetails.replaceChildren();
    addMeta(elements.braveConfigDetails, "State", preview.isEnabled ? "Enabled" : "Disabled");
    addMeta(elements.braveConfigDetails, "Safe search", preview.safeSearch);
    addMeta(elements.braveConfigDetails, "Country", preview.countryCode || "Provider default");
    addMeta(elements.braveConfigDetails, "Language", preview.searchLanguage);
    addMeta(elements.braveConfigDetails, "Credential", preview.credentialAction);
    addMeta(elements.braveConfigDetails, "Live verification",
      preview.verification ? `${preview.verification.resultCount} result · ${Math.round(preview.verification.durationMilliseconds)} ms` : "Not required while disabled");
    elements.braveConfigPreviewHash.textContent = preview.previewHash;
    elements.braveConfigReview.hidden = false;
    workspaceStatus(elements.braveConfigMessage, preview.warning, "ok");
  } catch (error) {
    workspaceStatus(elements.braveConfigMessage, error instanceof Error ? error.message : "Brave Search verification failed.", "error");
  } finally {
    setBusy(elements.braveConfigForm, false);
  }
}

function discardBraveConfigurationPreview() {
  admin.braveConfigurationPreview = null;
  elements.braveConfigKey.value = "";
  elements.braveConfigReview.hidden = true;
  elements.braveConfigDetails.replaceChildren();
  elements.braveConfigPreviewHash.textContent = "";
}

async function applyBraveConfiguration(event) {
  event.preventDefault();
  if (!admin.braveConfigurationPreview) return;
  setBusy(elements.braveConfigReview, true);
  try {
    await adminMutation("/api/v1/admin/research/providers/brave/configuration/apply", {
      previewHash: admin.braveConfigurationPreview.previewHash,
      apiKey: elements.braveConfigKey.value || null,
    });
    discardBraveConfigurationPreview();
    await loadContext();
    workspaceStatus(elements.braveConfigMessage,
      "Brave Search configuration applied. The write-only key was cleared from this page.", "ok");
  } catch (error) {
    workspaceStatus(elements.braveConfigMessage, error instanceof Error ? error.message : "Brave Search configuration failed.", "error");
  } finally {
    setBusy(elements.braveConfigReview, false);
  }
}

async function loadContext(message = "Loading memory and research capabilities…") {
  workspaceStatus(elements.researchMessage, message);
  try {
    if (!admin.agents.length) await loadAgents();
    const [payload, braveConfiguration] = await Promise.all([
      adminRead("/api/v1/admin/research/providers"),
      adminRead("/api/v1/admin/research/providers/brave/configuration"),
    ]);
    admin.researchProviders = payload.providers;
    renderResearchProviders();
    renderBraveConfiguration(braveConfiguration);
    const agent = selectedContextAgent(elements.memoryAgent);
    if (agent) elements.memoryRetention.max = Math.max(1, agent.retentionDays);
    workspaceStatus(elements.researchMessage,
      payload.providers.length ? `${payload.providers.length} search provider${payload.providers.length === 1 ? "" : "s"} available for exact approval.` : "No search provider is configured; memory remains available.",
      payload.providers.length ? "ok" : "");
  } catch (error) {
    workspaceStatus(elements.researchMessage, error instanceof Error ? error.message : "Context capabilities could not be loaded.", "error");
  }
}

async function previewResearch(event) {
  event.preventDefault();
  const agent = selectedContextAgent(elements.researchAgent);
  if (!agent) return;
  setBusy(elements.researchForm, true);
  try {
    const providerIds = [...elements.researchProviders.querySelectorAll("input:checked")].map(input => input.value);
    const preview = await adminMutation("/api/v1/admin/research/preview", {
      expectedAgentVersion: agent.version,
      agentId: agent.id,
      query: elements.researchQuery.value.trim(),
      maximumResults: Number(elements.researchLimit.value),
      providerIds,
    });
    admin.researchPreview = preview;
    elements.researchPreviewDetails.replaceChildren();
    addMeta(elements.researchPreviewDetails, "Agent", `${preview.agent.name} · v${preview.agent.version}`);
    addMeta(elements.researchPreviewDetails, "Query", preview.query);
    addMeta(elements.researchPreviewDetails, "Providers", preview.providers.map(item => item.id).join(", "));
    addMeta(elements.researchPreviewDetails, "Result limit", preview.maximumResults);
    elements.researchPreviewHash.textContent = preview.previewHash;
    elements.researchReview.hidden = false;
    workspaceStatus(elements.researchMessage, preview.warning, "ok");
  } catch (error) {
    workspaceStatus(elements.researchMessage, error instanceof Error ? error.message : "Research preview failed.", "error");
  } finally {
    setBusy(elements.researchForm, false);
  }
}

function discardResearchPreview() {
  admin.researchPreview = null;
  elements.researchReview.hidden = true;
  elements.researchPreviewDetails.replaceChildren();
  elements.researchPreviewHash.textContent = "";
}

async function applyResearch(event) {
  event.preventDefault();
  if (!admin.researchPreview) return;
  setBusy(elements.researchReview, true);
  try {
    const result = await adminMutation("/api/v1/admin/research/apply", { previewHash: admin.researchPreview.previewHash });
    const agentId = admin.researchPreview.agent.id;
    admin.researchReceipt = { ...result, agentId };
    elements.researchResults.replaceChildren();
    for (const citation of result.citations) {
      const card = makeElement("article", "resource-card compact-card");
      const source = document.createElement("a");
      source.href = citation.source;
      source.target = "_blank";
      source.rel = "noreferrer";
      source.textContent = citation.title;
      card.append(source, makeElement("p", "resource-description", citation.excerpt),
        makeElement("p", "resource-subtitle", `${citation.citationId} · ${citation.providerIds.join(", ")}`));
      elements.researchResults.append(card);
    }
    const option = document.createElement("option");
    option.value = result.receiptHash;
    option.dataset.agentId = agentId;
    option.textContent = `${admin.researchPreview.query} · ${result.citations.length} citation${result.citations.length === 1 ? "" : "s"}`;
    elements.runResearchReceipt.append(option);
    if (elements.runAgent.value === agentId) elements.runResearchReceipt.value = result.receiptHash;
    discardResearchPreview();
    workspaceStatus(elements.researchMessage, "Research receipt created. It can now be attached explicitly from Runs.", "ok");
  } catch (error) {
    workspaceStatus(elements.researchMessage, error instanceof Error ? error.message : "Approved research failed.", "error");
  } finally {
    setBusy(elements.researchReview, false);
  }
}

function parseHttpApiHeaders() {
  const headers = {};
  for (const rawLine of elements.httpApiStaticHeaders.value.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) continue;
    const separator = line.indexOf(":");
    if (separator < 1 || !line.slice(separator + 1).trim()) {
      throw new Error("Static headers must use one Name: value pair per line.");
    }
    const name = line.slice(0, separator).trim();
    if (Object.hasOwn(headers, name)) throw new Error(`Duplicate static header: ${name}`);
    headers[name] = line.slice(separator + 1).trim();
  }
  return headers;
}

function renderHttpApiProfiles(payload) {
  admin.httpApiProfiles = payload.profiles || [];
  const current = admin.httpApiProfiles.find(profile => profile.profileId === elements.httpApiProfileId.value)
    || admin.httpApiProfiles[0] || null;
  elements.httpApiConfigState.textContent = current ? (current.isEnabled ? "READY" : "DISABLED") : "NOT CONFIGURED";
  elements.httpApiConfigState.className = `state-chip ${current?.isEnabled ? "completed" : ""}`;
  elements.httpApiConfigSummary.textContent = current
    ? `${current.displayName} · ${current.baseEndpoint} · OS-backed bearer token`
    : "Create a named HTTPS profile for an AI-generated skill. The bearer token never enters skill or model context.";
  if (current) {
    elements.httpApiProfileId.value = current.profileId;
    elements.httpApiDisplayName.value = current.displayName;
    elements.httpApiBaseEndpoint.value = current.baseEndpoint;
    elements.httpApiProbePath.value = current.probeRelativePath;
    elements.httpApiStaticHeaders.value = Object.entries(current.staticHeaders)
      .map(([name, value]) => `${name}: ${value}`).join("\n");
    elements.httpApiConfigEnabled.checked = current.isEnabled;
  }
  elements.httpApiBearerToken.value = "";
}

function discardHttpApiConfigurationPreview() {
  admin.httpApiConfigurationPreview = null;
  elements.httpApiConfigReview.hidden = true;
  elements.httpApiConfigForm.hidden = false;
  elements.httpApiConfigDetails.replaceChildren();
  elements.httpApiConfigPreviewHash.textContent = "";
}

async function previewHttpApiConfiguration(event) {
  event.preventDefault();
  setBusy(elements.httpApiConfigForm, true);
  try {
    const profileId = elements.httpApiProfileId.value.trim();
    const current = admin.httpApiProfiles.find(profile => profile.profileId === profileId);
    const preview = await adminMutation("/api/v1/admin/http-api/profiles/preview", {
      expectedVersion: current?.version ?? null,
      profileId,
      displayName: elements.httpApiDisplayName.value.trim(),
      baseEndpoint: elements.httpApiBaseEndpoint.value.trim(),
      probeRelativePath: elements.httpApiProbePath.value.trim(),
      staticHeaders: parseHttpApiHeaders(),
      isEnabled: elements.httpApiConfigEnabled.checked,
      bearerToken: elements.httpApiBearerToken.value || null,
    });
    admin.httpApiConfigurationPreview = preview;
    elements.httpApiConfigDetails.replaceChildren();
    const verification = preview.verification
      ? `${preview.verification.statusCode} · ${preview.verification.responseBytes.toLocaleString()} bytes · ${Math.round(preview.verification.durationMilliseconds)} ms`
      : "Disabled configuration; no request";
    for (const [label, value] of [
      ["Profile", `${preview.profileId} · ${preview.displayName}`],
      ["State", preview.isEnabled ? "Enabled" : "Disabled"],
      ["Base endpoint", preview.baseEndpoint],
      ["Verification path", preview.probeRelativePath],
      ["Static headers", Object.keys(preview.staticHeaders).join(", ") || "None"],
      ["Credential", preview.credentialAction],
      ["Live verification", verification],
    ]) addMeta(elements.httpApiConfigDetails, label, String(value));
    elements.httpApiConfigPreviewHash.textContent = `Bound preview ${preview.previewHash}`;
    elements.httpApiConfigForm.hidden = true;
    elements.httpApiConfigReview.hidden = false;
    workspaceStatus(elements.httpApiConfigMessage, preview.warning, "ok");
  } catch (error) {
    workspaceStatus(elements.httpApiConfigMessage,
      error instanceof Error ? error.message : "HTTP API profile verification failed.", "error");
  } finally {
    setBusy(elements.httpApiConfigForm, false);
  }
}

async function applyHttpApiConfiguration(event) {
  event.preventDefault();
  if (!admin.httpApiConfigurationPreview) return;
  setBusy(elements.httpApiConfigReview, true);
  try {
    await adminMutation("/api/v1/admin/http-api/profiles/apply", {
      previewHash: admin.httpApiConfigurationPreview.previewHash,
      bearerToken: elements.httpApiBearerToken.value || null,
    });
    elements.httpApiBearerToken.value = "";
    discardHttpApiConfigurationPreview();
    await loadSkills();
    workspaceStatus(elements.httpApiConfigMessage,
      "HTTP API profile applied. The write-only bearer field was cleared.", "ok");
  } catch (error) {
    workspaceStatus(elements.httpApiConfigMessage,
      error instanceof Error ? error.message : "HTTP API profile update failed.", "error");
  } finally {
    setBusy(elements.httpApiConfigReview, false);
  }
}

async function loadSkills(message = "Loading immutable skill packages…") {
  workspaceStatus(elements.skillsMessage, message);
  try {
    const [payload, httpApiProfiles] = await Promise.all([
      adminRead("/api/v1/admin/skills"),
      adminRead("/api/v1/admin/http-api/profiles"),
    ]);
    admin.skillRegistry = payload;
    renderHttpApiProfiles(httpApiProfiles);
    elements.skillList.replaceChildren();
    for (const skill of payload.skills) {
      const card = makeElement("article", "resource-card");
      const title = makeElement("div", "resource-title");
      const titleCopy = document.createElement("div");
      titleCopy.append(
        makeElement("h3", "", skill.id),
        makeElement("p", "resource-subtitle", `${skill.version} · ${skill.provenance} · record ${skill.recordVersion}`));
      title.append(titleCopy, stateChip(skill.status));
      const meta = makeElement("div", "resource-meta");
      addMeta(meta, "Permissions", skill.permissions.length ? skill.permissions.join(", ") : "None");
      addMeta(meta, "Operating systems", skill.operatingSystems.length ? skill.operatingSystems.join(", ") : "Portable");
      addMeta(meta, "Package hash", `${skill.packageHash.slice(0, 24)}…`);
      addMeta(meta, "Promotion", skill.status === "Active" ? "Active" : "Evaluation required");
      card.append(title, makeElement("p", "resource-description", skill.description), meta);
      const actions = makeElement("div", "resource-actions");
      if (skill.status === "Installed") {
        const hasOpenProposal = payload.proposals.some(proposal =>
          proposal.skillId === skill.id && proposal.candidateVersion === skill.version &&
          ["Proposed", "AwaitingApproval", "Approved", "Canary"].includes(proposal.state));
        const activate = makeElement("button", "secondary-action", hasOpenProposal ? "Activation in progress" : "Begin activation review");
        activate.type = "button";
        activate.disabled = hasOpenProposal;
        activate.addEventListener("click", () => createSkillProposal(skill));
        actions.append(activate);
      }
      if (skill.status === "Active") {
        for (const agent of payload.agents) {
          const granted = agent.skillGrants.includes(skill.id);
          const grant = makeElement("button", granted ? "danger-action" : "secondary-action",
            `${granted ? "Revoke from" : "Grant to"} ${agent.name}`);
          grant.type = "button";
          grant.addEventListener("click", () => previewSkillGrant(agent, skill, !granted));
          actions.append(grant);
        }
      }
      if (actions.childElementCount) card.append(actions);
      elements.skillList.append(card);
    }
    if (!payload.skills.length) elements.skillList.append(makeElement("div", "empty-state", "No skills installed. The bundled starter can be validated and installed above."));
    elements.installSeedSkill.disabled = payload.seedAvailable !== true || payload.skills.some(skill => skill.id === "skill:csharp.review");
    elements.installSeedSkill.textContent = payload.skills.some(skill => skill.id === "skill:csharp.review") ? "Starter installed" : "Install starter skill";
    renderSkillProposals(payload.proposals);
    workspaceStatus(elements.skillsMessage, `${payload.skills.length} registered skill ${payload.skills.length === 1 ? "version" : "versions"} loaded.`, "ok");
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "Skills could not be loaded.", "error");
  }
}

async function createSkillProposal(skill) {
  workspaceStatus(elements.skillsMessage, `Creating a version-bound activation proposal for ${skill.id} ${skill.version}…`);
  try {
    const result = await adminMutation("/api/v1/admin/skills/proposals", { skillId: skill.id, version: skill.version });
    await loadSkills(`${result.skillId} entered ${result.state}. Complete each visible gate before it can become active.`);
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "The activation proposal could not be created.", "error");
  }
}

function skillGateAction(state) {
  return {
    Proposed: "evaluate",
    AwaitingApproval: "approve",
    Approved: "start-canary",
    Canary: "finish-canary",
    Promoted: "rollback",
  }[state] || null;
}

function skillGateLabel(action) {
  return {
    evaluate: "Record evaluation",
    approve: "Approve exact package",
    "start-canary": "Start scoped canary",
    "finish-canary": "Finish canary",
    rollback: "Roll back promotion",
  }[action];
}

function renderSkillProposals(proposals) {
  elements.skillProposalList.replaceChildren();
  for (const proposal of proposals) {
    const card = makeElement("article", "resource-card learning-card");
    const title = makeElement("div", "resource-title");
    const copy = document.createElement("div");
    copy.append(makeElement("h3", "", proposal.skillId),
      makeElement("p", "resource-subtitle", `${proposal.candidateVersion} · proposal v${proposal.version}`));
    title.append(copy, stateChip(proposal.state));
    const meta = makeElement("div", "resource-meta");
    addMeta(meta, "Next gate", proposal.nextGate.replaceAll("-", " "));
    addMeta(meta, "Candidate hash", `${proposal.candidatePackageHash.slice(0, 24)}…`);
    addMeta(meta, "Added permissions", proposal.addedPermissions.length ? proposal.addedPermissions.join(", ") : "None");
    addMeta(meta, "Authority", proposal.activeAuthority ? "Active exact version" : "None");
    card.append(title, meta);
    const action = skillGateAction(proposal.state);
    if (action) {
      const actions = makeElement("div", "resource-actions");
      const button = makeElement("button", action === "rollback" ? "danger-action" : "secondary-action", skillGateLabel(action));
      button.type = "button";
      button.addEventListener("click", () => openSkillGate(proposal, action));
      actions.append(button);
      card.append(actions);
    }
    elements.skillProposalList.append(card);
  }
  if (!proposals.length) elements.skillProposalList.append(makeElement("div", "empty-state",
    "No activation proposals. Begin a review from an installed skill above."));
}

function skillEvidenceField() {
  const label = document.createElement("label");
  label.append(document.createTextNode("Evidence note "));
  label.append(makeElement("span", "field-note", "Hashed in this browser; note text is not sent or stored."));
  const textarea = document.createElement("textarea");
  textarea.id = "skill-gate-evidence";
  textarea.rows = 4;
  textarea.maxLength = 4096;
  textarea.required = true;
  textarea.placeholder = "Reference deterministic tests, adversarial checks, canary observations, or rollback evidence.";
  label.append(textarea);
  return label;
}

function openSkillGate(proposal, action) {
  admin.selectedSkillProposal = proposal;
  admin.skillGateAction = action;
  elements.skillGateFields.replaceChildren();
  elements.skillGateTitle.textContent = skillGateLabel(action);
  elements.skillGateSource.textContent = `${proposal.skillId} ${proposal.candidateVersion} · proposal version ${proposal.version}`;
  elements.submitSkillGate.textContent = skillGateLabel(action);
  if (action === "evaluate") {
    const checks = makeElement("fieldset", "learning-gate-checks");
    checks.append(gateCheck("Target tests passed", "skill-gate-target"),
      gateCheck("Holdout tests passed", "skill-gate-holdout"),
      gateCheck("Adversarial tests passed", "skill-gate-adversarial"));
    const metrics = makeElement("div", "learning-proposal-grid");
    metrics.append(gateField("Baseline score", "skill-gate-baseline", "number", "0"),
      gateField("Candidate score", "skill-gate-candidate", "number", "1"));
    elements.skillGateFields.append(checks, metrics, skillEvidenceField());
    elements.skillGateExplanation.textContent = "Any failed deterministic check or candidate regression rejects this exact package.";
  } else if (action === "finish-canary") {
    const metrics = makeElement("div", "learning-proposal-grid");
    metrics.append(gateField("Baseline metric", "skill-gate-baseline", "number", "0"),
      gateField("Candidate metric", "skill-gate-candidate", "number", "1"));
    elements.skillGateFields.append(gateCheck("Scoped canary passed", "skill-gate-passed"), metrics, skillEvidenceField());
    elements.skillGateExplanation.textContent = "Promotion occurs only when this exact candidate passes and meets the baseline.";
  } else if (action === "rollback") {
    elements.skillGateFields.append(skillEvidenceField());
    elements.skillGateExplanation.textContent = "Rollback removes active authority atomically and restores the exact recorded baseline when one exists.";
  } else if (action === "approve") {
    elements.skillGateExplanation.textContent = "A separate governor actor approves the exact package and baseline hashes. No active authority is granted yet.";
  } else {
    elements.skillGateExplanation.textContent = "The canary is scoped to the already-approved package. A passing receipt is still required for promotion.";
  }
  elements.skillGateForm.hidden = false;
  elements.skillGateForm.scrollIntoView({ behavior: "smooth", block: "center" });
}

function closeSkillGate() {
  admin.selectedSkillProposal = null;
  admin.skillGateAction = null;
  elements.skillGateForm.hidden = true;
  elements.skillGateFields.replaceChildren();
}

async function skillEvidenceHash(proposal, action) {
  const note = document.querySelector("#skill-gate-evidence")?.value.trim();
  if (!note) return null;
  const bytes = new TextEncoder().encode(`${proposal.id}\n${proposal.version}\n${action}\n${note}`);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return `sha256:${Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("")}`;
}

async function transitionSkillProposal(event) {
  event.preventDefault();
  const proposal = admin.selectedSkillProposal;
  const action = admin.skillGateAction;
  if (!proposal || !action) return;
  setBusy(elements.skillGateForm, true);
  try {
    const payload = { action, expectedVersion: proposal.version, evidenceHash: await skillEvidenceHash(proposal, action) };
    if (action === "evaluate") Object.assign(payload, {
      targetPassed: document.querySelector("#skill-gate-target").checked,
      holdoutPassed: document.querySelector("#skill-gate-holdout").checked,
      adversarialPassed: document.querySelector("#skill-gate-adversarial").checked,
      baselineMetric: Number(document.querySelector("#skill-gate-baseline").value),
      candidateMetric: Number(document.querySelector("#skill-gate-candidate").value),
    });
    if (action === "finish-canary") Object.assign(payload, {
      passed: document.querySelector("#skill-gate-passed").checked,
      baselineMetric: Number(document.querySelector("#skill-gate-baseline").value),
      candidateMetric: Number(document.querySelector("#skill-gate-candidate").value),
    });
    const result = await adminMutation(`/api/v1/admin/skills/proposals/${proposal.id}/transition`, payload);
    closeSkillGate();
    await loadSkills(`${result.skillId} advanced to ${result.state}.`);
    await loadRunOptions();
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "The activation gate could not be recorded.", "error");
  } finally {
    setBusy(elements.skillGateForm, false);
  }
}

function closeSkillGrant() {
  admin.skillGrantPreview = null;
  elements.skillGrantForm.hidden = true;
  elements.skillGrantChanges.replaceChildren();
  elements.skillGrantPreviewHash.textContent = "";
}

async function previewSkillGrant(agent, skill, grant) {
  closeSkillGrant();
  workspaceStatus(elements.skillsMessage, `Preparing an exact ${grant ? "grant" : "revocation"} preview…`);
  try {
    const preview = await adminMutation(`/api/v1/admin/agents/${agent.id}/skill-grants/preview`, {
      expectedInstallationVersion: admin.skillRegistry.installationVersion,
      expectedAgentVersion: agent.version,
      skillId: skill.id,
      grant,
    });
    admin.skillGrantPreview = { agentId: agent.id, previewHash: preview.previewHash, grant };
    elements.skillGrantTitle.textContent = `${grant ? "Grant" : "Revoke"} ${skill.id}`;
    elements.skillGrantSource.textContent = `${agent.name} · ${skill.version} · ${skill.packageHash}`;
    elements.skillGrantWarning.textContent = preview.warning;
    elements.skillGrantChanges.replaceChildren();
    for (const change of preview.changes) {
      const item = document.createElement("div");
      item.append(makeElement("span", "", change.path), makeElement("strong", "", "One exact skill authority change"));
      elements.skillGrantChanges.append(item);
    }
    elements.skillGrantPreviewHash.textContent = `Bound preview ${preview.previewHash}`;
    elements.applySkillGrant.textContent = grant ? "Apply exact grant" : "Apply exact revocation";
    elements.skillGrantForm.hidden = false;
    elements.skillGrantForm.scrollIntoView({ behavior: "smooth", block: "center" });
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "The skill grant could not be previewed.", "error");
  }
}

async function applySkillGrant(event) {
  event.preventDefault();
  if (!admin.skillGrantPreview) return;
  setBusy(elements.skillGrantForm, true);
  try {
    const preview = admin.skillGrantPreview;
    const result = await adminMutation(`/api/v1/admin/agents/${preview.agentId}/skill-grants/apply`, {
      previewHash: preview.previewHash,
    });
    closeSkillGrant();
    await Promise.all([loadSkills(), loadAgents(), loadRunOptions()]);
    workspaceStatus(elements.skillsMessage, `${result.skillId} ${result.granted ? "granted to" : "revoked from"} ${result.agent.name}.`, "ok");
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "The skill grant could not be applied.", "error");
  } finally {
    setBusy(elements.skillGrantForm, false);
  }
}

async function installSeedSkill() {
  elements.installSeedSkill.disabled = true;
  try {
    const result = await adminMutation("/api/v1/admin/skills/seed/csharp-review/install");
    await loadSkills(`${result.skill.id} ${result.skill.version} installed. Refreshing registry…`);
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "The starter skill could not be installed.", "error");
    elements.installSeedSkill.disabled = false;
  }
}

function selectedTool() {
  return admin.toolCatalog?.tools.find(tool => `${tool.id}@${tool.version}` === elements.toolSelector.value) ?? null;
}

function selectedToolAgent() {
  return admin.toolCatalog?.agents.find(agent => agent.id === elements.toolAgent.value) ?? null;
}

function populateToolComposer() {
  const previousAgent = elements.toolAgent.value;
  elements.toolAgent.replaceChildren();
  for (const agent of admin.toolCatalog?.agents ?? []) {
    const option = document.createElement("option");
    option.value = agent.id;
    option.textContent = `${agent.name} · policy v${agent.version}`;
    elements.toolAgent.append(option);
  }
  if ([...elements.toolAgent.options].some(option => option.value === previousAgent)) elements.toolAgent.value = previousAgent;
  populateToolOptions();
}

function populateToolOptions() {
  const agent = selectedToolAgent();
  const previous = elements.toolSelector.value;
  elements.toolSelector.replaceChildren();
  for (const tool of admin.toolCatalog?.tools ?? []) {
    const option = document.createElement("option");
    option.value = `${tool.id}@${tool.version}`;
    const granted = agent?.toolGrants.includes(tool.capabilityId) === true;
    option.textContent = `${tool.name} · ${granted ? "granted" : "grant required"}`;
    option.disabled = !granted;
    elements.toolSelector.append(option);
  }
  if ([...elements.toolSelector.options].some(option => option.value === previous && !option.disabled)) {
    elements.toolSelector.value = previous;
  } else {
    const first = [...elements.toolSelector.options].find(option => !option.disabled);
    if (first) elements.toolSelector.value = first.value;
  }
  elements.toolWorkspace.value = agent?.defaultWorkspace || elements.toolWorkspace.value;
  renderToolParameters();
}

function renderToolParameters() {
  const tool = selectedTool();
  const agent = selectedToolAgent();
  elements.toolParameterFields.replaceChildren();
  if (!tool || !agent?.toolGrants.includes(tool.capabilityId)) {
    elements.toolRequestSummary.textContent = "Grant a catalog capability to this agent before preparing an invocation.";
    elements.previewToolInvocation.disabled = true;
    return;
  }

  elements.previewToolInvocation.disabled = false;
  for (const parameter of tool.parameters) {
    const label = document.createElement("label");
    label.append(document.createTextNode(parameter.name));
    const note = makeElement("span", "field-note", parameter.description);
    label.append(note);
    let input;
    if (parameter.type === "Switch") {
      input = document.createElement("select");
      input.append(new Option("False", "false"), new Option("True", "true"));
    } else {
      input = document.createElement("input");
      if (parameter.type === "WholeNumber") {
        input.type = "number";
        input.min = String(parameter.minimumInteger);
        input.max = String(parameter.maximumInteger);
        input.value = String(Math.min(parameter.maximumInteger, parameter.name === "maximumEntries" ? 100 : 65536));
      } else {
        input.type = "text";
        input.maxLength = parameter.maximumLength;
        if (parameter.name === tool.targetParameterName && tool.id === "tool:workspace.list") {
          input.value = elements.toolWorkspace.value;
        }
      }
    }
    input.dataset.toolParameter = parameter.name;
    input.dataset.toolType = parameter.type;
    input.required = parameter.required;
    label.append(input);
    elements.toolParameterFields.append(label);
  }
  elements.toolRequestSummary.textContent = `${tool.capabilityId} · ${tool.riskClass} · ${tool.sandbox} sandbox · network ${tool.networkPolicy.toLowerCase()}`;
}

async function loadTools(message = "Loading authoritative tool descriptors…") {
  workspaceStatus(elements.toolsMessage, message);
  try {
    const payload = await adminRead("/api/v1/admin/tools");
    admin.toolCatalog = payload;
    elements.toolList.replaceChildren();
    for (const tool of payload.tools) {
      const card = makeElement("article", "resource-card");
      const title = makeElement("div", "resource-title");
      const copy = document.createElement("div");
      copy.append(makeElement("h3", "", tool.name),
        makeElement("p", "resource-subtitle", `${tool.id}@${tool.version} · ${tool.executionKind}`));
      title.append(copy, stateChip(tool.riskClass));
      const meta = makeElement("div", "resource-meta");
      addMeta(meta, "Capability", tool.capabilityId);
      addMeta(meta, "Side effects", tool.sideEffects);
      addMeta(meta, "Sandbox / network", `${tool.sandbox} / ${tool.networkPolicy}`);
      addMeta(meta, "Descriptor hash", `${tool.descriptorHash.slice(0, 31)}…`);
      card.append(title, makeElement("p", "resource-description", tool.description), meta);
      const actions = makeElement("div", "resource-actions");
      for (const agent of payload.agents) {
        const granted = agent.toolGrants.includes(tool.capabilityId);
        const button = makeElement("button", granted ? "danger-action" : "secondary-action",
          `${granted ? "Revoke from" : "Grant to"} ${agent.name}`);
        button.type = "button";
        button.addEventListener("click", () => previewToolGrant(agent, tool, !granted));
        actions.append(button);
      }
      card.append(actions);
      elements.toolList.append(card);
    }
    if (!payload.tools.length) elements.toolList.append(makeElement("div", "empty-state", "No authoritative tools are installed."));
    populateToolComposer();
    workspaceStatus(elements.toolsMessage,
      `${payload.tools.length} immutable descriptor${payload.tools.length === 1 ? "" : "s"} loaded.`, "ok");
  } catch (error) {
    workspaceStatus(elements.toolsMessage, error instanceof Error ? error.message : "Tools could not be loaded.", "error");
  }
}

function closeToolGrant() {
  admin.toolGrantPreview = null;
  elements.toolGrantForm.hidden = true;
  elements.toolGrantChanges.replaceChildren();
  elements.toolGrantPreviewHash.textContent = "";
}

async function previewToolGrant(agent, tool, grant) {
  closeToolGrant();
  workspaceStatus(elements.toolsMessage, `Preparing an exact ${grant ? "grant" : "revocation"} preview…`);
  try {
    const preview = await adminMutation(`/api/v1/admin/agents/${agent.id}/tool-grants/preview`, {
      expectedInstallationVersion: admin.toolCatalog.installationVersion,
      expectedAgentVersion: agent.version,
      capabilityId: tool.capabilityId,
      grant,
      maximumToolInvocations: grant ? 10 : agent.maxToolInvocations,
    });
    admin.toolGrantPreview = { agentId: agent.id, previewHash: preview.previewHash, grant };
    elements.toolGrantTitle.textContent = `${grant ? "Grant" : "Revoke"} ${tool.capabilityId}`;
    elements.toolGrantSource.textContent = `${agent.name} · ${preview.descriptors.length} exact descriptor${preview.descriptors.length === 1 ? "" : "s"}`;
    elements.toolGrantWarning.textContent = preview.warning;
    elements.toolGrantChanges.replaceChildren();
    for (const change of preview.changes) {
      const item = document.createElement("div");
      item.append(makeElement("span", "", change.path),
        makeElement("strong", "", change.path === "agent.budget" ? `${preview.maximumToolInvocations} calls per run` : "One exact capability"));
      elements.toolGrantChanges.append(item);
    }
    elements.toolGrantPreviewHash.textContent = `Bound preview ${preview.previewHash}`;
    elements.applyToolGrant.textContent = grant ? "Apply exact grant" : "Apply exact revocation";
    elements.toolGrantForm.hidden = false;
    elements.toolGrantForm.scrollIntoView({ behavior: "smooth", block: "center" });
  } catch (error) {
    workspaceStatus(elements.toolsMessage, error instanceof Error ? error.message : "The tool grant could not be previewed.", "error");
  }
}

async function applyToolGrant(event) {
  event.preventDefault();
  if (!admin.toolGrantPreview) return;
  setBusy(elements.toolGrantForm, true);
  try {
    const preview = admin.toolGrantPreview;
    const result = await adminMutation(`/api/v1/admin/agents/${preview.agentId}/tool-grants/apply`, {
      previewHash: preview.previewHash,
    });
    closeToolGrant();
    await Promise.all([loadTools(), loadAgents()]);
    workspaceStatus(elements.toolsMessage,
      `${result.capabilityId} ${result.granted ? "granted" : "revoked"}; invocation ceiling ${result.maximumToolInvocations}.`, "ok");
  } catch (error) {
    workspaceStatus(elements.toolsMessage, error instanceof Error ? error.message : "The tool grant could not be applied.", "error");
  } finally {
    setBusy(elements.toolGrantForm, false);
  }
}

function closeToolApproval() {
  admin.toolInvocationPreview = null;
  elements.toolApprovalForm.hidden = true;
  elements.toolApprovalDetails.replaceChildren();
  elements.toolApprovalPreviewHash.textContent = "";
}

async function previewToolInvocation(event) {
  event.preventDefault();
  closeToolApproval();
  const tool = selectedTool();
  const agent = selectedToolAgent();
  if (!tool || !agent) return;
  const parameters = {};
  for (const input of elements.toolParameterFields.querySelectorAll("[data-tool-parameter]")) {
    parameters[input.dataset.toolParameter] = input.dataset.toolType === "WholeNumber"
      ? Number(input.value)
      : input.dataset.toolType === "Switch" ? input.value === "true" : input.value;
  }
  setBusy(elements.toolInvocationForm, true);
  try {
    const preview = await adminMutation(`/api/v1/admin/agents/${agent.id}/tool-invocations/preview`, {
      expectedInstallationVersion: admin.toolCatalog.installationVersion,
      expectedAgentVersion: agent.version,
      toolId: tool.id,
      toolVersion: tool.version,
      workspace: elements.toolWorkspace.value.trim(),
      parameters,
      disposition: elements.toolDisposition.value,
      approvalSeconds: 300,
    });
    admin.toolInvocationPreview = { agentId: agent.id, previewHash: preview.previewHash };
    const denying = preview.disposition === "Deny";
    elements.toolApprovalTitle.textContent = denying ? "Record exact denial" : "Approve one exact execution";
    elements.toolApprovalSource.textContent = `${preview.tool.id}@${preview.tool.version} · expires ${new Date(preview.expiresAt).toLocaleTimeString()}`;
    elements.toolApprovalWarning.textContent = preview.warning;
    elements.toolApprovalDetails.replaceChildren();
    const details = [
      ["Descriptor", preview.tool.descriptorHash],
      ["Capability / risk", `${preview.tool.capabilityId} / ${preview.tool.riskClass}`],
      ["Parameters", preview.parametersJson],
      ["Target", preview.targetJson],
      ["Workspace", preview.workspaceJson],
      ["Sandbox / network", `${preview.tool.sandbox} / ${preview.tool.networkPolicy}`],
    ];
    for (const [label, value] of details) {
      const item = document.createElement("div");
      item.append(makeElement("span", "", label), makeElement("strong", "", value));
      elements.toolApprovalDetails.append(item);
    }
    elements.toolApprovalPreviewHash.textContent = `Approval ${preview.previewHash} · request ${preview.requestHash}`;
    elements.applyToolInvocation.textContent = denying ? "Record exact denial" : "Approve and run once";
    elements.toolApprovalForm.hidden = false;
    elements.toolApprovalForm.scrollIntoView({ behavior: "smooth", block: "center" });
  } catch (error) {
    workspaceStatus(elements.toolsMessage, error instanceof Error ? error.message : "The exact tool request could not be previewed.", "error");
  } finally {
    setBusy(elements.toolInvocationForm, false);
  }
}

async function applyToolInvocation(event) {
  event.preventDefault();
  if (!admin.toolInvocationPreview) return;
  setBusy(elements.toolApprovalForm, true);
  try {
    const preview = admin.toolInvocationPreview;
    const result = await adminMutation(`/api/v1/admin/agents/${preview.agentId}/tool-invocations/apply`, {
      previewHash: preview.previewHash,
    });
    closeToolApproval();
    elements.toolOutput.hidden = false;
    elements.toolOutputState.className = `state-chip ${result.executed ? "active" : ""}`;
    elements.toolOutputState.textContent = result.executed ? result.state : result.disposition;
    elements.toolOutputText.textContent = result.executed ? result.output || result.standardError || "(no output)" : "No tool execution occurred.";
    elements.toolOutputMeta.textContent = result.executed
      ? `${result.outputLength} bytes · ${result.outputHash} · ${result.sandbox.kind}`
      : `Exact request ${result.requestHash} denied until ${new Date(result.expiresAt).toLocaleTimeString()}.`;
    workspaceStatus(elements.toolsMessage,
      result.executed ? "Single-use approval consumed; bounded execution completed." : "Exact denial recorded; no execution occurred.", "ok");
  } catch (error) {
    workspaceStatus(elements.toolsMessage, error instanceof Error ? error.message : "The tool request could not be applied.", "error");
  } finally {
    setBusy(elements.toolApprovalForm, false);
  }
}

function populateLearningSources() {
  const selected = admin.pendingLearningTaskId || elements.learningSourceRun.value;
  const terminalStates = ["Completed", "Failed", "Canceled", "DeadLettered"];
  elements.learningSourceRun.replaceChildren();
  for (const run of admin.runs.filter(item => terminalStates.includes(item.state) &&
    (item.sourceTaskId || !item.conversationId))) {
    const option = document.createElement("option");
    option.value = run.sourceTaskId || run.taskId;
    option.textContent = `${run.name} · ${run.state} · ${formatRunTime(run.updatedAt)}`;
    elements.learningSourceRun.append(option);
  }
  if (admin.runs.some(run => (run.sourceTaskId || run.taskId) === selected && terminalStates.includes(run.state))) {
    elements.learningSourceRun.value = selected;
  }
  admin.pendingLearningTaskId = null;
  const available = elements.learningSourceRun.options.length > 0;
  const formBusy = elements.learningForm.getAttribute("aria-busy") === "true";
  elements.learningSourceRun.disabled = !available || formBusy;
  elements.captureLearning.disabled = !available || formBusy;
}

function learningActionCopy(action) {
  return {
    Memory: "Retain as scoped evidence; it has no authority to revise a skill.",
    NewSkill: "Eligible for isolated candidate generation; no package has been created yet.",
    SkillRevision: "Eligible only with an exact successful usage receipt or explicit revision authorization.",
    Bundle: "Eligible only from a repeated exact multi-skill chain with compatible contracts.",
    NoDurableAction: "No durable learning action is justified by this evidence.",
  }[action] || "The deterministic classifier recorded this evidence without granting authority.";
}

function renderLearningSignals(signals) {
  elements.learningList.replaceChildren();
  for (const signal of signals) {
    const card = makeElement("article", "resource-card learning-card");
    const title = makeElement("div", "resource-title");
    const titleCopy = document.createElement("div");
    titleCopy.append(
      makeElement("h3", "", signal.kind.replace(/([a-z])([A-Z])/g, "$1 $2")),
      makeElement("p", "resource-subtitle", `${signal.id} · ${formatRunTime(signal.capturedAt)}`));
    title.append(titleCopy, stateChip(signal.action));
    const meta = makeElement("div", "resource-meta");
    addMeta(meta, "Classification", signal.reasonCode);
    addMeta(meta, "Occurrences", signal.occurrenceCount);
    addMeta(meta, "Source run", signal.sourceRunId || "Hash-bound evidence");
    addMeta(meta, "Evidence hash", `${signal.sourceEvidenceHash.slice(0, 24)}…`);
    card.append(
      title,
      makeElement("p", "resource-description", signal.summary),
      makeElement("p", "learning-disposition", learningActionCopy(signal.action)),
      meta);
    if (signal.action === "NewSkill") {
      const existing = admin.learningCandidates.some(candidate => candidate.signalId === signal.id);
      const actions = makeElement("div", "resource-actions");
      const propose = makeElement("button", "secondary-action",
        existing ? "Proposal created" : "Generate with local agent");
      propose.type = "button";
      propose.disabled = existing;
      propose.addEventListener("click", () => openLearningProposal(signal));
      actions.append(propose);
      card.append(actions);
    }
    elements.learningList.append(card);
  }
  if (!signals.length) {
    elements.learningList.append(makeElement("div", "empty-state", "No learning evidence captured. Use a terminal run receipt to start the governed intake path."));
  }
}

function proposalSlug(summary) {
  const slug = summary.toLowerCase().replace(/[^a-z0-9]+/g, ".").replace(/^\.+|\.+$/g, "")
    .split(".").filter(Boolean).slice(0, 5).join(".");
  return slug || "missing.capability";
}

function openLearningProposal(signal) {
  admin.selectedLearningSignal = signal;
  elements.learningProposalTitle.textContent = "Generate candidate skill";
  elements.learningProposalSource.textContent = `${signal.kind} · ${signal.id}`;
  elements.learningProposalSkillId.value = `skill:proposal.${proposalSlug(signal.summary)}`;
  elements.learningProposalVersion.value = "0.1.0";
  elements.learningProposalDescription.value = signal.summary.slice(0, 512);
  elements.learningProposalPermissions.value = "";
  elements.learningProposalHttpApi.checked = false;
  elements.learningProposalGuidance.value = "";
  elements.learningProposalForm.hidden = false;
  elements.learningProposalSkillId.focus();
  elements.learningProposalForm.scrollIntoView({ behavior: "smooth", block: "center" });
}

function closeLearningProposal() {
  admin.selectedLearningSignal = null;
  elements.learningProposalForm.hidden = true;
  elements.learningProposalForm.reset();
  elements.learningProposalVersion.value = "0.1.0";
}

function renderLearningCandidates(candidates) {
  elements.learningCandidateList.replaceChildren();
  for (const candidate of candidates) {
    const card = makeElement("article", "resource-card learning-candidate-card");
    const title = makeElement("div", "resource-title");
    const titleCopy = document.createElement("div");
    titleCopy.append(
      makeElement("h3", "", `${candidate.skillId} ${candidate.candidateVersion}`),
      makeElement("p", "resource-subtitle", `${candidate.id} · version ${candidate.version}`));
    title.append(titleCopy, stateChip(candidate.state));
    const meta = makeElement("div", "resource-meta");
    addMeta(meta, "Next gate", candidate.nextGate.replace(/-/g, " "));
    addMeta(meta, "Authority", candidate.activeAuthority ? "Active" : "None — package is inert");
    addMeta(meta, "Package hash", `${candidate.candidatePackageHash.slice(0, 24)}…`);
    addMeta(meta, "Workspace hash", `${candidate.proposalWorkspace.contentHash.slice(0, 24)}…`);
    addMeta(meta, "Permissions", candidate.requestedPermissions.length
      ? candidate.requestedPermissions.join(", ") : "None declared");
    addMeta(meta, "Role separation", "Worker · proposer · verifier · critic · governor");
    const latestGeneration = admin.lastLearningGeneration;
    if (latestGeneration?.candidateId === candidate.id) {
      addMeta(meta, "Generated by", `${latestGeneration.model} · agent v${latestGeneration.agentVersion}`);
      addMeta(meta, "Model evidence", `${latestGeneration.modelEvidenceHash.slice(0, 24)}…`);
      addMeta(meta, "Generated body", `${latestGeneration.selectedMarkdownHash.slice(0, 24)}…`);
    }
    if (candidate.evaluation) {
      addMeta(meta, "Automated score", `${candidate.evaluation.candidateScore} / 100`);
      addMeta(meta, "Evaluation receipt", `${candidate.evaluation.evidenceHash.slice(0, 24)}…`);
    }
    card.append(
      title,
      makeElement("p", "learning-disposition",
        candidate.activeAuthority
          ? "The immutable package passed its governed canary and is active. Individual agents still require an exact skill grant before selection."
          : "The immutable AgentProposal package has no active authority. It cannot be selected by a run or activate itself."),
      meta);
    const latestReceipt = admin.lastLearningEvaluation;
    if (latestReceipt?.candidateId === candidate.id) {
      const evaluation = makeElement("div", "learning-evaluation-receipt");
      evaluation.append(makeElement("strong", "", "Latest automated evaluation"));
      const checks = makeElement("ul", "learning-evaluation-checks");
      for (const check of latestReceipt.checks) {
        checks.append(makeElement("li", check.passed ? "pass" : "fail",
          `${check.passed ? "PASS" : "FAIL"} · ${check.code} — ${check.summary}`));
      }
      evaluation.append(checks, makeElement("p", "resource-subtitle",
        `${latestReceipt.evaluator} · ${latestReceipt.evidence.contentHash}`));
      card.append(evaluation);
    }
    const action = candidateGateAction(candidate.state);
    if (action) {
      const actions = makeElement("div", "resource-actions");
      const gate = makeElement("button", action === "rollback" ? "danger-action" : "secondary-action",
        candidateGateLabel(action));
      gate.type = "button";
      gate.addEventListener("click", () => openLearningGate(candidate, action));
      actions.append(gate);
      card.append(actions);
    }
    elements.learningCandidateList.append(card);
  }
  if (!candidates.length) {
    elements.learningCandidateList.append(makeElement("div", "empty-state",
      "No candidates yet. A NewSkill signal may create one isolated, inactive package."));
  }
}

function candidateGateAction(state) {
  return {
    Proposed: "evaluate",
    Verified: "critique",
    Critiqued: "approve",
    Approved: "start-canary",
    Canary: "finish-canary",
    Promoted: "rollback",
  }[state] || null;
}

function candidateGateLabel(action) {
  return {
    evaluate: "Run isolated evaluation",
    critique: "Record critique",
    approve: "Approve candidate",
    "start-canary": "Start scoped canary",
    "finish-canary": "Finish canary",
    rollback: "Roll back promotion",
  }[action];
}

function gateField(labelText, id, type = "text", value = "") {
  const label = document.createElement("label");
  label.textContent = labelText;
  const input = document.createElement("input");
  input.id = id;
  input.type = type;
  input.value = value;
  if (type === "number") {
    input.min = "0";
    input.max = "1000000";
    input.step = "0.01";
  }
  label.append(input);
  return label;
}

function gateCheck(labelText, id) {
  const label = makeElement("label", "gate-check");
  const input = document.createElement("input");
  input.id = id;
  input.type = "checkbox";
  label.append(input, document.createTextNode(labelText));
  return label;
}

function evidenceField() {
  const label = document.createElement("label");
  label.append(document.createTextNode("Evidence note "));
  label.append(makeElement("span", "field-note", "Hashed in this browser; note text is not sent or stored."));
  const textarea = document.createElement("textarea");
  textarea.id = "learning-gate-evidence";
  textarea.rows = 4;
  textarea.maxLength = 4096;
  textarea.required = true;
  textarea.placeholder = "Reference the retained test transcript, review, or rollback evidence without including secrets.";
  label.append(textarea);
  return label;
}

function openLearningGate(candidate, action) {
  admin.selectedLearningCandidate = candidate;
  admin.learningGateAction = action;
  elements.learningGateFields.replaceChildren();
  elements.learningGateTitle.textContent = candidateGateLabel(action);
  elements.learningGateSource.textContent = `${candidate.skillId} ${candidate.candidateVersion} · snapshot version ${candidate.version}`;
  elements.submitLearningGate.textContent = candidateGateLabel(action);
  const fields = elements.learningGateFields;
  if (action === "evaluate") {
    const summary = makeElement("div", "policy-note");
    summary.append(
      makeElement("strong", "", "Server-owned deterministic gate"),
      makeElement("p", "", "AgentForge will reopen the immutable artifact in a fresh managed sandbox, verify its hash and package contract, reload it against holdout invariants, scan hostile authority-escalation patterns, and enforce an exact bounded permission diff."));
    fields.append(summary);
    elements.learningGateExplanation.textContent = "No pass flags or scores come from this browser. The content-addressed evaluator receipt decides whether the candidate advances or is rejected.";
  } else if (action === "critique") {
    fields.append(gateCheck("Independent critique passed", "learning-gate-passed"),
      gateField("Finding codes (comma-separated)", "learning-gate-findings"), evidenceField());
    elements.learningGateExplanation.textContent = "The critic is separate from the proposer and verifier. A failed critique rejects the candidate.";
  } else if (action === "finish-canary") {
    const metrics = makeElement("div", "learning-proposal-grid");
    metrics.append(gateField("Baseline metric", "learning-gate-baseline", "number", "0"),
      gateField("Candidate metric", "learning-gate-candidate", "number", "1"));
    fields.append(gateCheck("Scoped canary passed", "learning-gate-passed"), metrics, evidenceField());
    elements.learningGateExplanation.textContent = "A passing canary promotes only when its candidate metric meets or exceeds the recorded baseline; failure quarantines the package.";
  } else if (action === "rollback") {
    fields.append(evidenceField());
    elements.learningGateExplanation.textContent = "Rollback atomically removes active authority and restores the exact baseline recorded by governance.";
  } else if (action === "approve") {
    elements.learningGateExplanation.textContent = "Governor approval is bound to the current package and baseline hashes. It does not activate the candidate; a scoped canary is still required.";
  } else {
    elements.learningGateExplanation.textContent = "Starting the canary grants only the scope already approved by governance. Promotion still requires a passing canary receipt.";
  }
  elements.learningGateForm.hidden = false;
  elements.learningGateForm.scrollIntoView({ behavior: "smooth", block: "center" });
}

function closeLearningGate() {
  admin.selectedLearningCandidate = null;
  admin.learningGateAction = null;
  elements.learningGateForm.hidden = true;
  elements.learningGateFields.replaceChildren();
}

async function learningEvidenceHash(candidate, action) {
  const note = document.querySelector("#learning-gate-evidence")?.value.trim();
  if (!note) return null;
  const bytes = new TextEncoder().encode(`${candidate.id}\n${candidate.version}\n${action}\n${note}`);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return `sha256:${Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("")}`;
}

async function transitionLearningCandidate(event) {
  event.preventDefault();
  const candidate = admin.selectedLearningCandidate;
  const action = admin.learningGateAction;
  if (!candidate || !action) return;
  setBusy(elements.learningGateForm, true);
  try {
    if (action === "evaluate") {
      const result = await adminMutation(
        `/api/v1/admin/learning/candidates/${candidate.id}/evaluate`,
        { expectedVersion: candidate.version });
      admin.lastLearningEvaluation = result.receipt;
      closeLearningGate();
      await Promise.all([loadLearning(), loadSkills()]);
      const failed = result.receipt.checks.filter(check => !check.passed).length;
      workspaceStatus(elements.learningMessage,
        `${result.candidate.skillId} ${result.candidate.state}: ${result.receipt.checks.length - failed} passed, ${failed} failed. Receipt ${result.receipt.evidence.contentHash.slice(0, 24)}….`,
        failed ? "error" : "ok");
      return;
    }
    const evidenceHash = await learningEvidenceHash(candidate, action);
    const payload = { action, expectedVersion: candidate.version, evidenceHash };
    if (action === "critique") Object.assign(payload, {
      passed: document.querySelector("#learning-gate-passed").checked,
      findingCodes: document.querySelector("#learning-gate-findings").value.split(",")
        .map(value => value.trim()).filter(Boolean),
    });
    if (action === "finish-canary") Object.assign(payload, {
      passed: document.querySelector("#learning-gate-passed").checked,
      baselineMetric: Number(document.querySelector("#learning-gate-baseline").value),
      candidateMetric: Number(document.querySelector("#learning-gate-candidate").value),
    });
    const result = await adminMutation(`/api/v1/admin/learning/candidates/${candidate.id}/transition`, payload);
    closeLearningGate();
    await Promise.all([loadLearning(), loadSkills()]);
    workspaceStatus(elements.learningMessage,
      `${result.skillId} advanced to ${result.state}; ${result.activeAuthority ? "active authority granted" : "no active authority"}.`, "ok");
  } catch (error) {
    workspaceStatus(elements.learningMessage,
      error instanceof Error ? error.message : "The learning gate could not be recorded.", "error");
  } finally {
    setBusy(elements.learningGateForm, false);
  }
}

async function loadLearning(message = "Loading classified learning evidence…") {
  workspaceStatus(elements.learningMessage, message);
  try {
    if (!admin.runs.length) {
      const runs = await adminRead("/api/v1/admin/runs");
      admin.runs = runs.runs;
    }
    populateLearningSources();
    const [payload, candidatePayload] = await Promise.all([
      adminRead("/api/v1/admin/learning/signals"),
      adminRead("/api/v1/admin/learning/candidates"),
    ]);
    admin.learningSignals = payload.signals;
    admin.learningCandidates = candidatePayload.candidates;
    renderLearningSignals(payload.signals);
    renderLearningCandidates(candidatePayload.candidates);
    workspaceStatus(elements.learningMessage,
      `${payload.signals.length} classified ${payload.signals.length === 1 ? "signal" : "signals"} · ` +
      `${candidatePayload.candidates.length} governed ${candidatePayload.candidates.length === 1 ? "candidate" : "candidates"}.`, "ok");
  } catch (error) {
    workspaceStatus(elements.learningMessage, error instanceof Error ? error.message : "Learning evidence could not be loaded.", "error");
  }
}

async function createLearningProposal(event) {
  event.preventDefault();
  const signal = admin.selectedLearningSignal;
  if (!signal) {
    workspaceStatus(elements.learningMessage, "Select a NewSkill signal before creating a proposal.", "error");
    return;
  }
  setBusy(elements.learningProposalForm, true);
  try {
    const permissions = elements.learningProposalPermissions.value.split(",")
      .map(value => value.trim()).filter(Boolean);
    const result = await adminMutation(`/api/v1/admin/learning/signals/${signal.id}/candidates`, {
      skillId: elements.learningProposalSkillId.value.trim(),
      version: elements.learningProposalVersion.value.trim(),
      description: elements.learningProposalDescription.value.trim(),
      requestedPermissions: [...new Set(permissions)].sort(),
      generationGuidance: elements.learningProposalGuidance.value.trim() || null,
      requiredTools: elements.learningProposalHttpApi.checked ? ["tool:http-api.get"] : [],
    });
    admin.lastLearningGeneration = { ...result.generation, candidateId: result.id };
    closeLearningProposal();
    await loadLearning();
    workspaceStatus(elements.learningMessage,
      `${result.skillId} ${result.candidateVersion} was generated by ${result.generation.model} and is inert; next gate: ${result.nextGate}.`, "ok");
  } catch (error) {
    workspaceStatus(elements.learningMessage,
      error instanceof Error ? error.message : "The isolated proposal could not be created.", "error");
  } finally {
    setBusy(elements.learningProposalForm, false);
  }
}

async function captureLearning(event) {
  event.preventDefault();
  setBusy(elements.learningForm, true);
  try {
    const result = await adminMutation("/api/v1/admin/learning/signals", {
      sourceTaskId: elements.learningSourceRun.value,
      kind: elements.learningKind.value,
      summary: elements.learningSummary.value.trim(),
      occurrenceCount: Number(elements.learningOccurrences.value),
    });
    elements.learningSummary.value = "";
    elements.learningOccurrences.value = "1";
    await loadLearning();
    workspaceStatus(elements.learningMessage,
      `${result.action}: ${result.reasonCode}. Evidence stored; authority unchanged.`, "ok");
  } catch (error) {
    workspaceStatus(elements.learningMessage, error instanceof Error ? error.message : "Learning evidence was not accepted.", "error");
  } finally {
    setBusy(elements.learningForm, false);
    populateLearningSources();
  }
}

const viewDetails = {
  overview: ["CONTROL PLANE", "Good evening."],
  setup: ["FIRST RUN", "Configure AgentForge"],
  agents: ["IDENTITIES", "Your local agents"],
  runs: ["ORCHESTRATION", "Durable runs"],
  schedules: ["AUTOMATION", "Durable schedules"],
  context: ["KNOWLEDGE", "Memory and research"],
  skills: ["REGISTRY", "Governed skills"],
  tools: ["CAPABILITIES", "Tools and approvals"],
  learning: ["LEARNING", "Evidence inbox"],
};

async function showCurrentView() {
  const requested = window.location.hash.slice(1);
  const view = Object.hasOwn(viewDetails, requested) ? requested : "overview";
  for (const panel of document.querySelectorAll(".app-view")) panel.hidden = panel.id !== view;
  for (const link of document.querySelectorAll("[data-view]")) {
    const active = link.dataset.view === view;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  [elements.viewKicker.textContent, elements.viewTitle.textContent] = viewDetails[view];
  if (view === "agents") await loadAgents();
  if (view === "runs") await loadRuns();
  if (view === "schedules") await loadSchedules();
  if (view === "context") await loadContext();
  if (view === "skills") await loadSkills();
  if (view === "tools") await loadTools();
  if (view === "learning") await loadLearning();
}

function renderHost(result) {
  if (result.ok) {
    setIndicator(elements.hostIndicator, "ok", "Online");
    elements.hostCopy.textContent = "The local ASP.NET Core host is responding normally.";
  } else {
    setIndicator(elements.hostIndicator, "error", "Unavailable");
    elements.hostCopy.textContent = "The liveness endpoint did not report a healthy host.";
  }
}

function renderReadiness(result) {
  if (result.ok) {
    setIndicator(elements.readinessIndicator, "ok", "Ready");
    elements.readinessCopy.textContent = "Setup validation has passed and the runtime is available.";
  } else if (result.status === 503) {
    setIndicator(elements.readinessIndicator, "warn", "Setup required");
    elements.readinessCopy.textContent = "Runtime work is denied until setup completes.";
  } else {
    setIndicator(elements.readinessIndicator, "error", "Unavailable");
    elements.readinessCopy.textContent = "Readiness evidence could not be loaded.";
  }
}

function renderSandbox(result) {
  const payload = result.payload;
  const available = result.ok && payload.isAvailable === true;
  setIndicator(elements.sandboxIndicator, available ? "ok" : "warn", available ? "Available" : "Limited");
  elements.sandboxKind.textContent = payload.kind ?? "Unknown";
  elements.sandboxCopy.textContent = available
    ? "Restricted execution is available with explicit capability reporting."
    : "Requested isolation will fail closed when unsupported.";
}

function renderFailure() {
  setIndicator(elements.hostIndicator, "error", "Offline");
  setIndicator(elements.readinessIndicator, "error", "Unknown");
  setIndicator(elements.sandboxIndicator, "error", "Unknown");
  elements.hostCopy.textContent = "Could not reach the local control plane.";
  elements.readinessCopy.textContent = "Start the AgentForge host and refresh this page.";
  elements.sandboxCopy.textContent = "Capability evidence is unavailable while the host is offline.";
  elements.updated.textContent = "Connection failed";
}

async function refreshStatus() {
  elements.refresh.classList.add("loading");
  elements.refresh.disabled = true;
  try {
    const [installation, live, ready, sandbox] = await Promise.all([
      readJson("/api/v1/setup/status"),
      readJson("/health/live"),
      readJson("/health/ready"),
      readJson("/api/v1/sandbox/capabilities"),
    ]);
    renderInstallation(installation);
    renderHost(live);
    renderReadiness(ready);
    renderSandbox(sandbox);
    elements.updated.textContent = `Updated ${new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(new Date())}`;
  } catch {
    renderFailure();
  } finally {
    elements.refresh.classList.remove("loading");
    elements.refresh.disabled = false;
  }
}

function setupStatus(message, state = "") {
  elements.setupMessage.textContent = message;
  elements.setupMessage.className = `setup-message ${state}`;
}

async function mutation(path, payload, contentType = "application/json") {
  const response = await fetch(path, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Content-Type": contentType,
      "Idempotency-Key": crypto.randomUUID(),
      "X-CSRF-Token": setup.csrfToken,
      Accept: "application/json",
    },
    body: contentType.startsWith("application/json") ? JSON.stringify(payload) : payload,
  });
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail ?? body.title ?? "Setup request failed");
  return body;
}

function setBusy(form, busy) {
  for (const control of form.querySelectorAll("button, input, select, textarea")) {
    if (busy) {
      control.dataset.busyWasDisabled = control.disabled ? "true" : "false";
      control.disabled = true;
    } else {
      control.disabled = control.dataset.policyDisabled === "true" ||
        control.dataset.busyWasDisabled === "true";
      delete control.dataset.busyWasDisabled;
    }
  }
  form.setAttribute("aria-busy", busy ? "true" : "false");
}

function showStage(stage) {
  setup.currentStage = Math.min(5, Math.max(1, stage));
  for (const item of elements.progress.querySelectorAll("li")) {
    const itemStep = Number(item.dataset.step);
    item.classList.toggle("active", itemStep === setup.currentStage);
    item.classList.toggle("done", itemStep < setup.currentStage);
    if (itemStep === setup.currentStage) item.setAttribute("aria-current", "step");
    else item.removeAttribute("aria-current");
  }
  for (const panel of document.querySelectorAll(".setup-stage")) {
    panel.hidden = Number(panel.dataset.stage) !== setup.currentStage;
  }
  elements.complete.hidden = true;
  if (setup.currentStage === 3) renderVerify();
  if (setup.currentStage === 5) renderReview();
}

function showCompletedInstallation() {
  for (const panel of document.querySelectorAll(".setup-stage")) panel.hidden = true;
  elements.complete.hidden = false;
  elements.progress.hidden = true;
  setupStatus("AgentForge is already configured and Ready.", "ok");
}

function providerPayload() {
  return {
    name: document.querySelector("#provider-name").value.trim(),
    providerType: document.querySelector("#provider-type").value,
    endpoint: document.querySelector("#provider-endpoint").value.trim(),
  };
}

function populateModels(models, selectedModel = null) {
  setup.models = models;
  elements.model.replaceChildren();
  for (const model of models) {
    const option = document.createElement("option");
    option.value = model.id;
    option.textContent = model.id;
    if (model.id === selectedModel) option.selected = true;
    elements.model.append(option);
  }
  elements.modelSummary.textContent = `${models.length} compatible ${models.length === 1 ? "model" : "models"} reported by ${setup.provider.endpoint}.`;
  renderModelDetails();
}

function renderModelDetails() {
  const selected = setup.models.find(model => model.id === elements.model.value);
  if (!selected) {
    elements.modelDetails.textContent = "Select a model to continue.";
    return;
  }
  const details = [];
  if (selected.ownedBy) details.push(`Owner: ${selected.ownedBy}`);
  if (selected.maximumContextTokens) details.push(`Context: ${selected.maximumContextTokens.toLocaleString()} tokens`);
  elements.modelDetails.textContent = details.length ? details.join(" · ") : "The endpoint reported this model as available.";
}

function renderVerify() {
  elements.verifySummary.replaceChildren();
  const title = document.createElement("strong");
  title.textContent = setup.provider.model || "No model selected";
  const endpoint = document.createElement("span");
  endpoint.textContent = setup.provider.endpoint;
  elements.verifySummary.append(title, endpoint);
}

function addReviewItem(label, value) {
  const item = document.createElement("div");
  const key = document.createElement("span");
  const content = document.createElement("strong");
  key.textContent = label;
  content.textContent = value;
  item.append(key, content);
  elements.reviewSummary.append(item);
}

function renderReview() {
  elements.reviewSummary.replaceChildren();
  addReviewItem("Connection", setup.provider.name);
  addReviewItem("Endpoint", setup.provider.endpoint);
  addReviewItem("Model", setup.provider.model || "Not selected");
  addReviewItem("Agent", setup.agent.name);
  addReviewItem("Time zone", setup.agent.timeZone);
  addReviewItem("Authentication", elements.credential.value ? "API key provided" : "No API key");
}

function applySession(session) {
  setup.csrfToken = session.csrfToken;
  setup.begun = session.begun === true;
  setup.providerConfigured = session.providerConfigured === true;
  setup.modelTested = session.modelTested === true;
  setup.agentCreated = session.agentCreated === true;
  if (session.provider) {
    setup.provider = { ...setup.provider, ...session.provider };
    document.querySelector("#provider-name").value = setup.provider.name;
    document.querySelector("#provider-type").value = setup.provider.providerType;
    document.querySelector("#provider-endpoint").value = setup.provider.endpoint;
  }
  if (Array.isArray(session.models) && session.models.length) {
    populateModels(session.models.map(id => ({ id })), setup.provider.model);
  }
  showStage(session.currentStep || 1);
}

async function startSetup() {
  try {
    let result = await readJson("/api/v1/setup/web/session");
    if (!result.ok) {
      result = await readJson("/api/v1/setup/web/session", {
        method: "POST",
        headers: { "Content-Type": "application/json", Origin: window.location.origin },
        body: "{}",
      });
    }
    if (!result.ok) {
      if (result.status === 409 && result.payload.title === "Setup complete") {
        showCompletedInstallation();
        return;
      }
      throw new Error(result.payload.detail ?? "Protected setup could not be started.");
    }
    applySession(result.payload);
    setupStatus(result.payload.resumed ? "Protected setup resumed." : "Protected local setup is ready.", "ok");
  } catch (error) {
    setupStatus(error instanceof Error ? error.message : "Protected setup could not be started.", "error");
  }
}

async function discoverModels(event) {
  event.preventDefault();
  setBusy(elements.providerForm, true);
  try {
    const provider = providerPayload();
    setupStatus("Connecting to the endpoint and reading its model catalog…");
    await mutation("/api/v1/setup/web/provider/discover", provider);
    const result = await mutation("/api/v1/setup/web/provider/models", elements.credential.value, "text/plain;charset=UTF-8");
    setup.provider = { ...provider, model: null };
    sessionStorage.setItem("agentforge.setup.endpoint", provider.endpoint);
    populateModels(result.models);
    setupStatus(`Connected. Found ${result.models.length} models.`, "ok");
    showStage(2);
  } catch (error) {
    elements.manualModelPanel.hidden = false;
    setupStatus(error instanceof Error ? error.message : "The endpoint could not be reached.", "error");
  } finally {
    setBusy(elements.providerForm, false);
  }
}

async function useManualModel() {
  const model = elements.manualModel.value.trim();
  if (!model) {
    setupStatus("Enter the exact model ID exposed by your server.", "error");
    elements.manualModel.focus();
    return;
  }
  setBusy(elements.providerForm, true);
  try {
    const provider = providerPayload();
    setupStatus("Preparing the endpoint for an exact model ID…");
    await mutation("/api/v1/setup/web/provider/discover", provider);
    await mutation("/api/v1/setup/web/provider/model/manual", { ...provider, model });
    setup.provider = { ...provider, model: null };
    sessionStorage.setItem("agentforge.setup.endpoint", provider.endpoint);
    populateModels([{ id: model, ownedBy: "Manual entry" }], model);
    setupStatus("Manual model ID accepted. Select it, then run the required connection test.", "ok");
    showStage(2);
  } catch (error) {
    setupStatus(error instanceof Error ? error.message : "The model ID could not be prepared.", "error");
  } finally {
    setBusy(elements.providerForm, false);
  }
}

async function selectModel(event) {
  event.preventDefault();
  setBusy(elements.modelForm, true);
  try {
    setup.provider.model = elements.model.value;
    await mutation("/api/v1/setup/web/provider/select", setup.provider);
    setup.modelTested = false;
    setupStatus(`${setup.provider.model} selected. Verify it before continuing.`, "ok");
    showStage(3);
  } catch (error) {
    setupStatus(error instanceof Error ? error.message : "The model could not be selected.", "error");
  } finally {
    setBusy(elements.modelForm, false);
  }
}

async function verifyModel(event) {
  event.preventDefault();
  setBusy(elements.verifyForm, true);
  try {
    setupStatus(`Sending a bounded test to ${setup.provider.model}…`);
    const result = await mutation("/api/v1/setup/web/provider/test", elements.credential.value, "text/plain;charset=UTF-8");
    setup.modelTested = true;
    setupStatus(`Connection verified in ${result.durationMilliseconds} ms.`, "ok");
    showStage(4);
  } catch (error) {
    setupStatus(error instanceof Error ? error.message : "The model test failed.", "error");
  } finally {
    setBusy(elements.verifyForm, false);
  }
}

function prepareReview(event) {
  event.preventDefault();
  setup.agent = {
    name: document.querySelector("#agent-name").value.trim(),
    timeZone: document.querySelector("#agent-timezone").value.trim(),
  };
  setupStatus("Review the connection and conservative policy before saving.");
  showStage(5);
}

function agentPayload() {
  return {
    name: setup.agent.name,
    expertise: "local agent harness",
    mission: "operate within explicit AgentForge policy",
    preferredLanguage: "en",
    timeZone: setup.agent.timeZone,
    responseStyle: "concise and evidence-backed",
    defaultWorkspace: null,
  };
}

async function completeSetup(event) {
  event.preventDefault();
  setBusy(elements.reviewForm, true);
  try {
    if (!setup.begun) {
      setupStatus("Initializing the durable installation…");
      await mutation("/api/v1/setup/web/begin", {});
      setup.begun = true;
    }
    if (!setup.providerConfigured) {
      setupStatus("Saving the verified provider profile…");
      await mutation("/api/v1/setup/web/provider/credential", elements.credential.value, "text/plain;charset=UTF-8");
      setup.providerConfigured = true;
    }
    if (!setup.agentCreated) {
      setupStatus("Previewing effective policy and capabilities…");
      const agent = agentPayload();
      await mutation("/api/v1/setup/web/agent/preview", agent);
      await mutation("/api/v1/setup/web/agent", agent);
      setup.agentCreated = true;
    }
    setupStatus("Running minimum-viability checks…");
    await mutation("/api/v1/setup/web/complete", {});
    setup.csrfToken = null;
    elements.credential.value = "";
    for (const panel of document.querySelectorAll(".setup-stage")) panel.hidden = true;
    elements.complete.hidden = false;
    elements.progress.hidden = true;
    setupStatus("Setup complete. AgentForge is Ready.", "ok");
    await refreshStatus();
  } catch (error) {
    setupStatus(error instanceof Error ? error.message : "Setup failed safely.", "error");
  } finally {
    setBusy(elements.reviewForm, false);
  }
}

elements.refresh.addEventListener("click", refreshStatus);
elements.providerForm.addEventListener("submit", discoverModels);
document.querySelector("#show-manual-model").addEventListener("click", () => {
  elements.manualModelPanel.hidden = !elements.manualModelPanel.hidden;
  if (!elements.manualModelPanel.hidden) elements.manualModel.focus();
});
document.querySelector("#use-manual-model").addEventListener("click", useManualModel);
elements.modelForm.addEventListener("submit", selectModel);
elements.verifyForm.addEventListener("submit", verifyModel);
elements.agentForm.addEventListener("submit", prepareReview);
elements.reviewForm.addEventListener("submit", completeSetup);
elements.newProvider.addEventListener("click", openProviderCreate);
elements.newAgent.addEventListener("click", openAgentCreate);
elements.providerCreateClose.addEventListener("click", closeProviderCreate);
elements.providerCreateForm.addEventListener("submit", previewProviderCreate);
elements.providerCreateDiscard.addEventListener("click", discardProviderCreatePreview);
elements.providerCreateApply.addEventListener("click", applyProviderCreate);
elements.agentModelForm.addEventListener("submit", previewAgentModel);
elements.agentProfileForm.addEventListener("submit", previewAgentProfile);
elements.agentDiscoverModels.addEventListener("click", discoverAgentModels);
elements.agentEditorClose.addEventListener("click", closeAgentEditor);
elements.agentEditCancel.addEventListener("click", discardAgentEditPreview);
elements.agentEditApply.addEventListener("click", applyAgentEditPreview);
elements.runForm.addEventListener("submit", createRun);
elements.scheduleForm.addEventListener("submit", previewSchedule);
elements.scheduleReview.addEventListener("submit", applySchedule);
elements.scheduleReviewClose.addEventListener("click", discardSchedulePreview);
elements.memorySearchForm.addEventListener("submit", searchMemory);
elements.memoryCreateForm.addEventListener("submit", createMemory);
elements.researchForm.addEventListener("submit", previewResearch);
elements.researchReview.addEventListener("submit", applyResearch);
elements.researchReviewClose.addEventListener("click", discardResearchPreview);
elements.braveConfigForm.addEventListener("submit", previewBraveConfiguration);
elements.braveConfigReview.addEventListener("submit", applyBraveConfiguration);
elements.braveConfigDiscard.addEventListener("click", discardBraveConfigurationPreview);
elements.runContinueForm.addEventListener("submit", continueRunConversation);
elements.resumeRunTurn.addEventListener("click", resumeRunConversation);
elements.closeRunDetails.addEventListener("click", () => {
  admin.selectedRunDetails = null;
  elements.runDetails.hidden = true;
});
elements.learningForm.addEventListener("submit", captureLearning);
elements.learningProposalForm.addEventListener("submit", createLearningProposal);
elements.learningProposalClose.addEventListener("click", closeLearningProposal);
elements.learningGateForm.addEventListener("submit", transitionLearningCandidate);
elements.learningGateClose.addEventListener("click", closeLearningGate);
elements.skillGateForm.addEventListener("submit", transitionSkillProposal);
elements.skillGateClose.addEventListener("click", closeSkillGate);
elements.skillGrantForm.addEventListener("submit", applySkillGrant);
elements.skillGrantClose.addEventListener("click", closeSkillGrant);
elements.installSeedSkill.addEventListener("click", installSeedSkill);
elements.httpApiConfigForm.addEventListener("submit", previewHttpApiConfiguration);
elements.httpApiConfigReview.addEventListener("submit", applyHttpApiConfiguration);
elements.httpApiConfigDiscard.addEventListener("click", discardHttpApiConfigurationPreview);
elements.toolGrantForm.addEventListener("submit", applyToolGrant);
elements.toolGrantClose.addEventListener("click", closeToolGrant);
elements.toolInvocationForm.addEventListener("submit", previewToolInvocation);
elements.toolApprovalForm.addEventListener("submit", applyToolInvocation);
elements.toolApprovalClose.addEventListener("click", closeToolApproval);
elements.toolAgent.addEventListener("change", populateToolOptions);
elements.toolSelector.addEventListener("change", renderToolParameters);
elements.toolWorkspace.addEventListener("change", renderToolParameters);
elements.model.addEventListener("change", renderModelDetails);
elements.runAgent.addEventListener("change", () => {
  const selected = elements.runResearchReceipt.selectedOptions[0];
  if (selected?.dataset.agentId && selected.dataset.agentId !== elements.runAgent.value) {
    elements.runResearchReceipt.value = "";
  }
  loadRunOptions();
});
elements.memoryAgent.addEventListener("change", () => {
  admin.memories = [];
  renderMemories();
  const agent = selectedContextAgent(elements.memoryAgent);
  if (agent) {
    elements.memoryRetention.max = Math.max(1, agent.retentionDays);
    elements.memoryRetention.value = Math.max(1, agent.retentionDays);
  }
});
elements.agentEditMemoryScope.addEventListener("change", () => {
  if (elements.agentEditMemoryScope.value === "Task") elements.agentEditRetention.value = 0;
});
elements.agentEditLearning.addEventListener("change", () => {
  const scopes = { Off: "None", Observe: "None", Propose: "ProposalWorkspaceOnly", ScopedAuto: "ApprovedSkillClasses" };
  elements.agentEditMutableScope.value = scopes[elements.agentEditLearning.value] || "None";
});
elements.agentEditToolGrants.addEventListener("input", () => {
  if (readGrantList(elements.agentEditToolGrants).length === 0) elements.agentEditMaxTools.value = 0;
  else if (Number(elements.agentEditMaxTools.value) === 0) elements.agentEditMaxTools.value = 10;
  syncFriendlyCapabilitiesFromPolicy();
});
elements.agentEditSearch.addEventListener("change", () =>
  updateFriendlyCapability("tool:search.web", elements.agentEditSearch.checked));
elements.agentEditHttpApi.addEventListener("change", () =>
  updateFriendlyCapability("tool:http-api.read", elements.agentEditHttpApi.checked));
elements.agentEditWorkspaceRead.addEventListener("change", () =>
  updateFriendlyCapability("tool:workspace.read", elements.agentEditWorkspaceRead.checked));
elements.runSearchApprove.addEventListener("click", () => previewRunSearch("grant"));
elements.runSearchDeny.addEventListener("click", () => previewRunSearch("deny"));
elements.runSearchApply.addEventListener("click", applyRunSearch);
elements.agentEditContextOverride.addEventListener("input", refreshEffectiveContext);
elements.agentEditMaxInput.addEventListener("input", refreshEffectiveContext);
elements.agentEditMaxOutput.addEventListener("input", refreshEffectiveContext);
elements.agentEditModel.addEventListener("change", () => {
  const selected = elements.agentEditModel.selectedOptions[0];
  setDiscoveredContext(selected?.dataset.maximumContextTokens, selected?.value);
});
elements.agentEditProvider.addEventListener("change", () => {
  if (admin.agentEditMode === "create" ||
      elements.agentEditProvider.value !== admin.agentEdit?.provider?.id) {
    setDiscoveredContext(null, null);
  }
});
elements.runDepth.addEventListener("change", () => {
  const preset = Number(elements.runDepth.selectedOptions[0]?.dataset.tokens);
  if (Number.isInteger(preset) && preset > 0) elements.runTokenLimit.value = preset;
});
elements.runFollowUpDepth.addEventListener("change", () => {
  const presets = { concise: 512, balanced: 2048, detailed: 8192, extended: 16384, maximum: 262144 };
  const maximum = Number(elements.runFollowUpTokens.max) || 262144;
  elements.runFollowUpTokens.value = Math.min(maximum, presets[elements.runFollowUpDepth.value] || 2048);
});
elements.runSearch.addEventListener("input", () => {
  admin.runPage = 1;
  renderRunHistory();
});
elements.runStateFilter.addEventListener("change", () => {
  admin.runPage = 1;
  renderRunHistory();
});
elements.runPageSize.addEventListener("change", () => {
  admin.runPage = 1;
  renderRunHistory();
});
elements.runPagePrevious.addEventListener("click", () => {
  admin.runPage -= 1;
  renderRunHistory();
});
elements.runPageNext.addEventListener("click", () => {
  admin.runPage += 1;
  renderRunHistory();
});
for (const button of document.querySelectorAll("[data-back]")) {
  button.addEventListener("click", () => {
    const stage = Number(button.dataset.back);
    showStage(stage);
    const messages = {
      1: "Update the endpoint and discover its current model catalog.",
      2: "Choose one of the models reported by this endpoint.",
      3: "Verify the selected model before saving anything.",
      4: "Name the agent that will own this model policy.",
    };
    setupStatus(messages[stage] || "Continue setup.");
  });
}
for (const button of document.querySelectorAll("[data-reload]")) {
  button.addEventListener("click", () => {
    if (button.dataset.reload === "agents") loadAgents("Refreshing agent identities…");
    if (button.dataset.reload === "runs") loadRuns("Refreshing durable snapshots…");
    if (button.dataset.reload === "schedules") loadSchedules("Refreshing durable schedules…");
    if (button.dataset.reload === "context") loadContext("Refreshing memory and research capabilities…");
    if (button.dataset.reload === "skills") loadSkills("Refreshing the skill registry…");
    if (button.dataset.reload === "tools") loadTools("Refreshing the authoritative tool catalog…");
    if (button.dataset.reload === "learning") loadLearning("Refreshing classified evidence…");
  });
}
window.addEventListener("hashchange", showCurrentView);

document.querySelector("#provider-endpoint").value = setup.provider.endpoint;
document.querySelector("#agent-timezone").value = setup.agent.timeZone;
elements.scheduleOneShot.value = new Date(Date.now() + 60 * 60 * 1000).toISOString().slice(0, 16);
refreshStatus();
startSetup();
showCurrentView();
