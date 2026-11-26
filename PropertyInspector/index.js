"use strict";

let ws = null;
let uuid = null;
let actionInfo = null;
let settings = null;
let currentFreqPreset = "full";
let gradientStops = [];

const ACTION_UUID = "com.musicvisualizer.loopback.visualizer";
const TAB_STATE_KEY = "musicVisualizerActiveTab";
const DEFAULT_TAB = "playback";

const FREQ_PRESETS = {
  low: { min: 20, max: 300 },
  mid: { min: 300, max: 4000 },
  high: { min: 4000, max: 20000 },
  full: { min: 20, max: 20000 },
  custom: null
};

const GRADIENT_PRESETS = [
  { name: "Matrix Green", stops: [{ pos: 0.0, color: "#154320" }, { pos: 0.7, color: "#46e661" }, { pos: 1.0, color: "#46e661" }] },
  { name: "Neon", stops: [{ pos: 0.0, color: "#00e5ff" }, { pos: 0.5, color: "#00ff88" }, { pos: 1.0, color: "#ff1744" }] },
  { name: "Fire", stops: [{ pos: 0.0, color: "#b71c1c" }, { pos: 0.45, color: "#ff6d00" }, { pos: 0.8, color: "#ffb300" }, { pos: 1.0, color: "#fff59d" }] },
  { name: "Ocean", stops: [{ pos: 0.0, color: "#0d47a1" }, { pos: 0.5, color: "#00acc1" }, { pos: 1.0, color: "#e1f5fe" }] },
  { name: "Violet", stops: [{ pos: 0.0, color: "#4a0072" }, { pos: 0.5, color: "#e040fb" }, { pos: 1.0, color: "#ffcc00" }] },
  { name: "Mono", stops: [{ pos: 0.0, color: "#111111" }, { pos: 0.5, color: "#888888" }, { pos: 1.0, color: "#ffffff" }] }
];

function defaults() {
  return {
    captureDeviceId: "default",
    freqPreset: "full",
    freqMin: 20,
    freqMax: 20000,
    barCount: 16,
    gainDb: 6.0,
    minDb: -48.0,
    slopeDbPerOct: 4.0,
    gamma: 1.0,
    noiseGateDb: -65.0,
    attackSpeed: 0.65,
    releaseSpeed: 0.25,
    smoothing: 0.22,
    peakHold: true,
    peakFallSpeed: 0.08,
    peakThickness: 2,
    topRadius: 2,
    bgColor: "#08150e",
    gradientStops: cloneStops(GRADIENT_PRESETS[0].stops),
    paused: false,
    renderMode: "bars",
    dotSpacing: 3,
    showNumericMeter: false,
    autoPauseEnabled: false,
    autoPauseTimeoutSec: 30,
    ledSegmentMode: "fixedCount",
    ledSegmentCount: 14,
    ledSegmentSizePx: 8,
    ledSegmentGapPx: 2,
    ledLowColor: "#46e661",
    ledMidColor: "#46e661",
    ledHighColor: "#154320",
    ledLowThreshold: 0.5,
    ledHighThreshold: 0.85
  };
}

window.connectElgatoStreamDeckSocket = function (port, inUUID, registerEvent, info, actionInfoIn) {
  uuid = inUUID;
  actionInfo = actionInfoIn || null;
  ws = new WebSocket("ws://127.0.0.1:" + port);

  ws.onopen = () => {
    sendRaw({ event: registerEvent, uuid: uuid });
    sendRaw({ event: "getSettings", context: uuid });
    sendToPlugin({ event: "piReady" });
    sendToPlugin({ event: "getCaptureDevices" });
  };

  ws.onmessage = (ev) => {
    try {
      const data = JSON.parse(ev.data);
      handleMessage(data);
    } catch (err) {
      console.error("PI JSON parse error", err);
    }
  };

  ws.onerror = (err) => console.error("PI socket error", err);
  ws.onclose = () => console.warn("PI socket closed");
};

function sendRaw(obj) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
}

function sendToPlugin(payload) {
  sendRaw({ action: ACTION_UUID, event: "sendToPlugin", context: uuid, payload: payload });
}

function setSettingsPayload(payload) {
  sendRaw({ event: "setSettings", context: uuid, payload: payload });
}

function handleMessage(msg) {
  if (!msg || !msg.event) return;

  if (msg.event === "didReceiveSettings") {
    const incoming = (msg.payload && msg.payload.settings) ? msg.payload.settings : {};
    settings = normalizeSettings(Object.assign(defaults(), incoming));
    loadUiFromSettings(settings);
    return;
  }

  if (msg.event === "sendToPropertyInspector") {
    handlePropertyInspectorEvent(msg.payload);
  }
}

