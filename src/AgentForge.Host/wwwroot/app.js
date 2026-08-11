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
  setupForm: document.querySelector("#setup-form"),
  setupMessage: document.querySelector("#setup-message"),
  setupNonce: document.querySelector("#setup-nonce"),
  setupSubmit: document.querySelector("#setup-submit"),
};

let csrfToken = null;

async function readJson(path) {
  const response = await fetch(path, {
    method: "GET",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
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
    ? "The durable installation is ready. Runtime operations still remain behind authentication and policy."
    : "Finish the trusted CLI setup journey to configure a provider, create a named agent, and unlock authenticated runtime operations.";
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
      "X-CSRF-Token": csrfToken,
      Accept: "application/json",
    },
    body: contentType.startsWith("application/json") ? JSON.stringify(payload) : payload,
  });
  const body = await response.json();
  if (!response.ok) {
    throw new Error(body.detail ?? body.title ?? "Setup request failed");
  }
  return body;
}

async function loadSetupNonce() {
  try {
    const result = await readJson("/api/v1/setup/web/nonce");
    if (result.ok) {
      elements.setupNonce.value = result.payload.nonce;
      setupStatus("One-time nonce loaded. Review the safe local defaults, then continue.");
    } else if (result.status === 409) {
      setupStatus("The one-time nonce was already consumed. Restart the host if no setup session is active.", "error");
    } else {
      setupStatus("The setup nonce is unavailable.", "error");
    }
  } catch {
    setupStatus("Could not load the local setup nonce.", "error");
  }
}

async function runSetup(event) {
  event.preventDefault();
  elements.setupSubmit.disabled = true;
  const credentialInput = document.querySelector("#provider-credential");
  try {
    setupStatus("Creating protected setup session…");
    const sessionResponse = await fetch("/api/v1/setup/web/session", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ nonce: elements.setupNonce.value }),
    });
    const session = await sessionResponse.json();
    if (!sessionResponse.ok) throw new Error(session.detail ?? "Session creation failed");
    csrfToken = session.csrfToken;

    setupStatus("Initializing durable installation…");
    await mutation("/api/v1/setup/web/begin", {});
    await mutation("/api/v1/setup/web/provider", {
      name: document.querySelector("#provider-name").value,
      providerType: document.querySelector("#provider-type").value,
      endpoint: document.querySelector("#provider-endpoint").value,
      model: document.querySelector("#provider-model").value,
    });
    const credential = credentialInput.value;
    credentialInput.value = "";
    setupStatus("Validating provider through the shared setup service…");
    await mutation("/api/v1/setup/web/provider/credential", credential, "text/plain;charset=UTF-8");

    const agent = {
      name: document.querySelector("#agent-name").value,
      expertise: "local agent harness",
      mission: "operate within explicit AgentForge policy",
      preferredLanguage: "en",
      timeZone: document.querySelector("#agent-timezone").value,
      responseStyle: "concise and evidence-backed",
      defaultWorkspace: null,
    };
    setupStatus("Previewing effective policy and capabilities…");
    await mutation("/api/v1/setup/web/agent/preview", agent);
    await mutation("/api/v1/setup/web/agent", agent);
    setupStatus("Running minimum-viability checks…");
    await mutation("/api/v1/setup/web/complete", {});
    csrfToken = null;
    setupStatus("Setup complete. AgentForge is Ready.", "ok");
    await refreshStatus();
  } catch (error) {
    credentialInput.value = "";
    setupStatus(error instanceof Error ? error.message : "Setup failed safely.", "error");
  } finally {
    elements.setupSubmit.disabled = false;
  }
}

elements.refresh.addEventListener("click", refreshStatus);
elements.setupForm.addEventListener("submit", runSetup);
refreshStatus();
loadSetupNonce();
