# Descripción de Store — Español (España)

Todo lo de aquí es para pegar tal cual en el formulario de Partner Center. Paquete:
`tools\empaquetar-msix.ps1` → `bin\sOCScreenShare.msix` (identidad de *Product management › Product identity*).
En el MSIX no se ofrece «Arrancar con Windows»: la clave Run quedaría en el registro privado del paquete.

## Nombre del producto

```
sOC ScreenShare
```

## Descripción

```
sOC ScreenShare graba una pantalla, todas o una ventana con su sonido en un MP4 listo para compartir (H.264 + AAC, ligero por defecto), con un par de clics o con un atajo de teclado desde cualquier sitio. Y puede grabar sola cuando se abre la ventana de una aplicación concreta.

QUÉ GRABAR
• Una pantalla (elegida en un dibujo de tus pantallas), todas juntas o una ventana.

SONIDO
• Todo lo que suena por un dispositivo de salida, o solo el de una aplicación.
• Al grabar una ventana, solo su sonido: lo demás que suene en el PC no entra.
• Micrófono aparte, si lo activas, con volumen de cada origen y medidores de nivel.

CALIDAD
• «Óptima para compartir» (hasta 1080p, unos 30 MB por minuto) o «Alta» (hasta 4K y 60 imágenes/s); o a medida: resolución, imágenes por segundo, tasa de bits, H.264 o H.265/HEVC y audio.

GRABACIÓN AUTOMÁTICA
• Reglas por aplicación (y por título de ventana, si quieres): al abrirse la ventana se graba con su sonido y al cerrarse se guarda.

MANDOS
• Grabar, pausar y parar con botones o con atajos globales (Ctrl+Mayús+F9 / F10), también desde el icono junto al reloj.
• Al terminar: reproducir, enseñar en su carpeta o copiar el fichero para pegarlo en un chat o un correo.

No se conecta a internet ni sube nada. Sin cuenta, sin anuncios, sin rastreadores. Software libre bajo licencia MIT. En español y en inglés, con modo claro y oscuro.
```

## Novedades de esta versión

En blanco en el primer envío.

## Características del producto

```
Graba una pantalla, todas o una ventana
Solo el sonido de la ventana que grabas
MP4 ligero listo para compartir
Grabación automática al abrirse una aplicación
Atajos de teclado globales
Sin internet, sin cuenta, sin anuncios
```

## Palabras clave

```
grabar pantalla, grabador, captura de vídeo, mp4, grabar ventana, screencast, audio
```

## Categoría

Foto y vídeo (o Productividad).

## Capturas

`capturas/01.png`–`04.png` (1600×900), con `python tools\capturas-store.py store\capturas\<png>:0 …`.
Nunca la de la lista de ventanas (enseña títulos reales).

## Logotipos

`logos/`, con `tools\generar-icono.ps1`.

## Dependencias de software (política 10.2.4.1)

Ninguna: la captura y el vídeo usan Windows (Windows.Graphics.Capture y Media Foundation); el sonido
por aplicación necesita Windows compilación 20348 o posterior (en las anteriores se avisa).

## Privacidad

URL: https://socraticweb0.wordpress.com/privacidad/ — sOC ScreenShare no recoge ningún dato; el
micrófono solo se usa si se activa y las grabaciones se quedan en el PC.