function handlePropertyInspectorEvent(payload) {
  if (!payload) return;
  switch (payload.event) {
    case "captureDevices":
      populateCaptureDevices(payload.devices || [], payload.selectedId || "default");
      break;
    case "runtimeState":
      updateRuntimeState(payload);
      break;
    case "validationError":
      showError(payload.message || "Validation error");
      break;
    case "defaultsLoaded":
      settings = normalizeSettings(Object.assign(defaults(), payload.settings));
      loadUiFromSettings(settings);
      break;
    case "connectionStatus":
      updateConnectionStatus(payload);
      break;
    case "exportSettings":
      downloadSettingsAsFile(payload.settings || settings);
      break;
  }
}

function normalizeSettings(input) {
  const s = Object.assign(defaults(), input);

  s.captureDeviceId = s.captureDeviceId || "default";
  s.freqPreset = typeof s.freqPreset === "string" ? s.freqPreset : "full";
  s.freqMin = clampInt(s.freqMin, 20, 20000, 20);
  s.freqMax = clampInt(s.freqMax, 20, 20000, 20000);
  if (s.freqMax < s.freqMin) {
    const temp = s.freqMin;
    s.freqMin = s.freqMax;
    s.freqMax = temp;
  }
  s.barCount = clampInt(s.barCount, 2, 32, 16);

  s.gainDb = clampFloat(s.gainDb, -18.0, 30.0, 6.0);
  s.minDb = clampFloat(s.minDb, -72.0, -12.0, -48.0);
  s.slopeDbPerOct = clampFloat(s.slopeDbPerOct, 0.0, 8.0, 4.0);
  s.gamma = clampFloat(s.gamma, 0.3, 4.0, 1.0);
  s.noiseGateDb = clampFloat(s.noiseGateDb, -90.0, -20.0, -65.0);

  s.attackSpeed = clampFloat(s.attackSpeed, 0.01, 1.0, 0.65);
  s.releaseSpeed = clampFloat(s.releaseSpeed, 0.01, 1.0, 0.25);
  s.smoothing = clampFloat(s.smoothing, 0.0, 0.85, 0.22);
  s.peakHold = !!s.peakHold;
  s.peakFallSpeed = clampFloat(s.peakFallSpeed, 0.005, 0.25, 0.08);
  s.peakThickness = clampInt(s.peakThickness, 1, 8, 2);
  s.topRadius = clampInt(s.topRadius, 0, 12, 2);
  s.bgColor = normalizeHex(s.bgColor, "#08150e");
  s.paused = !!s.paused;

  if (!["bars", "vuMeter", "mirror", "dots", "ledSegments"].includes(s.renderMode)) {
    s.renderMode = "bars";
  }
  s.dotSpacing = clampInt(s.dotSpacing, 2, 10, 3);
  s.showNumericMeter = !!s.showNumericMeter;
  s.autoPauseEnabled = !!s.autoPauseEnabled;
  s.autoPauseTimeoutSec = clampInt(s.autoPauseTimeoutSec, 5, 120, 30);

  s.ledSegmentMode = s.ledSegmentMode === "fixedSize" ? "fixedSize" : "fixedCount";
  s.ledSegmentCount = clampInt(s.ledSegmentCount, 4, 24, 14);
  s.ledSegmentSizePx = clampInt(s.ledSegmentSizePx, 4, 16, 8);
  s.ledSegmentGapPx = clampInt(s.ledSegmentGapPx, 1, 4, 2);
  s.ledLowColor = normalizeHex(s.ledLowColor, "#46e661");
  s.ledMidColor = normalizeHex(s.ledMidColor, "#46e661");
  s.ledHighColor = normalizeHex(s.ledHighColor, "#154320");
  s.ledLowThreshold = clampFloat(s.ledLowThreshold, 0.0, 1.0, 0.5);
  s.ledHighThreshold = clampFloat(s.ledHighThreshold, s.ledLowThreshold, 1.0, 0.85);

  if (!Array.isArray(s.gradientStops) || s.gradientStops.length < 2) {
    s.gradientStops = cloneStops(defaults().gradientStops);
  }
  s.gradientStops = s.gradientStops
    .map(stop => ({ pos: clampFloat(stop.pos, 0, 1, 0), color: normalizeHex(stop.color, "#46e661") }))
    .sort((a, b) => a.pos - b.pos)
    .slice(0, 6);
  if (s.gradientStops.length < 2) {
    s.gradientStops = cloneStops(defaults().gradientStops);
  }
  s.gradientStops[0].pos = 0;
  s.gradientStops[s.gradientStops.length - 1].pos = 1;

  return s;
}

