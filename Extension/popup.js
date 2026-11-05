const $ = (id) => document.getElementById(id);
const t = (key) => chrome.i18n.getMessage(key);
const DEFAULTS = { resolution: "1080", fps: "30", fill: true, keepSound: false, hours: "", minutes: "", seconds: "" };
let timer = null;

document.querySelectorAll("[data-i18n]").forEach((e) => { e.textContent = t(e.dataset.i18n); });
document.querySelectorAll("[data-i18n-title]").forEach((e) => {
  e.title = t(e.dataset.i18nTitle);
  e.setAttribute("aria-label", e.title);
});

const options = () => ({
  resolution: $("resolution").value, fps: $("fps").value, fill: $("fill").checked, keepSound: $("keepSound").checked,
  hours: $("hours").value, minutes: $("minutes").value, seconds: $("seconds").value,
});

// Cero o vacío: sin límite.
const limitSeconds = () => Math.min(Number($("hours").value) * 3600 + Number($("minutes").value) * 60 + Number($("seconds").value), 359999) || 0;

function showLimit() {
  const s = limitSeconds();
  const two = (n) => String(n).padStart(2, "0");
  $("limitHint").textContent = s ? t("limitAt").replace("{0}", `${two(Math.floor(s / 3600))}:${two(Math.floor(s / 60) % 60)}:${two(s % 60)}`) : t("limitNone");
}
["hours", "minutes", "seconds"].forEach((id) => $(id).addEventListener("input", showLimit));

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
  const reply = await chrome.runtime.sendMessage({ target: "background", cmd: "start", tabId: tab.id, ...o, fps: Number(o.fps), limit: limitSeconds() });
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
  $("hours").value = o.hours;
  $("minutes").value = o.minutes;
  $("seconds").value = o.seconds;
  showLimit();
});
render();
