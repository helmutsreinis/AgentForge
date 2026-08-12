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
};

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
  elements.stateLabel.textContent = ready ? "Installation ready" : `${state} · Setup required`;
  elements.statePill.classList.toggle("ready", ready);
  elements.stateDescription.textContent = ready
    ? "The durable installation is ready. Runtime operations remain behind authentication and policy."
    : "Connect a provider, choose a model, and create your first local agent to unlock authenticated runtime operations.";
  elements.runtimeMode.textContent = ready ? "Authenticated" : "Setup only";
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
  for (const control of form.querySelectorAll("button, input, select")) control.disabled = busy;
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
elements.model.addEventListener("change", renderModelDetails);
document.querySelector("#refresh-after-setup").addEventListener("click", () => document.querySelector("#overview").scrollIntoView());
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

document.querySelector("#provider-endpoint").value = setup.provider.endpoint;
document.querySelector("#agent-timezone").value = setup.agent.timeZone;
refreshStatus();
startSetup();