function loadUiFromSettings(s) {
  setSelectValue("captureDeviceId", s.captureDeviceId);

  setRange("barCount", "barCountVal", s.barCount, 0, "");
  setRange("gainDb", "gainDbVal", s.gainDb, 0, " dB", true);
  setRange("minDb", "minDbVal", s.minDb, 0, " dB");
  setRange("slopeDbPerOct", "slopeDbPerOctVal", 2, " dB");
  setRange("gamma", "gammaVal", s.gamma, 2, "");
  setRange("noiseGateDb", "noiseGateDbVal", s.noiseGateDb, 0, " dB");

  setRange("attackSpeed", "attackSpeedVal", s.attackSpeed, 2, "");
  setRange("releaseSpeed", "releaseSpeedVal", s.releaseSpeed, 2, "");
  setRange("smoothing", "smoothingVal", s.smoothing, 2, "");
  setRange("peakFallSpeed", "peakFallSpeedVal", s.peakFallSpeed, 3, "");
  setRange("peakThickness", "peakThicknessVal", s.peakThickness, 0, "");
  setRange("topRadius", "topRadiusVal", s.topRadius, 0, "");

  setRange("dotSpacing", "dotSpacingVal", s.dotSpacing, 0, "");
  setRange("autoPauseTimeoutSec", "autoPauseTimeoutSecVal", s.autoPauseTimeoutSec, 0, "");

  setRange("ledSegmentCount", "ledSegmentCountVal", s.ledSegmentCount, 0, "");
  setRange("ledSegmentSizePx", "ledSegmentSizePxVal", s.ledSegmentSizePx, 0, "");
  setRange("ledSegmentGapPx", "ledSegmentGapPxVal", s.ledSegmentGapPx, 0, "");
  setRange("ledLowThreshold", "ledLowThresholdVal", s.ledLowThreshold, 2, "");
  setRange("ledHighThreshold", "ledHighThresholdVal", s.ledHighThreshold, 2, "");
  setSelectValue("ledSegmentMode", s.ledSegmentMode);

  const ledLowColor = document.getElementById("ledLowColor");
  if (ledLowColor) ledLowColor.value = s.ledLowColor;
  const ledMidColor = document.getElementById("ledMidColor");
  if (ledMidColor) ledMidColor.value = s.ledMidColor;
  const ledHighColor = document.getElementById("ledHighColor");
  if (ledHighColor) ledHighColor.value = s.ledHighColor;

  toggleVisibility("ledSegmentCountRow", s.ledSegmentMode === "fixedCount");
  toggleVisibility("ledSegmentSizeRow", s.ledSegmentMode === "fixedSize");

  setCheckbox("peakHold", s.peakHold);
  setCheckbox("showNumericMeter", s.showNumericMeter);
  setCheckbox("autoPauseEnabled", s.autoPauseEnabled);

  toggleVisibility("dotSpacingRow", s.renderMode === "dots");
  toggleVisibility("ledSegmentSection", s.renderMode === "ledSegments");
  toggleVisibility("autoPauseTimeoutRow", s.autoPauseEnabled);

  setRenderModePillUi(s.renderMode);
  renderLedPreview(s);

  const bgColor = document.getElementById("bgColor");
  if (bgColor) bgColor.value = s.bgColor;

  const pauseBtn = document.getElementById("pauseBtn");
  if (pauseBtn) pauseBtn.textContent = s.paused ? "Resume" : "Pause";

  gradientStops = cloneStops(s.gradientStops);
  renderGradientPresets();
  renderGradientStops();
  updateGradientPreview();

  setFreqPresetUi(detectPreset(s.freqPreset, s.freqMin, s.freqMax), s.freqMin, s.freqMax);

  hideError();
}

function renderLedPreview(s) {
  const container = document.getElementById("ledPreview");
  if (!container) return;

  const containerHeight = 70 - 12;
  const previewGapPx = 1;
  const sampleHeights = [0.3, 0.6, 0.9, 0.45, 0.75, 0.55];

  const totalSegments = s.ledSegmentMode === "fixedSize"
    ? Math.max(4, Math.min(24, Math.floor((containerHeight + previewGapPx) / (s.ledSegmentSizePx + previewGapPx))))
    : s.ledSegmentCount;

  const segHeight = Math.max(1, (containerHeight - (totalSegments - 1) * previewGapPx) / totalSegments);

  container.innerHTML = "";
  sampleHeights.forEach(h => {
    const barEl = document.createElement("div");
    barEl.className = "led-preview-bar";
    barEl.style.gap = previewGapPx + "px";
    const litCount = Math.round(h * totalSegments);
    for (let i = 0; i < totalSegments; i++) {
      const segEl = document.createElement("div");
      segEl.className = "led-preview-seg";
      segEl.style.height = segHeight + "px";
      segEl.style.width = "100%";
      if (i < litCount) {
        const ratio = (i + 1) / totalSegments;
        if (ratio >= s.ledHighThreshold) segEl.style.background = s.ledHighColor;
        else if (ratio >= s.ledLowThreshold) segEl.style.background = s.ledMidColor;
        else segEl.style.background = s.ledLowColor;
      }
      barEl.appendChild(segEl);
    }
    container.appendChild(barEl);
  });
}

