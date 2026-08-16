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
  agentEditWorkspace: document.querySelector("#agent-edit-workspace"),
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
  runInstructions: document.querySelector("#run-instructions"),
  runSystemPreview: document.querySelector("#run-system-preview"),
  runSkills: document.querySelector("#run-skills"),
  runModelSummary: document.querySelector("#run-model-summary"),
  runRestrictions: document.querySelector("#run-restrictions"),
  runOutput: document.querySelector("#run-output"),
  runOutputState: document.querySelector("#run-output-state"),
  runOutputText: document.querySelector("#run-output-text"),
  runOutputMeta: document.querySelector("#run-output-meta"),
  cancelInteraction: document.querySelector("#cancel-interaction"),
  runHistoryCount: document.querySelector("#run-history-count"),
  runSearch: document.querySelector("#run-search"),
  runStateFilter: document.querySelector("#run-state-filter"),
  runPageSize: document.querySelector("#run-page-size"),
  runPagePrevious: document.querySelector("#run-page-previous"),
  runPageNext: document.querySelector("#run-page-next"),
  runPageSummary: document.querySelector("#run-page-summary"),
  skillsMessage: document.querySelector("#skills-message"),
  skillList: document.querySelector("#skill-list"),
  installSeedSkill: document.querySelector("#install-seed-skill"),
  learningForm: document.querySelector("#learning-form"),
  learningSourceRun: document.querySelector("#learning-source-run"),
  learningKind: document.querySelector("#learning-kind"),
  learningOccurrences: document.querySelector("#learning-occurrences"),
  learningSummary: document.querySelector("#learning-summary"),
  captureLearning: document.querySelector("#capture-learning"),
  learningMessage: document.querySelector("#learning-message"),
  learningList: document.querySelector("#learning-list"),
  accessModeTitle: document.querySelector("#access-mode-title"),
  accessModeDetail: document.querySelector("#access-mode-detail"),
};

const admin = {
  csrfToken: null,
  installationId: null,
  actorId: null,
  remoteAccessCode: "",
  agents: [],
  agentEdit: null,
  agentEditPreview: null,
  runs: [],
  runOptions: null,
  runPage: 1,
  runPageSize: 8,
  pendingLearningTaskId: null,
  activeTaskId: null,
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
    const selectedAgentId = elements.runAgent.value;
    const payload = await adminRead("/api/v1/admin/agents");
    admin.agents = payload.agents;
    elements.agentList.replaceChildren();
    elements.runAgent.replaceChildren();
    for (const agent of payload.agents) {
      const option = document.createElement("option");
      option.value = agent.id;
      option.textContent = agent.name;
      elements.runAgent.append(option);

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
    if (payload.agents.some(agent => agent.id === selectedAgentId)) {
      elements.runAgent.value = selectedAgentId;
    }
    if (!payload.agents.length) elements.agentList.append(makeElement("div", "empty-state", "No agents are configured."));
    workspaceStatus(elements.agentsMessage, `${payload.agents.length} ${payload.agents.length === 1 ? "agent" : "agents"} loaded from durable state.`, "ok");
    return payload.agents;
  } catch (error) {
    workspaceStatus(elements.agentsMessage, error instanceof Error ? error.message : "Agents could not be loaded.", "error");
    return [];
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
    option.textContent = model.ownedBy ? `${model.id} · ${model.ownedBy}` : model.id;
    elements.agentEditModel.append(option);
  }
  elements.agentEditModel.value = selected;
}

function discardAgentEditPreview() {
  admin.agentEditPreview = null;
  elements.agentEditReview.hidden = true;
  elements.agentEditChanges.replaceChildren();
  elements.agentEditPreviewHash.textContent = "";
}

function closeAgentEditor() {
  admin.agentEdit = null;
  discardAgentEditPreview();
  elements.agentEditor.hidden = true;
}

async function openAgentEditor(agentId) {
  elements.agentEditor.hidden = false;
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
    setAgentEditorStatus("Edit identity fields or discover the endpoint's current model catalog.", "ok");
    elements.agentEditor.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "The agent editor could not be opened.", "error");
  }
}

async function discoverAgentModels() {
  if (!admin.agentEdit) return;
  setBusy(elements.agentModelForm, true);
  setAgentEditorStatus("Discovering models from the pinned endpoint…");
  try {
    const payload = await adminMutation(
      `/api/v1/admin/agents/${admin.agentEdit.agent.id}/models/discover`, {});
    populateAgentModelOptions(payload.models, admin.agentEdit.provider.model);
    setAgentEditorStatus(`${payload.models.length} model ${payload.models.length === 1 ? "identifier" : "identifiers"} discovered safely.`, "ok");
  } catch (error) {
    setAgentEditorStatus(error instanceof Error ? error.message : "Model discovery failed.", "error");
  } finally {
    setBusy(elements.agentModelForm, false);
  }
}

