"use strict";

const form = document.querySelector("#build-form");
const messages = document.querySelector("#messages");
const commands = document.querySelector("#commands");
const runResult = document.querySelector("#run-result");
const buildLog = document.querySelector("#build-log");
const buildButton = document.querySelector("#build-button");
const cancelButton = document.querySelector("#cancel-button");
const armv7Warning = document.querySelector("#armv7-warning");
const bundledNote = document.querySelector("#bundled-note");
let defaults = {};
let timer = null;
let logOffset = 0;
let pollTimer = null;
let finalDestination = "";
const sessionToken = new URLSearchParams(window.location.search).get("token") || "";

function api(path, parameters = {}) {
  const query = new URLSearchParams({token: sessionToken, ...parameters});
  return `${path}?${query.toString()}`;
}

function value(name) {
  const element = form.elements[name];
  return element.type === "checkbox" ? element.checked : element.value;
}

function selected(name) {
  return form.querySelector(`input[name="${name}"]:checked`).value;
}

function options() {
  return {
    source_apk: value("source_apk"), dest_apk: value("dest_apk"), abi: selected("abi"),
    server_mode: selected("server_mode"), server_host: value("server_host"),
    server_port: value("server_port"), scheme: value("scheme"),
    keep_other_abi: String(value("keep_other_abi")), patched_il2cpp: value("patched_il2cpp"),
    auto_patch_il2cpp: String(value("auto_patch_il2cpp")), il2cpp_source: value("il2cpp_source"),
    keystore: value("keystore"), ks_pass: value("ks_pass"), key_pass: value("key_pass"),
    do_install: String(value("do_install")), legible: defaults.legible || "", zipalign: defaults.zipalign || "",
    apksigner: defaults.apksigner || "", java: defaults.java || "", adb: defaults.adb || ""
  };
}