function detectPreset(freqPreset, min, max) {
  if (FREQ_PRESETS[freqPreset]) return freqPreset;
  for (const [key, range] of Object.entries(FREQ_PRESETS)) {
    if (!range) continue;
    if (range.min === min && range.max === max) return key;
  }
  return "custom";
}

function setFreqPresetUi(preset, customMin, customMax) {
  currentFreqPreset = preset;
  document.querySelectorAll("[data-preset]").forEach(el => {
    el.classList.toggle("on", el.dataset.preset === preset);
  });
  const customRows = document.getElementById("customFreqRows");
  if (customRows) customRows.classList.toggle("hidden", preset !== "custom");

  const minEl = document.getElementById("freqMin");
  const maxEl = document.getElementById("freqMax");
  if (!minEl || !maxEl) return;

  if (preset !== "custom" && FREQ_PRESETS[preset]) {
    minEl.value = String(FREQ_PRESETS[preset].min);
    maxEl.value = String(FREQ_PRESETS[preset].max);
  } else {
    minEl.value = String(customMin ?? 20);
    maxEl.value = String(customMax ?? 20000);
  }
}

function setRenderModePillUi(mode) {
  document.querySelectorAll("#renderModePills [data-mode]").forEach(el => {
    el.classList.toggle("on", el.dataset.mode === mode);
  });
}

function collectSettingsFromUi() {
  const s = normalizeSettings({
    captureDeviceId: getValue("captureDeviceId", "default"),
    freqPreset: currentFreqPreset,
    freqMin: parseInt(getValue("freqMin", "20"), 10),
    freqMax: parseInt(getValue("freqMax", "20000"), 10),
    barCount: parseInt(getValue("barCount", "16"), 10),
    gainDb: parseFloat(getValue("gainDb", "6.0")),
    minDb: parseFloat(getValue("minDb", "-48.0")),
    slopeDbPerOct: parseFloat(getValue("slopeDbPerOct", "4.0")),
    gamma: parseFloat(getValue("gamma", "1.0")),
    noiseGateDb: parseFloat(getValue("noiseGateDb", "-65.0")),
    attackSpeed: parseFloat(getValue("attackSpeed", "0.65")),
    releaseSpeed: parseFloat(getValue("releaseSpeed", "0.25")),
    smoothing: parseFloat(getValue("smoothing", "0.22")),
    peakHold: !!document.getElementById("peakHold")?.checked,
    peakFallSpeed: parseFloat(getValue("peakFallSpeed", "0.08")),
    peakThickness: parseInt(getValue("peakThickness", "2"), 10),
    topRadius: parseInt(getValue("topRadius", "2"), 10),
    bgColor: getValue("bgColor", "#08150e"),
    paused: !!settings?.paused,
    renderMode: settings?.renderMode || "bars",
    dotSpacing: parseInt(getValue("dotSpacing", "3"), 10),
    showNumericMeter: !!document.getElementById("showNumericMeter")?.checked,
    autoPauseEnabled: !!document.getElementById("autoPauseEnabled")?.checked,
    autoPauseTimeoutSec: parseInt(getValue("autoPauseTimeoutSec", "30"), 10),
    ledSegmentMode: getValue("ledSegmentMode", "fixedCount"),
    ledSegmentCount: parseInt(getValue("ledSegmentCount", "14"), 10),
    ledSegmentSizePx: parseInt(getValue("ledSegmentSizePx", "8"), 10),
    ledSegmentGapPx: parseInt(getValue("ledSegmentGapPx", "2"), 10),
    ledLowColor: getValue("ledLowColor", "#46e661"),
    ledMidColor: getValue("ledMidColor", "#46e661"),
    ledHighColor: getValue("ledHighColor", "#154320"),
    ledLowThreshold: parseFloat(getValue("ledLowThreshold", "0.5")),
    ledHighThreshold: parseFloat(getValue("ledHighThreshold", "0.85")),
    gradientStops: gradientStops.map(stop => ({
      pos: parseFloat(Number(stop.pos).toFixed(4)),
      color: normalizeHex(stop.color, "#46e661")
    }))
  });

  if (currentFreqPreset !== "custom" && FREQ_PRESETS[currentFreqPreset]) {
    s.freqMin = FREQ_PRESETS[currentFreqPreset].min;
    s.freqMax = FREQ_PRESETS[currentFreqPreset].max;
  }

  return s;
}

