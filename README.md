# sOC ScreenShare (sOCScreenShare)

Graba **una pantalla, todas o una ventana** con su sonido en un **MP4 listo para compartir**
(H.264 + AAC, ligero por defecto), con un par de clics o con un atajo de teclado desde cualquier
sitio. Puede **grabar sola** cuando se abre la ventana de una aplicación concreta.

## Dónde conseguirla

- **GitHub**: el exe autocontenido de cada versión en
  [Releases](https://github.com/donki/ScreenShare/releases) (sin instalar: doble clic).
- **Microsoft Store**: aún no.

## Qué hace

- **Qué grabar**: una pantalla (selector visual con las pantallas dibujadas en su sitio), todas
  juntas tal como están colocadas, o una ventana (lista de ventanas abiertas). Si la ventana cambia de
  tamaño, el vídeo mantiene el suyo y la imagen se ajusta sin deformarse.
- **Sonido**:
  - Pantalla: ninguno, **todo lo que suena por un dispositivo de salida** (el predeterminado o el que
    se elija: altavoces, auriculares, una pantalla con altavoces…) o **solo una aplicación**.
  - **Ventana: solo su sonido**, el de su proceso y sus hijos (los navegadores suenan desde procesos
    auxiliares), con la captura por proceso de Windows (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`,
    `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`). Si el Windows no la tiene (antes de la
    compilación 20348), la ventana lo dice y **no se graba otro sonido en su lugar**.
  - **Micrófono** aparte, solo si se activa, con el dispositivo a elegir.
  - Volumen de cada origen (también en marcha) y medidores de nivel mientras graba.
- **Calidad**: dos atajos, «Óptima para compartir» (por defecto: hasta 1080p, 30 imágenes/s, H.264
  VBR a unos 4 Mbit/s a 1080p, AAC 128 kbit/s estéreo 48 kHz: unos 30 MB por minuto) y «Alta» (hasta 4K,
  60 imágenes/s, AAC 192 kbit/s), que rellenan las opciones de un diálogo «Calidad» aparte (no rehace la
  ventana); tocar cualquiera la deja en **«Personalizada»**:
  - Vídeo: resolución máxima (original, 2160p, 1440p, 1080p, 720p, 480p; nunca agranda), imágenes por
    segundo (15, 24, 30, 60), tasa de bits automática (≈ 0,065 bits por píxel en H.264, 0,04 en HEVC,
    un 25 % menos por encima de 30 imágenes/s) o en Mbit/s a mano, códec **H.264** o **H.265/HEVC** (si
    el equipo tiene codificador, que se mira con `MFTEnumEx`; si no, la opción sale apagada con su
    explicación) y control de tasa **VBR, CBR o calidad constante** (si el codificador no lo admite,
    graba con el suyo y se avisa).
  - Audio AAC: 96, 128, 160 o 192 kbit/s (256 y 320 salen apagados: el codificador AAC de Windows no los
    admite), 44,1 o 48 kHz, mono o estéreo.
  - La ventana y el diálogo enseñan antes de grabar el tamaño, el códec y los MB por minuto. Lo
    guardado que no valga se corrige al cargar. Codificador de la tarjeta gráfica si lo hay.
- **Mandos**: grabar, pausar/seguir y parar con botones de icono, contador de tiempo; **atajos
  globales** configurables (por defecto **Ctrl+Mayús+F9** empieza y **Ctrl+Mayús+F10** para), que
  funcionan con la ventana escondida o minimizada y se ven junto a los botones. Mientras graba, la
  ventana se aparta (opcional) y el **icono junto al reloj** lleva un punto rojo; desde él se pausa o
  se para. Las ventanas de la aplicación no salen en la grabación.
- **Al terminar**: tarjeta con la última grabación (tamaño, duración) y botones para reproducirla,
  enseñarla en su carpeta o **copiar el fichero** (se pega tal cual en una conversación o un correo).
- **Grabación automática**: reglas con la aplicación (de las abiertas o eligiendo su `.exe`) y, si se
  quiere, un filtro por título (contiene o expresión regular) para una pantalla concreta de esa app;
  interruptor general y uno por regla. Al abrirse una ventana que cumple una regla se graba esa
  ventana con su sonido y la carpeta y calidad de los ajustes; al cerrarse (o esconderse) se para y se
  guarda (opción por regla). Nunca empieza una segunda grabación si ya hay una. Aviso discreto junto
  al reloj al empezar y al acabar. La detección usa `SetWinEventHook` (sin sondeo) en un hilo propio.
  Con la grabación automática encendida, cerrar la ventana la deja junto al reloj; se puede
  **arrancar con Windows** (`HKCU\…\Run`, solo desde la versión publicada, nunca un Debug).
- Carpeta de destino elegible y recordada (por defecto `Vídeos\ScreenShare`), nombre con fecha y hora
  (`Grabación 2026-10-12 15.30.05.mp4`), ajustes en JSON, español e inglés, tema claro y oscuro
  siguiendo a Windows, guía de configuración, novedades y «Acerca de».

## Línea de órdenes

```
sOCScreenShare.exe --record <segundos> [--out f.mp4] [--target monitor|all|window] [--monitor n] [--window título]
                   [--quality share|high] [--system-audio | --no-system-audio | --app programa | --no-audio] [--mic | --no-mic]
                   [--resolution original|2160|1440|1080|720|480] [--fps 15|24|30|60] [--mbps n] [--codec h264|hevc]
                   [--rate cbr|vbr|quality] [--quality-level 0-100] [--audio-kbps 96|128|160|192] [--audio-rate 44100|48000]
                   [--channels mono|stereo]          (cualquiera de estas deja la calidad en personalizada)
sOCScreenShare.exe --auto <programa> [--title texto] [--regex] [--timeout s] [--out f.mp4]
sOCScreenShare.exe --probe <fichero.mp4>        comprueba un MP4 (códec, tamaño, imágenes/s, kbit/s medidos, audio)
sOCScreenShare.exe --tray                       arranca junto al reloj, sin la ventana
```

Lo que no se indique sale de los ajustes guardados (sin cambiarlos). Tras grabar comprueba el MP4.
Códigos de salida: 0 bien, 1 error, 2 orden no válida.

## Cómo está hecho

| Proyecto | Qué hay |
|---|---|
| `src/ScreenShare.Core` | Lógica pura (lo que mide el banco): ajustes JSON, nombres de fichero, calidad (resolución y bitrate), reloj con pausas y estados de la grabación, conversión/remuestreo/mezcla de audio en línea de tiempo, NV12 de reserva, filtro de ventanas y disposición de pantallas, reglas de grabación automática, atajos, línea de órdenes, textos es/en. |
| `src/ScreenShare.Engine` | El motor: `Windows.Graphics.Capture` (`Direct3D11CaptureFramePool` libre de hilos) copia cada pantalla o la ventana a un lienzo Direct3D 11; un hilo a ritmo fijo lo pasa por el **procesador de vídeo de la GPU** (escala con bandas + BGRA→NV12 BT.709) y lo da a **Media Foundation** (`IMFSinkWriter`, H.264 High o HEVC Main + AAC, hardware primero, control de tasa por `ICodecAPI`); el audio llega por **WASAPI** (NAudio) y se coloca en la línea de tiempo por su hora de captura (QPC), así que la salida del sistema puede callar sin desfasar nada. `WindowWatcher` (SetWinEventHook en hilo propio) y `Mp4Probe` (Source Reader). |
| `src/ScreenShare.App` | WPF: ventana, reglas, guía, novedades, «Acerca de», bandeja, atajos (`RegisterHotKey`), instancia única, línea de órdenes. |

Decisiones:
- **Sin «índice al principio» del MP4** (`MF_MPEG4SINK_MOOV_BEFORE_MDAT`): con él, Media Foundation
  dejaba la pista AAC sin poderse decodificar (comprobado con `--probe`). El MP4 se reproduce igual
  en todas partes; solo no empieza a verse antes de terminar de descargarse.
- **Sin MSIX de momento** (la versión de tienda queda para cuando se decida el nombre).
- Nada de programas de captura GPL ni de FFmpeg: solo APIs de Windows y bibliotecas MIT (ver `THIRD-PARTY-NOTICES.md`).

## Dónde guarda las cosas, a qué accede y qué puede romper

- **Vídeos**: en la carpeta elegida. Nada sale del equipo.
- **Ajustes**: `%AppData%\sOCScreenShare\settings.json`. **Registro de errores**: `%LocalAppData%\sOCScreenShare\errors.log`.
- **Arranque con Windows** (si se activa): `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\sOCScreenShare`.
- Accede a la imagen de las pantallas y ventanas, al sonido de los dispositivos de salida, de la
  aplicación elegida y del micrófono **solo mientras graba**. La grabación automática observa qué
  ventanas se abren (nombre del programa y título) para compararlas con las reglas; no guarda nada de eso.
- **Qué puede romper**: nada del sistema. Lo peor: llenar el disco con grabaciones largas en calidad
  alta (se para y guarda lo grabado si el disco se llena).

## Compilar y entregar

```
dotnet build ScreenShare.slnx -c Debug -m:1 -nodeReuse:false
.\tools\entregar.ps1 -Version 2026.10.13.0 -SoloCompilar      # banco de pruebas + exe autocontenido en publish\app
pwsh -File tools\generar-icono.ps1                             # vuelve a dibujar el icono desde assets\appicon.svg
```

Necesita el SDK de .NET 10 y Windows 10 2004 o posterior (Windows 11 o 10 compilación 20348 para el
sonido de una sola aplicación).

## Pruebas

`tests/ScreenShare.Tests` (xUnit): **153 pruebas** (144 de lógica + 9 de integración que solo corren
con `SOC_SCS_INTEGRATION=1`: graban de verdad la pantalla unos segundos en una carpeta temporal y
emiten tonos muy bajos con el programa de prueba propio `sOCScreenShareToneApp`). Las de integración
comprueban que una ventana se graba **con el tono de su proceso y sin el de otro proceso que suena a la
vez**, que una regla automática empieza y para sola, y que una calidad personalizada (720p, 24
imágenes/s, HEVC, AAC 128 kbit/s mono a 44,1 kHz) sale con esos parámetros exactos. Fecha: 2026-10-12.

- Cobertura de la lógica (`ScreenShare.Core`): **99,4 %**.
- Cobertura de lo instrumentado (Core + Engine, con las de integración): **92,5 %** (2657 de 2872 líneas).
- Cobertura de **toda la aplicación** (sumando la interfaz WPF, que no se instrumenta): **≈ 52 %**
  (2657 de ≈ 5100 líneas cubribles). Por debajo del 90 % de la constitución: la interfaz se cubre con
  las pruebas E2E de abajo, pero no se mide; ver el plan en el CHANGELOG.
- Tiempo del banco: **≈ 0,2 s** solo lógica; **≈ 22 s** con integración.

`tests/ScreenShare.UITests` (FlaUI, modo aislado `SOC_SANDBOX`): **10 pruebas** de interfaz en unos
**30 s**, entre ellas una grabación real de 2 s de punta a punta (ver su [README](tests/ScreenShare.UITests/README.md)).

```
dotnet test tests\ScreenShare.Tests
$env:SOC_SCS_INTEGRATION=1; dotnet test tests\ScreenShare.Tests --collect:"XPlat Code Coverage"
dotnet tool restore
dotnet tool run reportgenerator -reports:tests\ScreenShare.Tests\TestResults\*\coverage.cobertura.xml -targetdir:cobertura -reporttypes:TextSummary
dotnet test tests\ScreenShare.UITests
```

## Licencia

MIT (ver `LICENSE`). Terceros en `THIRD-PARTY-NOTICES.md`. Privacidad en `PRIVACY.md`.
