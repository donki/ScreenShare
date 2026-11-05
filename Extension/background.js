// Graba una pestaña con tabCapture. El MediaRecorder vive en un documento fuera de pantalla porque el
// service worker no puede usar getUserMedia ni vive lo que dura una grabación.

const OFFSCREEN = "offscreen.html";

async function getState() {
  return (await chrome.storage.session.get("recording")).recording ?? null;
}

async function setBadge(on) {
  await chrome.action.setBadgeBackgroundColor({ color: "#C62828" });
  await chrome.action.setBadgeText({ text: on ? "REC" : "" });
}

async function ensureOffscreen() {
  if (await chrome.offscreen.hasDocument())
    return;
  await chrome.offscreen.createDocument({
    url: OFFSCREEN,
    reasons: ["USER_MEDIA", "BLOBS"],
    justification: "Grabar la pestaña con MediaRecorder y guardar el fichero.",
  });
}

// Se inyecta en la pestaña: el vídeo más grande pasa a ocuparla entera. requestFullscreen pide un
// gesto del usuario, que no hay; con esto vale también si la pestaña ya no está delante.
function fillTab(on) {
  const ATTR = "data-soc-screenshare-fill";
  document.querySelectorAll(`[${ATTR}]`).forEach((e) => {
    e.style.cssText = e.getAttribute(ATTR);
    e.removeAttribute(ATTR);
  });
  if (!on)
    return;
  const area = (e) => e.getBoundingClientRect().width * e.getBoundingClientRect().height;
  const best = [...document.querySelectorAll("video, canvas, iframe")].sort((a, b) => area(b) - area(a))[0];
  if (!best || area(best) === 0)
    return;
  best.setAttribute(ATTR, best.style.cssText);
  best.style.cssText += ";position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;"
    + "max-width:none!important;max-height:none!important;margin:0!important;z-index:2147483647!important;"
    + "background:#000!important;object-fit:contain!important;";
}

async function setFill(tabId, on) {
  try {
    await chrome.scripting.executeScript({ target: { tabId }, func: fillTab, args: [on] });
  } catch (e) {
    // Páginas del navegador o de otra extensión, o una pestaña ya cerrada: se graba tal cual.
    console.warn("fill", e.message);
  }
}

async function start({ tabId, resolution, fps, keepSound, fill }) {
  if (await getState())
    throw new Error("busy");
  const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tabId });
  await ensureOffscreen();
  if (fill)
    await setFill(tabId, true);
  const tab = await chrome.tabs.get(tabId);
  const reply = await chrome.runtime.sendMessage({
    target: "offscreen", cmd: "start", streamId, resolution, fps, keepSound,
  });
  if (!reply?.ok) {
    if (fill)
      await setFill(tabId, false);
    throw new Error(reply?.error ?? "start");
  }
  await chrome.storage.session.set({
    recording: { tabId, title: tab.title, startedAt: Date.now(), fill, extension: reply.extension },
  });
  await setBadge(true);
}

const pad = (n) => String(n).padStart(2, "0");

function fileName(extension) {
  const d = new Date();
  const when = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}.${pad(d.getMinutes())}.${pad(d.getSeconds())}`;
  return `ScreenShare/${chrome.i18n.getMessage("fileName")} ${when}.${extension}`;
}

// Salió bien o se cerró la pestaña: en los dos casos lo grabado se guarda.
async function finished({ url, opfsName, error }) {
  const state = await getState();
  await chrome.storage.session.remove("recording");
  await setBadge(false);
  if (state?.fill)
    await setFill(state.tabId, false);
  if (error) {
    await chrome.storage.session.set({ lastError: error });
    return;
  }
  const id = await chrome.downloads.download({ url, filename: fileName(state?.extension ?? "mp4") });
  await chrome.storage.session.set({ pendingDownload: { id, opfsName } });
}

chrome.downloads.onChanged.addListener(async (delta) => {
  const { pendingDownload } = await chrome.storage.session.get("pendingDownload");
  if (delta.id !== pendingDownload?.id || !delta.state || delta.state.current === "in_progress")
    return;
  await chrome.storage.session.remove("pendingDownload");
  if (delta.state.current === "interrupted")
    await chrome.storage.session.set({ lastError: delta.error?.current ?? "download" });
  await chrome.runtime.sendMessage({ target: "offscreen", cmd: "discard", opfsName: pendingDownload.opfsName });
  await chrome.offscreen.closeDocument();
});

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.target !== "background")
    return;
  const work = msg.cmd === "start" ? start(msg)
    : msg.cmd === "stop" ? chrome.runtime.sendMessage({ target: "offscreen", cmd: "stop" })
    : msg.cmd === "finished" ? finished(msg)
    : Promise.resolve();
  work.then(() => reply({ ok: true }), (e) => reply({ ok: false, error: e.message }));
  return true;
});
