// Vive mientras se graba. Va escribiendo en el disco privado de la extensión (OPFS) y no en memoria,
// para que una grabación larga no dependa de la RAM.

const TYPES = [
  ["video/mp4;codecs=avc1.640033,mp4a.40.2", "mp4"],
  ["video/mp4", "mp4"],
  ["video/webm;codecs=vp9,opus", "webm"],
  ["video/webm", "webm"],
];
const SIZES = { 720: [1280, 720], 1080: [1920, 1080], 1440: [2560, 1440], 2160: [3840, 2160] };

let recorder = null;
let stream = null;
let audioContext = null;
let writable = null;
let opfsName = null;
let writes = Promise.resolve();

// Tasa de vídeo como en la aplicación: unos 0,065 bits por píxel.
const videoBits = (w, h, fps) => Math.round(w * h * fps * 0.065);

async function start({ streamId, resolution, fps, keepSound }) {
  const [width, height] = SIZES[resolution] ?? SIZES[1080];
  const source = { chromeMediaSource: "tab", chromeMediaSourceId: streamId };
  stream = await navigator.mediaDevices.getUserMedia({
    audio: { mandatory: source },
    video: { mandatory: { ...source, minWidth: width, maxWidth: width, minHeight: height, maxHeight: height, maxFrameRate: fps } },
  });
  // Por defecto, el sonido solo va a la grabación: no sale por los altavoces.
  if (keepSound) {
    audioContext = new AudioContext();
    audioContext.createMediaStreamSource(stream).connect(audioContext.destination);
  }
  const [mimeType, extension] = TYPES.find(([t]) => MediaRecorder.isTypeSupported(t));
  const root = await navigator.storage.getDirectory();
  opfsName = `grabacion-${Date.now()}.${extension}`;
  writable = await (await root.getFileHandle(opfsName, { create: true })).createWritable();

  recorder = new MediaRecorder(stream, {
    mimeType, videoBitsPerSecond: videoBits(width, height, fps), audioBitsPerSecond: 160000,
  });
  recorder.ondataavailable = (e) => {
    if (e.data.size > 0)
      writes = writes.then(() => writable.write(e.data));
  };
  recorder.onstop = save;
  recorder.onerror = (e) => fail(e.error?.message ?? "recorder");
  // Al cerrarse la pestaña la pista termina: se para y se guarda lo grabado.
  stream.getVideoTracks()[0].onended = () => recorder?.state === "recording" && recorder.stop();
  await untilFluent();
  recorder.start(2000);
  return { extension };
}

// Al empezar, la pestaña se redimensiona al tamaño de la captura y la imagen se queda quieta unos
// segundos: se espera a que lleguen imágenes seguidas, para no empezar el vídeo congelado.
function untilFluent(maxMs = 5000) {
  const video = document.createElement("video");
  video.muted = true;
  video.srcObject = new MediaStream(stream.getVideoTracks());
  video.play();
  return new Promise((resolve) => {
    let last = 0;
    let steady = 0;
    const done = () => { video.srcObject = null; resolve(); };
    const limit = setTimeout(done, maxMs);
    const onFrame = (now) => {
      steady = now - last < 200 ? steady + 1 : 0;
      last = now;
      if (steady >= 5) {
        clearTimeout(limit);
        done();
      } else
        video.requestVideoFrameCallback(onFrame);
    };
    video.requestVideoFrameCallback(onFrame);
  });
}

function release() {
  stream?.getTracks().forEach((t) => t.stop());
  audioContext?.close();
  stream = recorder = audioContext = null;
}

async function save() {
  try {
    await writes;
    await writable.close();
    const root = await navigator.storage.getDirectory();
    const file = await (await root.getFileHandle(opfsName)).getFile();
    release();
    await chrome.runtime.sendMessage({ target: "background", cmd: "finished", url: URL.createObjectURL(file), opfsName });
  } catch (e) {
    await fail(e.message);
  }
}

async function fail(error) {
  release();
  await chrome.runtime.sendMessage({ target: "background", cmd: "finished", error });
}

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.target !== "offscreen")
    return;
  if (msg.cmd === "start")
    start(msg).then((r) => reply({ ok: true, ...r }), (e) => { release(); reply({ ok: false, error: e.message }); });
  else if (msg.cmd === "stop") {
    if (recorder?.state === "recording")
      recorder.stop();
    reply({ ok: true });
  } else if (msg.cmd === "discard")
    navigator.storage.getDirectory().then((r) => r.removeEntry(msg.opfsName)).finally(() => reply({ ok: true }));
  return true;
});
