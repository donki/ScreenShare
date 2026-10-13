<#
.SYNOPSIS
    Entrega de sOC ScreenShare: versión, banco de pruebas, exe autocontenido y, sin -SoloCompilar,
    OneDrive, commit, push y release de GitHub (como sOC WSManager y RC Manager).
.DESCRIPTION
    El CHANGELOG y src\ScreenShare.Core\whatsnew.json se escriben antes a mano. -SoloCompilar deja
    solo el exe en publish\app, sin OneDrive ni git.
.EXAMPLE
    .\tools\entregar.ps1 -Version 2026.10.12.0 -SoloCompilar
.EXAMPLE
    .\tools\entregar.ps1 -Version 2026.10.13.0 -Mensaje "2026.10.13.0: ..."
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Mensaje = '',
    [switch] $SoloCompilar
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
[IO.Directory]::SetCurrentDirectory($raiz)

# 1. Versión (una sola, en Directory.Build.props) y novedades.
$p = 'Directory.Build.props'; $x = Get-Content $p -Raw
foreach ($t in 'Version', 'AssemblyVersion', 'FileVersion') { $x = [regex]::Replace($x, "<$t>[^<]*</$t>", "<$t>$Version</$t>") }
[IO.File]::WriteAllText((Resolve-Path $p), $x)
$primera = (Get-Content src\ScreenShare.Core\whatsnew.json -Raw | ConvertFrom-Json)[0].version
if ([version]$primera -ne [version]$Version) {
    throw "whatsnew.json empieza por $primera y no por ${Version}: escribe antes las novedades (General 6.7)."
}

# 2. Banco de pruebas en verde (General 8.6).
dotnet build ScreenShare.slnx -c Debug -nologo -v q -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'No compila.' }
dotnet test tests\ScreenShare.Tests --no-build -nologo
if ($LASTEXITCODE -ne 0) { throw 'El banco de pruebas no pasa.' }

# 3. Exe autocontenido de un solo fichero.
Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue
dotnet publish src\ScreenShare.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -nologo -v q -m:1 -nodeReuse:false -o publish\app
if ($LASTEXITCODE -ne 0) { throw 'No se publica la aplicación.' }
$exe = 'publish\app\sOCScreenShare.exe'
$v = & $exe --version   # arranca y responde por consola
if ($LASTEXITCODE -ne 0) { throw 'El exe publicado no arranca.' }
"Exe: $(Resolve-Path $exe) ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
if ($SoloCompilar) { return }

# 4. OneDrive (General 8): el exe y el LEEME.
$d = 'C:\ID\OneDrive\ScreenShare'
New-Item -ItemType Directory -Force $d | Out-Null
$abierta = Get-Process sOCScreenShare -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$d*" }
$abierta | Stop-Process -Force
Start-Sleep -Milliseconds 500
Copy-Item $exe $d -Force
$leeme = @"
sOC ScreenShare
===============

Versión $Version

Graba una pantalla, todas o una ventana con su sonido en un MP4 listo para compartir. Ctrl+Mayús+F9
empieza y Ctrl+Mayús+F10 para. Puede grabar sola cuando se abre la ventana de una aplicación.

- sOCScreenShare.exe: la aplicación. Ábrela con doble clic; no hay que instalar nada.

Windows puede avisar de que el editor es desconocido ("Windows protegió su PC"): pulsa
"Más información" y "Ejecutar de todas formas".

Software libre bajo licencia MIT. En español y en inglés, claro y oscuro.
"@
[IO.File]::WriteAllText("$d\LEEME.txt", $leeme, (New-Object Text.UTF8Encoding $false))

# 5. Git y release (General 8). Sin Co-Authored-By (General 3.5).
if (-not $Mensaje) { throw 'Falta -Mensaje para el commit.' }
git add -A
git commit -q -m $Mensaje
git push -q origin HEAD 2>&1 | Select-Object -Last 1
python 'D:\sOCProjects\Mobile\Shared\release-github.py' "v$Version" $exe 2>&1 | Select-Object -Last 1

# 6. La entrega vuelve a dejar la aplicación abierta si lo estaba (General 8.3).
if ($abierta) { Start-Process "$d\sOCScreenShare.exe" -ArgumentList '--tray' }
