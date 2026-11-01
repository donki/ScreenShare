<#
.SYNOPSIS
    Construye el paquete MSIX de sOC ScreenShare para la Microsoft Store.

.DESCRIPTION
    Publica el ejecutable autocontenido, monta la carpeta del paquete con el manifiesto y los
    iconos, y llama a MakeAppx.

    Los valores de identidad los asigna Partner Center al reservar el nombre y vienen ya puestos
    por defecto. No son secretos: viajan dentro del paquete y estan publicados en la ficha.

    Para PROBARLO en este equipo hace falta firmarlo, porque Windows no instala un MSIX sin firma:
    con -Autofirmar se genera un certificado temporal, se firma y se deja el .cer al lado para
    poder confiar en el. Ese paquete sirve para probar y NO para la Store: la Store lo firma ella.

.EXAMPLE
    # Para probar aqui:
    .\tools\empaquetar-msix.ps1 -Autofirmar

.EXAMPLE
    # Para subir: los valores por defecto ya son los de la cuenta de Partner Center.
    .\tools\empaquetar-msix.ps1

.EXAMPLE
    # Si el nombre reservado en Partner Center fuera otro:
    .\tools\empaquetar-msix.ps1 -DisplayName "El nombre reservado"

#>
[CmdletBinding()]
param(
    # Valores de Partner Center (Product management > Product identity). El Publisher es el de la
    # cuenta sOCratic (comun a todas las apps); el nombre de identidad y el DisplayName hay que
    # confirmarlos al reservar el nombre en Partner Center (2026-11-01).
    [string] $IdentityName = "sOCratic.sOCScreenShare",
    [string] $Publisher = "CN=2FC3763A-58D5-473A-840E-D47726B23FE3",
    [string] $PublisherDisplayName = "sOCratic",

    # Tiene que ser uno de los nombres RESERVADOS de la aplicacion en Partner Center, no el que nos
    # guste: la Store lo comprueba contra su lista y rechaza el envio si no esta.
    [string] $DisplayName = "sOC ScreenShare",
    [string] $Version,
    [switch] $Autofirmar
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz "src\ScreenShare.App\ScreenShare.App.csproj"
$origenPaquete = Join-Path $raiz "Package"
$trabajo = Join-Path $raiz "bin\msix"
$salida = Join-Path $raiz "bin\sOCScreenShare.msix"

# --- La version sale de Directory.Build.props si no se pasa. MSIX exige cuatro numeros y el ultimo debe ser 0.
if (-not $Version) {
    $csproj = Get-Content (Join-Path $raiz "Directory.Build.props") -Raw
    if ($csproj -notmatch "<Version>([\d\.]+)</Version>") { throw "No se encuentra <Version> en Directory.Build.props." }
    $partes = $Matches[1].Split(".")
    while ($partes.Count -lt 4) { $partes += "0" }

    # La Store reserva el cuarto numero: tiene que ser 0. Se corre el nuestro a la tercera posicion
    # para no perderlo (2026.9.2.4 -> 2026.9.24.0).
    $Version = "{0}.{1}.{2}{3}.0" -f $partes[0], $partes[1], $partes[2], $partes[3]
}
Write-Host "Version del paquete: $Version"

$publicado = Join-Path $raiz "bin\msix-publish"

# --- Publicar el ejecutable autocontenido, igual que la entrega de siempre.
Write-Host "Publicando..."
dotnet publish $proyecto -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publicado -v q --nologo -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "Ha fallado el publish." }


# --- Montar la carpeta del paquete
if (Test-Path $trabajo) { Remove-Item $trabajo -Recurse -Force }
New-Item -ItemType Directory -Path $trabajo | Out-Null

Copy-Item (Join-Path $publicado "sOCScreenShare.exe") $trabajo
Copy-Item (Join-Path $origenPaquete "Images") $trabajo -Recurse

$manifiesto = Get-Content (Join-Path $origenPaquete "AppxManifest.xml") -Raw
$manifiesto = $manifiesto.Replace("@@IDENTITY_NAME@@", $IdentityName).
                          Replace("@@PUBLISHER@@", $Publisher).
                          Replace("@@PUBLISHER_DISPLAY_NAME@@", $PublisherDisplayName).
                          Replace("@@DISPLAY_NAME@@", $DisplayName).
                          Replace("@@VERSION@@", $Version)
Set-Content (Join-Path $trabajo "AppxManifest.xml") $manifiesto -Encoding UTF8

# --- MakeAppx: se coge el SDK mas nuevo que haya instalado.
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" |
            Sort-Object FullName | Select-Object -Last 1
if (-not $makeappx) { throw "No hay MakeAppx: falta el SDK de Windows." }

if (Test-Path $salida) { Remove-Item $salida -Force }
& $makeappx.FullName pack /d $trabajo /p $salida /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "MakeAppx ha fallado." }

Write-Host "Paquete: $salida"

# --- Firma solo para probar en este equipo
if ($Autofirmar) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
                Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw "No hay SignTool: falta el SDK de Windows." }

    # El sujeto del certificado tiene que ser IDENTICO al Publisher del manifiesto o la firma no vale.
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName "Remote Connections Manager (solo pruebas)" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

    $clave = ConvertTo-SecureString -String "pruebas" -Force -AsPlainText
    $pfx = Join-Path $raiz "bin\pruebas.pfx"
    Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfx -Password $clave | Out-Null
    Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" `
        -FilePath (Join-Path $raiz "bin\pruebas.cer") | Out-Null

    & $signtool.FullName sign /fd SHA256 /a /f $pfx /p "pruebas" $salida | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SignTool ha fallado." }

    Write-Host "Firmado para pruebas. Para instalarlo aqui hay que confiar antes en bin\pruebas.cer"
    Write-Host "(Equipo local > Entidades de confianza raiz). ESTE paquete no sirve para la Store."
}