function quote(arg) {
  return /[\s"']/.test(arg) ? JSON.stringify(arg) : arg;
}

function updateMode() {
  const bundled = selected("server_mode") === "bundled";
  const host = form.elements.server_host;
  const port = form.elements.server_port;
  const scheme = form.elements.scheme;
  if (bundled) {
    host.value = "127.0.0.1";
    port.value = "8080";
    scheme.value = "http";
  } else if (host.disabled) {
    host.value = "";
    port.value = "8443";
    scheme.value = "https";
  }
  [host, port, scheme].forEach((element) => { element.disabled = bundled; });
  bundledNote.classList.toggle("hidden", !bundled);
}

function updateArmv7Warning() {
  const show = selected("abi") === "armeabi-v7a" && !value("patched_il2cpp").trim() && !value("auto_patch_il2cpp");
  armv7Warning.classList.toggle("hidden", !show);
}

function updateIl2cppSourceAbi() {
  const source = form.elements.il2cpp_source;
  const abi = selected("abi");
  source.value = source.value.replace(/([/\\]lib[/\\])(arm64-v8a|armeabi-v7a)([/\\]libil2cpp\.so)$/, `$1${abi}$3`);
}

function renderMessages(result) {
  messages.replaceChildren();
  const errors = result.errors || [];
  const warnings = result.warnings || [];
  for (const text of errors) {
    const item = document.createElement("p");
    item.className = "alert error";
    item.textContent = text;
    messages.append(item);
  }
  for (const text of warnings) {
    const item = document.createElement("p");
    item.className = "alert warning";
    item.textContent = text;
    messages.append(item);
  }
  if (!errors.length && !warnings.length) {
    const item = document.createElement("p");
    item.className = "alert success";
    item.textContent = "Options look ready to build.";
    messages.append(item);
  }
}

function appendLogLines(lines) {
  for (const line of lines) {
    buildLog.appendChild(document.createTextNode(line + "\n"));
  }
  buildLog.scrollTop = buildLog.scrollHeight;
}

function setRunningState(isRunning) {
  buildButton.disabled = isRunning;
  cancelButton.disabled = !isRunning;
}

function setRunResult(text) {
  runResult.textContent = text;
}

async function refresh() {
  updateArmv7Warning();
  try {
    const response = await fetch(api("/api/validate"), {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(options())});
    const result = await response.json();
    renderMessages(result);
    commands.textContent = result.steps.map((step) => `${step.name}:\n${step.argv.map(quote).join(" ")}`).join("\n\n");
  } catch (error) {
    messages.textContent = `Could not refresh command preview: ${error.message}`;
  }
}

function scheduleRefresh() {
  clearTimeout(timer);
  timer = setTimeout(refresh, 120);
}

async function pollLog() {
  try {
    const response = await fetch(api("/api/log", {offset: String(logOffset)}));
    const data = await response.json();
    if (data.lines && data.lines.length) {
      appendLogLines(data.lines);
    }
    logOffset = data.next_offset;
    if (data.status === "running") {
      const stepInfo = data.step_total ? ` - ${data.step} (${data.step_index}/${data.step_total})` : "";
      setRunResult(`Running${stepInfo}...`);
      pollTimer = setTimeout(pollLog, 1000);
    } else {
      finishRun(data);
    }
  } catch (error) {
    pollTimer = setTimeout(pollLog, 1000);
  }
}

function finishRun(data) {
  setRunningState(false);
  if (data.status === "succeeded") {
    let dest = finalDestination;
    for (const line of data.lines || []) {
      const match = line.match(/finished signed APK at (.+)/);
      if (match) { dest = match[1]; break; }
    }
    setRunResult(dest ? `Build succeeded: ${dest}` : "Build succeeded.");
  } else if (data.status === "failed") {
    setRunResult(`Build failed at step "${data.step}" (exit code ${data.exit_code}).`);
  } else if (data.status === "cancelled") {
    setRunResult("Build cancelled.");
  } else {
    setRunResult(`Build ended (status: ${data.status}).`);
  }
}

form.addEventListener("input", scheduleRefresh);
form.addEventListener("change", (event) => {
  if (event.target.name === "server_mode") updateMode();
  if (event.target.name === "abi") {
    updateIl2cppSourceAbi();
    if (selected("abi") === "armeabi-v7a" && !value("patched_il2cpp").trim()) {
      form.elements.patched_il2cpp.value = defaults.suggested_patched_il2cpp || "";
    }
  }
  scheduleRefresh();
});

buildButton.addEventListener("click", async () => {
  setRunResult("");
  try {
    const response = await fetch(api("/api/run"), {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(options())});
    const result = await response.json();
    if (response.status === 400) {
      renderMessages(result);
      return;
    }
    if (response.status === 409) {
      setRunResult(result.error || "A build is already running.");
      return;
    }
    if (!response.ok || !result.started) {
      setRunResult(result.error || "Could not start the build.");
      return;
    }
    buildLog.replaceChildren();
    logOffset = 0;
    const signingStep = (result.steps || []).find((step) => step.name === "sign APK");
    finalDestination = signingStep && signingStep.argv.includes("--out")
      ? signingStep.argv[signingStep.argv.indexOf("--out") + 1] || ""
      : "";
    setRunningState(true);
    const total = result.steps ? result.steps.length : 0;
    setRunResult(total ? `Running step 1 of ${total}: ${result.steps[0].name}...` : "Running...");
    pollLog();
  } catch (error) {
    setRunResult(error.message);
  }
});

cancelButton.addEventListener("click", async () => {
  try {
    await fetch(api("/api/cancel"), {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(options())});
  } catch (error) {
    setRunResult(error.message);
  }
});

async function start() {
  const response = await fetch(api("/api/defaults"));
  const data = await response.json();
  defaults = {...data.defaults, suggested_patched_il2cpp: data.suggested_patched_il2cpp || ""};
  for (const [name, item] of Object.entries(defaults)) {
    const element = form.elements[name];
    if (!element) continue;
    if (element.type === "checkbox") element.checked = Boolean(item);
    else if (element.type !== "radio") element.value = item;
  }
  form.querySelector(`input[name="abi"][value="${defaults.abi}"]`).checked = true;
  form.querySelector(`input[name="server_mode"][value="${defaults.server_mode}"]`).checked = true;
  updateMode();
  await refresh();
}

start().catch((error) => { messages.textContent = `Could not load defaults: ${error.message}`; });