function saveSettings() {
  settings = collectSettingsFromUi();
  setSettingsPayload(settings);
}

function previewSettings() {
  const preview = collectSettingsFromUi();
  settings = preview;
  renderLedPreview(preview);
  sendToPlugin({ event: "previewSettings", settings: preview });
}

function populateCaptureDevices(devices, selectedId) {
  const select = document.getElementById("captureDeviceId");
  if (!select) return;
  const current = settings?.captureDeviceId || selectedId || "default";

  while (select.options.length > 0) select.remove(0);

  devices.forEach(device => {
    const option = document.createElement("option");
    option.value = device.id;
    option.textContent = device.name;
    select.add(option);
  });

  if (!devices.some(d => d.id === "default")) {
    const option = document.createElement("option");
    option.value = "default";
    option.textContent = "Domyślne urządzenie systemowe";
    select.add(option, 0);
  }

  select.value = devices.some(d => d.id === current) ? current : (selectedId || "default");
}

function updateRuntimeState(payload) {
  setText("statCapture", payload.isCapturing ? (payload.autoPaused ? "Auto-paused" : "Active") : "Stopped");
  setText("statSampleRate", payload.sampleRate ? payload.sampleRate + " Hz" : "-");
  const overallDb = typeof payload.overallDb === "number" ? payload.overallDb.toFixed(1) + " dB" : "-";
  setText("statOverallDb", overallDb);

  if (payload.lastError) showError(payload.lastError);
  else hideError();

  renderErrorHistory(payload.recentErrors);
}

function renderErrorHistory(errors) {
  const box = document.getElementById("errorHistoryBox");
  const list = document.getElementById("errorList");
  if (!box || !list) return;

  if (!errors || errors.length === 0) {
    box.classList.add("hidden");
    list.innerHTML = "";
    return;
  }

  box.classList.remove("hidden");
  list.innerHTML = "";
  errors.slice().reverse().forEach(err => {
    const item = document.createElement("div");
    item.className = "error-item";
    item.textContent = err;
    list.appendChild(item);
  });
}

function updateConnectionStatus(payload) {
  const badge = document.getElementById("connBadge");
  const text = document.getElementById("connBadgeText");
  if (!badge || !text) return;
  const healthy = !!payload.healthy;
  badge.classList.toggle("healthy", healthy);
  badge.classList.toggle("unhealthy", !healthy);
  text.textContent = healthy ? "Connected" : "Reconnecting...";
}

