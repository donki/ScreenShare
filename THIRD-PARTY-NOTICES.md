# Avisos de terceros — sOC ScreenShare

| Componente | Uso | Licencia | Titular |
|---|---|---|---|
| .NET 10 (runtime, WPF, WinRT/CsWinRT) | Plataforma de la aplicación. Va dentro del exe autocontenido. | MIT | .NET Foundation y colaboradores |
| NAudio.Wasapi / NAudio.Core 3.1.0 | Captura de audio WASAPI: salida de un dispositivo (loopback), una aplicación (loopback por proceso) y el micrófono. | MIT | Mark Heath y colaboradores |
| Vortice.Direct3D11, Vortice.DXGI, Vortice.MediaFoundation, Vortice.DirectX, Vortice.Mathematics 3.8.3 / 2.1.0 | Envoltorios de Direct3D 11 (lienzo y procesador de vídeo) y de Media Foundation (MP4 H.264 + AAC). | MIT | Amer Koleci y colaboradores |
| SharpGen.Runtime, SharpGen.Runtime.COM 2.4.2-beta | Base de los envoltorios COM de Vortice. | MIT | SharpGenTools y colaboradores |
| API de Windows (Windows.Graphics.Capture, Direct3D 11, Media Foundation, WASAPI, user32, dwmapi, shell32, winmm) | Captura, codificación H.264/AAC (codificadores del propio Windows o de la tarjeta gráfica), audio, ventanas, atajos y área de notificación. | API del sistema operativo | Microsoft |
| Segoe Fluent Icons / Segoe MDL2 Assets | Iconos planos de los botones. Fuentes del propio Windows; no se distribuyen. | Fuente del sistema operativo | Microsoft |

Solo para las pruebas (no van en el exe): xUnit (Apache 2.0), Microsoft.NET.Test.Sdk (MIT), coverlet
(MIT), ReportGenerator (Apache 2.0), FlaUI (MIT); el programa de prueba `sOCScreenShareToneApp` es propio.

No incluye ni deriva código de otros programas de captura con licencia GPL ni de FFmpeg. Todo el código (`*.cs`, `*.xaml`, scripts)
es propio y va bajo MIT (`LICENSE`).
