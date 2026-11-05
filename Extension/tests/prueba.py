"""Prueba real de la extensión en un Edge sin ventana: graba una pestaña que pasa a segundo plano y
comprueba el MP4 (imagen que sigue cambiando y sonido). Uso: python tests/prueba.py [segundos]"""
import asyncio, os, re, subprocess, sys, tempfile, http.server, threading, functools
import imageio_ffmpeg
from playwright.async_api import async_playwright

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ID = "fbgpoojimapinmhibnpoocnndddpiilb"
FF = imageio_ffmpeg.get_ffmpeg_exe()
SEG = int(sys.argv[1]) if len(sys.argv) > 1 else 10
MUTE = "--mute-tab" in sys.argv
DELANTE = "--delante" in sys.argv
FILL = "--fill" in sys.argv
CERRAR = "--cerrar" in sys.argv
LIMITE = 4 if "--limite" in sys.argv else 0


def servir():
    h = functools.partial(http.server.SimpleHTTPRequestHandler, directory=os.path.join(RAIZ, "tests"))
    s = http.server.ThreadingHTTPServer(("127.0.0.1", 0), h)
    threading.Thread(target=s.serve_forever, daemon=True).start()
    return s.server_address[1]


async def main():
    puerto = servir()
    with tempfile.TemporaryDirectory() as perfil, tempfile.TemporaryDirectory() as bajadas:
        async with async_playwright() as p:
            ctx = await p.chromium.launch_persistent_context(perfil, channel="msedge", headless=True, downloads_path=bajadas, accept_downloads=True, ignore_default_args=["--mute-audio"], args=[
                f"--disable-extensions-except={RAIZ}", f"--load-extension={RAIZ}", f"--allowlisted-extension-id={ID}",
                "--autoplay-policy=no-user-gesture-required", "--headless=new",
                "--disable-features=DisableLoadExtensionCommandLineSwitch"])
            sw = ctx.service_workers[0] if ctx.service_workers else await ctx.wait_for_event("serviceworker", timeout=15000)
            print("extension:", sw.url)
            tab = await ctx.new_page()
            await tab.goto(f"http://127.0.0.1:{puerto}/pagina.html")
            await tab.bring_to_front()
            tab_id = await sw.evaluate("async () => (await chrome.tabs.query({active:true}))[0].id")
            if FILL:
                fuente = await sw.evaluate("() => fillTab.toString()")
                await tab.evaluate(f"({fuente})(true)")
            r = await sw.evaluate("""async (tabId) => { try { await start({tabId, resolution:'1080', fps:30, keepSound:false, fill:false, limit:%d}); return 'ok' } catch(e){ return String(e) } }""" % LIMITE, tab_id)
            print("start:", r)
            print("audio de la pagina:", await tab.evaluate("a.state"))
            if MUTE:
                await sw.evaluate("(id) => chrome.tabs.update(id, {muted:true})", tab_id)
                print("pestana silenciada")
            if not DELANTE:
                otra = await ctx.new_page()
                await otra.goto("about:blank")
                await otra.bring_to_front()
            print("pestaña detrás:", await tab.evaluate("document.visibilityState"))
            await asyncio.sleep(SEG)
            print("sigue:", await tab.evaluate("document.visibilityState"))
            if LIMITE:
                pass  # se para sola
            elif CERRAR:
                await tab.close()
            else:
                await sw.evaluate("() => chrome.runtime.sendMessage({target:'offscreen',cmd:'stop'})")
            for _ in range(40):
                await asyncio.sleep(0.5)
                d = await sw.evaluate("async () => (await chrome.downloads.search({})).map(x => [x.state, x.filename, x.mime, x.error ?? ''])")
                e = await sw.evaluate("async () => (await chrome.storage.session.get('lastError')).lastError ?? ''")
                if e or (d and d[0][0] == "complete"):
                    break
            print("descarga:", d, "error:", e)
            ruta = d[0][1] if d else ""
            if ruta and os.path.exists(ruta):
                info = subprocess.run([FF, "-hide_banner", "-i", ruta], capture_output=True, text=True).stderr
                print("\n".join(l for l in info.splitlines() if re.search("Duration|Stream", l)))
                vol = subprocess.run([FF, "-hide_banner", "-i", ruta, "-af", "volumedetect", "-vn", "-f", "null", "-"], capture_output=True, text=True).stderr
                print("\n".join(l for l in vol.splitlines() if "mean_volume" in l or "max_volume" in l))
                fr = subprocess.run([FF, "-hide_banner", "-i", ruta, "-vf", "fps=1,signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"], capture_output=True, text=True).stderr
                print("luma por segundo:", re.findall(r"YAVG=([0-9.]+)", fr))
                salida = os.path.join(RAIZ, "tests", "ultima.mp4")
                subprocess.run([FF, "-y", "-loglevel", "error", "-i", ruta, "-c", "copy", salida])
            estado = await sw.evaluate("async () => [await chrome.storage.session.get(null), await chrome.action.getBadgeText({}), await chrome.offscreen.hasDocument()]")
            print("al acabar:", estado)
            panel = await ctx.new_page()
            await panel.set_viewport_size({"width": 308, "height": 330})
            await panel.goto(f"chrome-extension://{ID}/popup.html")
            await asyncio.sleep(0.5)
            await panel.screenshot(path=os.path.join(RAIZ, "tests", "panel.png"))
            await ctx.close()

asyncio.run(main())