function downloadSettingsAsFile(settingsObj) {
  const json = JSON.stringify(settingsObj, null, 2);
  const blob = new Blob([json], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = "musicvisualizer-settings.json";
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function renderGradientPresets() {
  const container = document.getElementById("gradientPresetGrid");
  if (!container) return;
  container.innerHTML = "";
  GRADIENT_PRESETS.forEach(preset => {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "preset-chip";
    chip.title = preset.name;
    chip.style.background = gradientCss(preset.stops);
    chip.classList.toggle("on", presetMatches(preset.stops, gradientStops));
    chip.addEventListener("click", () => {
      gradientStops = cloneStops(preset.stops);
      renderGradientPresets();
      renderGradientStops();
      updateGradientPreview();
      saveSettings();
      previewSettings();
    });
    container.appendChild(chip);
  });
}

function renderGradientStops() {
  const container = document.getElementById("stopsContainer");
  const addBtn = document.getElementById("addStopBtn");
  if (!container) return;

  gradientStops = gradientStops
    .map(s => ({ pos: clampFloat(s.pos, 0, 1, 0), color: normalizeHex(s.color, "#ffffff") }))
    .sort((a, b) => a.pos - b.pos);

  container.innerHTML = "";
  gradientStops.forEach((stop, index) => {
    const row = document.createElement("div");
    row.className = "stop-row";

    const color = document.createElement("input");
    color.type = "color";
    color.value = stop.color;
    color.addEventListener("input", (e) => {
      gradientStops[index].color = e.target.value;
      updateGradientPreview();
      renderGradientPresets();
    });
    color.addEventListener("change", () => { saveSettings(); previewSettings(); });

    const slider = document.createElement("input");
    slider.type = "range";
    slider.min = 0;
    slider.max = 100;
    slider.value = String(Math.round(stop.pos * 100));

    const value = document.createElement("div");
    value.className = "value";
    value.textContent = slider.value + "%";

    slider.addEventListener("input", (e) => {
      gradientStops[index].pos = parseInt(e.target.value, 10) / 100;
      value.textContent = e.target.value + "%";
      updateGradientPreview();
      renderGradientPresets();
    });
    slider.addEventListener("change", () => {
      gradientStops = gradientStops.sort((a, b) => a.pos - b.pos);
      renderGradientStops();
      renderGradientPresets();
      saveSettings();
      previewSettings();
    });

    const del = document.createElement("button");
    del.type = "button";
    del.className = "mini-btn";
    del.textContent = "✕";
    del.disabled = gradientStops.length <= 2;
    del.addEventListener("click", () => {
      if (gradientStops.length <= 2) return;
      gradientStops.splice(index, 1);
      renderGradientStops();
      renderGradientPresets();
      updateGradientPreview();
      saveSettings();
      previewSettings();
    });

    row.appendChild(color);
    row.appendChild(slider);
    row.appendChild(value);
    row.appendChild(del);
    container.appendChild(row);
  });

  if (addBtn) addBtn.disabled = gradientStops.length >= 6;
}

function addGradientStop() {
  if (gradientStops.length >= 6) return;
  const sorted = cloneStops(gradientStops).sort((a, b) => a.pos - b.pos);
  let bestGap = -1;
  let insertIndex = 0;
  for (let i = 0; i < sorted.length - 1; i++) {
    const gap = sorted[i + 1].pos - sorted[i].pos;
    if (gap > bestGap) {
      bestGap = gap;
      insertIndex = i;
    }
  }
  const left = sorted[insertIndex];
  const right = sorted[insertIndex + 1];
  const pos = (left.pos + right.pos) / 2;
  const color = blendHex(left.color, right.color, 0.5);

  gradientStops.push({ pos, color });
  gradientStops = gradientStops.sort((a, b) => a.pos - b.pos);
  renderGradientStops();
  renderGradientPresets();
  updateGradientPreview();
  saveSettings();
  previewSettings();
}

function updateGradientPreview() {
  const preview = document.getElementById("gradPreview");
  if (!preview) return;
  preview.style.background = gradientCss(gradientStops);
}

function gradientCss(stops) {
  const sorted = cloneStops(stops).sort((a, b) => a.pos - b.pos);
  return "linear-gradient(90deg, " + sorted.map(stop => stop.color + " " + Math.round(stop.pos * 100) + "%").join(", ") + ")";
}

function presetMatches(a, b) {
  if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
  const aa = cloneStops(a).sort((x, y) => x.pos - y.pos);
  const bb = cloneStops(b).sort((x, y) => x.pos - y.pos);
  for (let i = 0; i < aa.length; i++) {
    if (normalizeHex(aa[i].color, "#ffffff") !== normalizeHex(bb[i].color, "#ffffff")) return false;
    if (Math.abs(aa[i].pos - bb[i].pos) > 0.01) return false;
  }
  return true;
}

function loadActiveTab() {
  try {
    return localStorage.getItem(TAB_STATE_KEY) || DEFAULT_TAB;
  } catch {
    return DEFAULT_TAB;
  }
}

function saveActiveTab(tabId) {
  try {
    localStorage.setItem(TAB_STATE_KEY, tabId);
  } catch {
  }
}

function setActiveTab(tabId) {
  const panels = document.querySelectorAll(".tab-panel[data-tab-id]");
  const buttons = document.querySelectorAll(".tab-btn[data-tab]");
  let matched = false;

  panels.forEach(panel => {
    const isActive = panel.dataset.tabId === tabId;
    panel.classList.toggle("active", isActive);
    if (isActive) matched = true;
  });

  if (!matched) {
    tabId = DEFAULT_TAB;
    panels.forEach(panel => panel.classList.toggle("active", panel.dataset.tabId === tabId));
  }

  buttons.forEach(btn => btn.classList.toggle("active", btn.dataset.tab === tabId));
  saveActiveTab(tabId);
}

function initTabs() {
  const stored = loadActiveTab();
  setActiveTab(stored);

  document.querySelectorAll(".tab-btn[data-tab]").forEach(btn => {
    btn.addEventListener("click", () => {
      setActiveTab(btn.dataset.tab);
    });
  });
}

function setText(id, text) {
  const el = document.getElementById(id);
  if (el) el.textContent = text;
}

function setSelectValue(id, value) {
  const el = document.getElementById(id);
  if (el) el.value = value;
}

function setCheckbox(id, checked) {
  const el = document.getElementById(id);
  if (el) el.checked = !!checked;
}

function toggleVisibility(id, visible) {
  const el = document.getElementById(id);
  if (el) el.classList.toggle("hidden", !visible);
}

function setRange(id, valueId, value, digits, suffix = "", plusSign = false) {
  const el = document.getElementById(id);
  const out = document.getElementById(valueId);
  if (el) el.value = String(value);
  if (out) {
    const num = Number(value);
    const sign = (plusSign && num > 0) ? "+" : "";
    out.textContent = sign + (digits === 0 ? String(Math.round(num)) : num.toFixed(digits)) + suffix;
  }
}

function getValue(id, fallback) {
  const el = document.getElementById(id);
  return el ? el.value : fallback;
}

function showError(message) {
  const box = document.getElementById("runtimeErrorBox");
  if (!box) return;
  box.textContent = message;
  box.classList.remove("hidden");
}

function hideError() {
  const box = document.getElementById("runtimeErrorBox");
  if (!box) return;
  box.textContent = "";
  box.classList.add("hidden");
}

function cloneStops(stops) {
  return JSON.parse(JSON.stringify(stops));
}

function clampInt(value, min, max, fallback) {
  const n = parseInt(value, 10);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(min, Math.min(max, n));
}

function clampFloat(value, min, max, fallback) {
  const n = parseFloat(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(min, Math.min(max, n));
}

function normalizeHex(hex, fallback) {
  if (typeof hex !== "string") return fallback;
  let value = hex.trim();
  if (!value.startsWith("#")) value = "#" + value;
  if (/^#[0-9a-fA-F]{6}$/.test(value)) return value.toLowerCase();
  return fallback;
}

function hexToRgb(hex) {
  const match = /^#?([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})$/.exec(hex);
  if (!match) return null;
  return {
    r: parseInt(match[1], 16),
    g: parseInt(match[2], 16),
    b: parseInt(match[3], 16)
  };
}

function rgbToHex(r, g, b) {
  return "#" + [r, g, b].map(v => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, "0")).join("");
}

function blendHex(a, b, t) {
  const ra = hexToRgb(a);
  const rb = hexToRgb(b);
  if (!ra || !rb) return "#ffffff";
  return rgbToHex(
    ra.r + (rb.r - ra.r) * t,
    ra.g + (rb.g - ra.g) * t,
    ra.b + (rb.b - ra.b) * t
  );
}

function bindRange(id, outId, digits, suffix = "", plusSign = false, onInputAction = null) {
  const el = document.getElementById(id);
  const out = document.getElementById(outId);
  if (!el || !out) return;
  el.addEventListener("input", (e) => {
    const num = Number(e.target.value);
    const sign = (plusSign && num > 0) ? "+" : "";
    out.textContent = sign + (digits === 0 ? String(Math.round(num)) : num.toFixed(digits)) + suffix;
    if (typeof onInputAction === "function") onInputAction();
  });
  el.addEventListener("change", () => saveSettings());
}

function bindSimpleChange(id, handler) {
  const el = document.getElementById(id);
  if (!el) return;
  el.addEventListener("change", () => {
    handler();
    saveSettings();
  });
}

function initTooltips() {
  const bubble = document.getElementById("tooltipBubble");
  if (!bubble) return;
  document.querySelectorAll(".help-icon[data-help]").forEach(icon => {
    icon.addEventListener("mouseenter", () => {
      bubble.textContent = icon.dataset.help;
      bubble.classList.add("visible");
      positionTooltip(icon, bubble);
    });
    icon.addEventListener("mousemove", () => positionTooltip(icon, bubble));
    icon.addEventListener("mouseleave", () => bubble.classList.remove("visible"));
  });
}

function positionTooltip(iconEl, bubbleEl) {
  const rect = iconEl.getBoundingClientRect();
  const bubbleWidth = 240;
  const margin = 8;
  let left = rect.right + margin;
  let top = rect.top - 4;

  if (left + bubbleWidth > window.innerWidth) left = rect.left - bubbleWidth - margin;
  if (left < margin) left = margin;
  if (top + 80 > window.innerHeight) top = rect.bottom + margin;
  if (top < margin) top = margin;

  bubbleEl.style.left = left + "px";
  bubbleEl.style.top = top + "px";
}

function initUi() {
  renderGradientPresets();
  renderGradientStops();
  updateGradientPreview();
  initTooltips();
  initTabs();

  document.querySelectorAll("[data-preset]").forEach(pill => {
    pill.addEventListener("click", () => {
      const preset = pill.dataset.preset;
      if (!preset) return;
      if (preset === "custom") {
        setFreqPresetUi("custom", parseInt(getValue("freqMin", "20"), 10), parseInt(getValue("freqMax", "20000"), 10));
      } else {
        const range = FREQ_PRESETS[preset];
        setFreqPresetUi(preset, range.min, range.max);
      }
      saveSettings();
      previewSettings();
    });
  });

  document.querySelectorAll("#renderModePills [data-mode]").forEach(pill => {
    pill.addEventListener("click", () => {
      const mode = pill.dataset.mode;
      if (!mode || !settings) return;
      settings.renderMode = mode;
      setRenderModePillUi(mode);
      toggleVisibility("dotSpacingRow", mode === "dots");
      toggleVisibility("ledSegmentSection", mode === "ledSegments");
      if (mode === "ledSegments") renderLedPreview(settings);
      saveSettings();
      previewSettings();
    });
  });

  bindRange("barCount", "barCountVal", 0, "", false, previewSettings);
  bindRange("gainDb", "gainDbVal", 0, " dB", true, previewSettings);
  bindRange("minDb", "minDbVal", 0, " dB", false, previewSettings);
  bindRange("slopeDbPerOct", "slopeDbPerOctVal", 2, " dB", false, previewSettings);
  bindRange("gamma", "gammaVal", 2, "", false, previewSettings);
  bindRange("noiseGateDb", "noiseGateDbVal", 0, " dB", false, previewSettings);

  bindRange("attackSpeed", "attackSpeedVal", 2, "", false, previewSettings);
  bindRange("releaseSpeed", "releaseSpeedVal", 2, "", false, previewSettings);
  bindRange("smoothing", "smoothingVal", 2, "", false, previewSettings);
  bindRange("peakFallSpeed", "peakFallSpeedVal", 3, "", false, previewSettings);
  bindRange("peakThickness", "peakThicknessVal", 0, "", false, previewSettings);
  bindRange("topRadius", "topRadiusVal", 0, "", false, previewSettings);
  bindRange("dotSpacing", "dotSpacingVal", 0, "", false, previewSettings);
  bindRange("autoPauseTimeoutSec", "autoPauseTimeoutSecVal", 0, "", false, previewSettings);

  bindRange("ledSegmentCount", "ledSegmentCountVal", 0, "", false, previewSettings);
  bindRange("ledSegmentSizePx", "ledSegmentSizePxVal", 0, "", false, previewSettings);
  bindRange("ledSegmentGapPx", "ledSegmentGapPxVal", 0, "", false, previewSettings);
  bindRange("ledLowThreshold", "ledLowThresholdVal", 2, "", false, previewSettings);
  bindRange("ledHighThreshold", "ledHighThresholdVal", 2, "", false, previewSettings);

  ["captureDeviceId", "freqMin", "freqMax", "peakHold", "bgColor",
   "showNumericMeter", "ledLowColor", "ledMidColor", "ledHighColor"
  ].forEach(id => bindSimpleChange(id, () => previewSettings()));

  bindSimpleChange("ledSegmentMode", () => {
    const mode = getValue("ledSegmentMode", "fixedCount");
    toggleVisibility("ledSegmentCountRow", mode === "fixedCount");
    toggleVisibility("ledSegmentSizeRow", mode === "fixedSize");
    previewSettings();
  });

  bindSimpleChange("autoPauseEnabled", () => {
    toggleVisibility("autoPauseTimeoutRow", !!document.getElementById("autoPauseEnabled")?.checked);
    previewSettings();
  });

  const refreshBtn = document.getElementById("refreshDevicesBtn");
  if (refreshBtn) refreshBtn.addEventListener("click", () => sendToPlugin({ event: "getCaptureDevices" }));

  const pauseBtn = document.getElementById("pauseBtn");
  if (pauseBtn) pauseBtn.textContent = settings?.paused ? "Resume" : "Pause";
  if (pauseBtn) pauseBtn.addEventListener("click", () => {
    settings = collectSettingsFromUi();
    settings.paused = !settings.paused;
    pauseBtn.textContent = settings.paused ? "Resume" : "Pause";
    setSettingsPayload(settings);
    sendToPlugin({ event: "setPaused", paused: settings.paused });
  });

  const exportBtn = document.getElementById("exportBtn");
  if (exportBtn) exportBtn.addEventListener("click", () => sendToPlugin({ event: "requestExport" }));

  const importBtn = document.getElementById("importBtn");
  const importFileInput = document.getElementById("importFileInput");
  if (importBtn && importFileInput) {
    importBtn.addEventListener("click", () => importFileInput.click());
    importFileInput.addEventListener("change", (e) => {
      const file = e.target.files && e.target.files[0];
      if (!file) return;
      const reader = new FileReader();
      reader.onload = () => {
        try {
          const imported = JSON.parse(String(reader.result));
          sendToPlugin({ event: "importSettings", settings: imported });
        } catch (err) {
          showError("Nie udało się wczytać pliku ustawień: " + err.message);
        }
      };
      reader.readAsText(file);
      importFileInput.value = "";
    });
  }

  const addStopBtn = document.getElementById("addStopBtn");
  if (addStopBtn) addStopBtn.addEventListener("click", () => addGradientStop());
}

document.addEventListener("DOMContentLoaded", () => {
  settings = defaults();
  initUi();
  loadUiFromSettings(settings);
});
