# Changelog — sOC ScreenShare

## 2026.10.12.0 — Primera versión · First version

**Español**

- Antes de publicarse se llamaba «ShareXCreen»; pasa a llamarse **sOC ScreenShare** (exe `sOCScreenShare.exe`, ajustes en `%AppData%\sOCScreenShare`).

- Graba **una pantalla, todas o una ventana** en un **MP4 listo para compartir** (H.264 y AAC): ligero
  por defecto (hasta 1080p, 30 imágenes/s, unos 28 MB por minuto) o en calidad alta (hasta 4K, 60
  imágenes/s). Usa el codificador de la tarjeta gráfica si lo hay.
- **Sonido a elegir**: en una pantalla, todo lo que suena por el dispositivo de salida que elijas o
  solo una aplicación; **al grabar una ventana, solo su sonido** (también el de sus procesos
  auxiliares, como en los navegadores), y si Windows no sabe separarlo se avisa y no se graba otro. El
  micrófono, aparte. Cada uno con su volumen.
- **Atajos desde cualquier sitio**: Ctrl+Mayús+F9 empieza y Ctrl+Mayús+F10 para (se pueden cambiar);
  se ven junto a los botones. Mientras graba, la ventana se aparta y el icono junto al reloj lleva un
  punto rojo; desde él se pausa o se para.
- **Grabación automática**: cuando se abre una ventana de la aplicación que elijas (y, si quieres, con
  un título concreto), la graba con su sonido y la guarda al cerrarse. Puede arrancar con Windows.
- **Calidad a medida**: además de «Óptima para compartir» y «Alta», una «Personalizada» con resolución
  máxima, imágenes por segundo, tasa de bits (automática o a mano), códec H.264 o H.265/HEVC (si el
  equipo lo tiene), control de tasa (VBR, CBR o calidad constante) y audio AAC (96-192 kbit/s, 44,1 o
  48 kHz, mono o estéreo), con los MB por minuto a la vista antes de grabar.
- Al terminar: reproducir, enseñar en su carpeta o **copiar el fichero** para pegarlo donde quieras.
- Carpeta y opciones recordadas, línea de órdenes (`--record`, `--auto`, `--probe`), español e
  inglés, tema claro y oscuro, guía de configuración y novedades.
- Por dentro: 153 pruebas automáticas (99,4 % de la lógica; con las de integración, 92,5 % de lógica
  y motor) y 10 de interfaz. **Toda la aplicación queda en ≈ 52 %** porque la interfaz WPF no se
  instrumenta: plan para llegar al 90 % → medir también las pruebas de interfaz con cobertura y sacar
  de `MainWindow` a clases propias lo que queda de lógica (estimación de tamaño, textos de la última
  grabación, construcción de la petición).
- Sin probar a mano todavía: la letra grande de Windows, el tema claro, un equipo sin captura por
  proceso (Windows 10 anterior a 20348) y la entrega desde el exe publicado con arranque con Windows.

**English**

- Records **one screen, all of them or a window** to an **MP4 that is ready to share** (H.264 and
  AAC): light by default (up to 1080p, 30 fps, about 28 MB per minute) or high quality (up to 4K, 60
  fps). It uses the graphics card encoder when there is one.
- **Choose the sound**: on a screen, everything playing on the output device you choose or only one
  app; **recording a window records only its own sound** (including its helper processes, as browsers
  have), and if Windows cannot separate it you are told and nothing else is recorded instead. The
  microphone on top. Each one with its own volume.
- **Shortcuts from anywhere**: Ctrl+Shift+F9 starts and Ctrl+Shift+F10 stops (you can change them);
  they are shown next to the buttons. While recording the window steps aside and the icon next to the
  clock gets a red dot; pause or stop from it.
- **Automatic recording**: when a window of the app you choose opens (and, if you want, with a given
  title), it is recorded with its sound and saved when it closes. It can start with Windows.
- **Quality your way**: besides «Best for sharing» and «High quality», a «Custom» one with maximum
  resolution, frames per second, bitrate (automatic or by hand), H.264 or H.265/HEVC codec (if the
  computer has it), bitrate control (VBR, CBR or constant quality) and AAC audio (96-192 kbit/s, 44.1
  or 48 kHz, mono or stereo), with the MB per minute shown before recording.
- When it ends: play it, show it in its folder or **copy the file** to paste it anywhere.
- Folder and options remembered, command line (`--record`, `--auto`, `--probe`), Spanish and English,
  light and dark theme, setup guide and what's new.