function renderAgentEditPreview(kind, payload) {
  admin.agentEditPreview = { kind, hash: payload.previewHash };
  elements.agentEditReviewTitle.textContent = kind === "model" ? "Review model update" : "Review profile update";
  elements.agentEditReviewWarning.textContent = payload.warning ||
    "Only the displayed identity fields will change; effective authority is preserved.";
  elements.agentEditChanges.replaceChildren();
  for (const change of payload.changes) {
    const item = document.createElement("div");
    item.append(
      makeElement("span", "", change.path),
      makeElement("strong", "", `${change.before ?? "Not set"} → ${change.after ?? "Not set"}`));
    elements.agentEditChanges.append(item);
  }
  if (!payload.changes.length) {
    elements.agentEditChanges.append(makeElement("div", "", "No effective changes."));
  }
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
  setAgentEditorStatus("Validating the identity change and preserving effective authority…");
  try {
    const payload = await adminMutation(
      `/api/v1/admin/agents/${admin.agentEdit.agent.id}/profile/preview`, {
        expectedInstallationVersion: admin.agentEdit.installationVersion,
        expectedAgentVersion: admin.agentEdit.agent.version,
        name: elements.agentEditName.value.trim(),
        expertise: elements.agentEditExpertise.value.trim() || null,
        mission: elements.agentEditMission.value.trim() || null,
        preferredLanguage: elements.agentEditLanguage.value.trim(),
        timeZone: elements.agentEditTimezone.value.trim(),
        responseStyle: elements.agentEditStyle.value.trim(),
        defaultWorkspace: elements.agentEditWorkspace.value.trim() || null,
      });
    renderAgentEditPreview("profile", payload);
    setAgentEditorStatus("The exact profile diff is ready for review.", "ok");
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
  const agentId = admin.agentEdit.agent.id;
  setAgentEditorStatus("Revalidating and atomically applying the approved preview…");
  try {
    await adminMutation(
      `/api/v1/admin/agents/${agentId}/${kind === "model" ? "model" : "profile"}/apply`,
      { previewHash: hash });
    discardAgentEditPreview();
    await loadAgents("Refreshing the updated durable agent…");
    await openAgentEditor(agentId);
    await loadRunOptions();
    setAgentEditorStatus(`${kind === "model" ? "Model" : "Profile"} update committed and audited.`, "ok");
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
    option.textContent = `${depth.label} · up to ${depth.maximumOutputTokens.toLocaleString()} tokens`;
    elements.runDepth.append(option);
  }
  elements.runDepth.value = payload.responseDepths.some(item => item.id === selectedDepth)
    ? selectedDepth
    : "balanced";

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
    addMeta(meta, "Updated", formatRunTime(run.updatedAt));
    addMeta(meta, "Snapshot", `v${run.version} · ${run.snapshotHash.slice(0, 18)}…`);
    content.append(title, meta);
    card.append(content);
    const actions = makeElement("div", "resource-actions");
    if (!terminalStates.includes(run.state)) {
      const cancel = makeElement("button", "secondary-action", "Cancel run");
      cancel.type = "button";
      cancel.addEventListener("click", async () => {
        cancel.disabled = true;
        try {
          await adminMutation(`/api/v1/admin/runs/${run.taskId}/cancel`);
          await loadRuns("Run canceled. Refreshing snapshots…", false);
        } catch (error) {
          workspaceStatus(elements.runsMessage, error instanceof Error ? error.message : "Run cancellation failed.", "error");
          cancel.disabled = false;
        }
      });
      actions.append(cancel);
    } else {
      const learn = makeElement("button", "secondary-action", "Capture learning");
      learn.type = "button";
      learn.addEventListener("click", async () => {
        admin.pendingLearningTaskId = run.taskId;
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
  elements.cancelInteraction.hidden = true;
  elements.cancelInteraction.disabled = false;
  admin.activeTaskId = null;
  try {
    const prompt = document.querySelector("#run-prompt").value.trim();
    const skillIds = [...elements.runSkills.querySelectorAll("input[type='checkbox']:checked")]
      .map(input => input.value);
    workspaceStatus(elements.runsMessage, "Invoking the pinned local model with tools and fallback disabled…");
    let providerLabel = "Pinned local model";
    let usageLabel = "Token usage pending";
    let terminalMessage = "Local model stream ended.";
    let terminalEvent = null;
    await adminStreamMutation(
      `/api/v1/admin/agents/${elements.runAgent.value}/test-chat-stream`,
      {
        prompt,
        name: elements.runName.value.trim() || null,
        runInstructions: elements.runInstructions.value.trim() || null,
        responseDepth: elements.runDepth.value,
        skillIds,
      },
      async (eventName, payload) => {
        if (eventName === "run-started") {
          admin.activeTaskId = payload.taskId;
          providerLabel = `${payload.provider.name} · ${payload.provider.model}`;
          elements.runOutputState.textContent = "Running";
          const skillLabel = payload.configuration.skillIds.length
            ? ` · ${payload.configuration.skillIds.length} skill ${payload.configuration.skillIds.length === 1 ? "snapshot" : "snapshots"}`
            : "";
          elements.runOutputMeta.textContent = `${providerLabel} · ${payload.configuration.responseDepth} · up to ${payload.configuration.maximumOutputTokens.toLocaleString()} tokens${skillLabel}`;
          elements.cancelInteraction.hidden = false;
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
        } else if (eventName === "canceled") {
          terminalEvent = eventName;
          terminalMessage = "Interaction canceled. Durable cancellation receipt loaded below.";
          elements.runOutputState.textContent = "Canceled";
          elements.runOutputState.className = "state-chip canceled";
          elements.runOutputMeta.textContent = `${providerLabel} · canceled by operator`;
          elements.cancelInteraction.hidden = true;
        } else if (eventName === "failed") {
          terminalEvent = eventName;
          terminalMessage = payload.message || "The model interaction failed.";
          elements.runOutputState.textContent = "Failed";
          elements.runOutputState.className = "state-chip failed";
          elements.runOutputMeta.textContent = `${providerLabel} · ${payload.code}`;
          elements.cancelInteraction.hidden = true;
        }
      });
    if (!terminalEvent) throw new Error("The model stream closed without a durable terminal receipt.");
    await loadRuns(terminalMessage);
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

async function loadSkills(message = "Loading immutable skill packages…") {
  workspaceStatus(elements.skillsMessage, message);
  try {
    const payload = await adminRead("/api/v1/admin/skills");
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
      elements.skillList.append(card);
    }
    if (!payload.skills.length) elements.skillList.append(makeElement("div", "empty-state", "No skills installed. The bundled starter can be validated and installed above."));
    elements.installSeedSkill.disabled = payload.seedAvailable !== true || payload.skills.some(skill => skill.id === "skill:csharp.review");
    elements.installSeedSkill.textContent = payload.skills.some(skill => skill.id === "skill:csharp.review") ? "Starter installed" : "Install starter skill";
    workspaceStatus(elements.skillsMessage, `${payload.skills.length} registered skill ${payload.skills.length === 1 ? "version" : "versions"} loaded.`, "ok");
  } catch (error) {
    workspaceStatus(elements.skillsMessage, error instanceof Error ? error.message : "Skills could not be loaded.", "error");
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

function populateLearningSources() {
  const selected = admin.pendingLearningTaskId || elements.learningSourceRun.value;
  const terminalStates = ["Completed", "Failed", "Canceled", "DeadLettered"];
  elements.learningSourceRun.replaceChildren();
  for (const run of admin.runs.filter(item => terminalStates.includes(item.state))) {
    const option = document.createElement("option");
    option.value = run.taskId;
    option.textContent = `${run.name} · ${run.state} · ${formatRunTime(run.updatedAt)}`;
    elements.learningSourceRun.append(option);
  }
  if (admin.runs.some(run => run.taskId === selected && terminalStates.includes(run.state))) {
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
    elements.learningList.append(card);
  }
  if (!signals.length) {
    elements.learningList.append(makeElement("div", "empty-state", "No learning evidence captured. Use a terminal run receipt to start the governed intake path."));
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
    const payload = await adminRead("/api/v1/admin/learning/signals");
    renderLearningSignals(payload.signals);
    workspaceStatus(elements.learningMessage,
      `${payload.signals.length} classified learning ${payload.signals.length === 1 ? "signal" : "signals"} loaded.`, "ok");
  } catch (error) {
    workspaceStatus(elements.learningMessage, error instanceof Error ? error.message : "Learning evidence could not be loaded.", "error");
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
  skills: ["REGISTRY", "Governed skills"],
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
  if (view === "skills") await loadSkills();
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
elements.agentModelForm.addEventListener("submit", previewAgentModel);
elements.agentProfileForm.addEventListener("submit", previewAgentProfile);
elements.agentDiscoverModels.addEventListener("click", discoverAgentModels);
elements.agentEditorClose.addEventListener("click", closeAgentEditor);
elements.agentEditCancel.addEventListener("click", discardAgentEditPreview);
elements.agentEditApply.addEventListener("click", applyAgentEditPreview);
elements.runForm.addEventListener("submit", createRun);
elements.learningForm.addEventListener("submit", captureLearning);
elements.installSeedSkill.addEventListener("click", installSeedSkill);
elements.model.addEventListener("change", renderModelDetails);
elements.runAgent.addEventListener("change", loadRunOptions);
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
    if (button.dataset.reload === "skills") loadSkills("Refreshing the skill registry…");
    if (button.dataset.reload === "learning") loadLearning("Refreshing classified evidence…");
  });
}
window.addEventListener("hashchange", showCurrentView);

document.querySelector("#provider-endpoint").value = setup.provider.endpoint;
document.querySelector("#agent-timezone").value = setup.agent.timeZone;
refreshStatus();
startSetup();
showCurrentView();
