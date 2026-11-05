const $ = (id) => document.getElementById(id);
const t = (key) => chrome.i18n.getMessage(key);
const DEFAULTS = { resolution: "1080", fps: "30", fill: true, keepSound: false };
let timer = null;

document.querySelectorAll("[data-i18n]").forEach((e) => { e.textContent = t(e.dataset.i18n); });
document.querySelectorAll("[data-i18n-title]").forEach((e) => {
  e.title = t(e.dataset.i18nTitle);
  e.setAttribute("aria-label", e.title);
});

const options = () => ({
  resolution: $("resolution").value, fps: $("fps").value, fill: $("fill").checked, keepSound: $("keepSound").checked,
});

async function render() {
  const { recording, lastError } = await chrome.storage.session.get(["recording", "lastError"]);
  $("idle").hidden = !!recording;
  $("busy").hidden = !recording;
  $("message").textContent = lastError ? `${t("error")}: ${lastError}` : "";
  clearInterval(timer);
  if (!recording)
    return;
  $("title").textContent = recording.title ?? "";
  const tick = () => {
    const s = Math.floor((Date.now() - recording.startedAt) / 1000);
    $("time").textContent = `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
  };
  tick();
  timer = setInterval(tick, 1000);
}

$("start").addEventListener("click", async () => {
  await chrome.storage.session.remove("lastError");
  await chrome.storage.local.set({ options: options() });
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const o = options();
  const reply = await chrome.runtime.sendMessage({ target: "background", cmd: "start", tabId: tab.id, ...o, fps: Number(o.fps) });
  if (!reply?.ok)
    $("message").textContent = `${t("error")}: ${reply?.error}`;
  else
    window.close();
});

$("stop").addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ target: "background", cmd: "stop" });
  window.close();
});

chrome.storage.local.get("options").then(({ options: saved }) => {
  const o = { ...DEFAULTS, ...saved };
  $("resolution").value = o.resolution;
  $("fps").value = o.fps;
  $("fill").checked = o.fill;
  $("keepSound").checked = o.keepSound;
});
render();
