# Pruebas de interfaz (FlaUI)

Recorridos de sOC ScreenShare con [FlaUI](https://github.com/FlaUI/FlaUI) (MIT) sobre UI Automation y
xUnit (General §8.7). Lanzan el **exe Debug ya compilado** en modo aislado y lo manejan como una
persona: arranque con los mandos y los atajos a la vista; elegir pantalla, todas o ventana (y que con
una ventana se ofrezca su sonido); **grabar 2 s de verdad, pausar, seguir, parar** y ver la última
grabación (el MP4 queda en la carpeta aislada); cambio de idioma; «Acerca de» y novedades; la guía que
sale la primera vez; crear una regla de grabación automática y que una regla sin aplicación se avise y
no se guarde; volver a los atajos de siempre.

## Cómo se lanzan

```powershell
dotnet build ScreenShare.slnx -c Debug -m:1 -nodeReuse:false
dotnet test tests\ScreenShare.UITests --no-build
```

Otro exe: variable `SCREENSHARE_EXE` (tiene que ser Debug: en Release no hay modo aislado).
Son 9 pruebas y tardan unos 28 s; van una detrás de otra, cada una con su instancia y su carpeta.

## Modo aislado (`SOC_SANDBOX`)

Solo en Debug. Con `SOC_SANDBOX=<carpeta temporal>` la aplicación guarda ajustes, registro y vídeos en
esa carpeta; no pone icono en la bandeja, no registra atajos globales, no vigila ventanas, no tiene
instancia única ni arranque con Windows, sus ventanas **no se activan al abrir** (no roban el foco) y
lleva `[SOC_SANDBOX]` en el título. La prueba de grabar captura la pantalla de verdad, sin sonido.

## Capturas

Cada paso deja un PNG (PrintWindow, sin traer la ventana al frente) en `tests\ScreenShare.UITests\artifacts\`
(fuera de git), con el nombre `<prueba>-<paso>.png`.

## Cómo se buscan los controles

Por `AutomationId` (el `x:Name` en WPF); los diálogos pequeños llevan `OkButton` y `CancelButton`.
Nunca por el texto, que cambia con el idioma. Todo va por patrones (Invoke, Value, SelectionItem): sin
ratón ni teclado.
