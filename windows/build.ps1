<#
.SYNOPSIS
  Builds Convert for Windows and packages it as a signed sparse MSIX.

.DESCRIPTION
    .\build.ps1             build into out\app and create out\Convert.msix
    .\build.ps1 -Install    also install to %LOCALAPPDATA%\Programs\Convert and register the
                            context menu ("Convert..." in Explorer's right-click menu)

  The first -Install creates a self-signed certificate "CN=Convert Dev" and asks once (UAC) to trust it
  on this PC, because Windows only registers signed packages.
#>
param(
    [switch]$Install,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Publisher = "CN=Convert Dev"
$PackageName = "Convert"
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\Convert"
$Out = Join-Path $PSScriptRoot "out"
$AppOut = Join-Path $Out "app"
$PkgDir = Join-Path $Out "pkg"
$Msix = Join-Path $Out "Convert.msix"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# --- tools -------------------------------------------------------------------------------------------

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw ".NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10" }

# makeappx and signtool come from the Microsoft.Windows.SDK.BuildTools NuGet package, so the full
# Windows SDK isn't needed.
function Get-SdkTool($name) {
    $root = Join-Path $PSScriptRoot "tools\sdk"
    $found = Get-ChildItem $root -Recurse -Filter $name -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
    if ($found) { return $found.FullName }
    Step "Downloading Windows SDK build tools (makeappx, signtool)"
    $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json"
    $version = $index.versions | Where-Object { $_ -notmatch "-" } | Select-Object -Last 1
    $nupkg = Join-Path $env:TEMP "sdk-buildtools-$version.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$version/microsoft.windows.sdk.buildtools.$version.nupkg" -OutFile $nupkg
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    Expand-Archive $nupkg $root
    Remove-Item $nupkg
    $found = Get-ChildItem $root -Recurse -Filter $name | Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
    if (-not $found) { throw "$name not found in Microsoft.Windows.SDK.BuildTools $version" }
    return $found.FullName
}

# --- build -------------------------------------------------------------------------------------------

Step "Building ($Configuration)"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
& $dotnet publish src\Convert\Convert.csproj -c $Configuration -o $AppOut --nologo -v quiet
if ($LASTEXITCODE) { throw "Building Convert failed" }
& $dotnet publish src\Convert.Shell\Convert.Shell.csproj -c $Configuration -o $AppOut --nologo -v quiet
if ($LASTEXITCODE) { throw "Building Convert.Shell failed" }
Copy-Item packaging\Assets (Join-Path $AppOut "Assets") -Recurse

# A bundled ffmpeg (windows\tools\ffmpeg\ffmpeg.exe + ffprobe.exe) is shipped in tools\ next to Convert.exe.
$bundled = Join-Path $PSScriptRoot "tools\ffmpeg"
if (Test-Path (Join-Path $bundled "ffmpeg.exe")) {
    Step "Bundling ffmpeg"
    New-Item -ItemType Directory (Join-Path $AppOut "tools") -Force | Out-Null
    Copy-Item (Join-Path $bundled "*.exe") (Join-Path $AppOut "tools")
}

# --- package manifest --------------------------------------------------------------------------------

Step "Writing package manifest"
$exe = Join-Path $AppOut "Convert.exe"
$extensions = & $exe --list-extensions | ForEach-Object { $_.Trim() } | Where-Object { $_ }
if (-not $extensions) { throw "Convert.exe --list-extensions returned nothing" }
$itemTypes = ($extensions | ForEach-Object {
    "            <desktop5:ItemType Type=`".$_`"><desktop5:Verb Id=`"Convert`" Clsid=`"5E2B8C34-9A71-4D6F-B0E3-2C8F41A7D965`" /></desktop5:ItemType>"
}) -join "`r`n"
$version = (& $dotnet msbuild src\Convert\Convert.csproj -getProperty:Version -nologo).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { $version = "0.1.0" }
$manifest = (Get-Content packaging\AppxManifest.template.xml -Raw) `
    -replace '\{\{VERSION\}\}', "$version.0" `
    -replace '\{\{PUBLISHER\}\}', $Publisher `
    -replace '\{\{ITEMTYPES\}\}', $itemTypes
New-Item -ItemType Directory $PkgDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $PkgDir "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))
Copy-Item packaging\Assets (Join-Path $PkgDir "Assets") -Recurse
Write-Host "    $($extensions.Count) file types"

Step "Packing $Msix"
$makeappx = Get-SdkTool "makeappx.exe"
& $makeappx pack /o /nv /d $PkgDir /p $Msix | Out-Null
if ($LASTEXITCODE) { throw "makeappx failed" }

# --- signing -----------------------------------------------------------------------------------------

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    Step "Creating signing certificate $Publisher"
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
        -FriendlyName "Convert local package signing" -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(5)
}

Step "Signing"
$signtool = Get-SdkTool "signtool.exe"
& $signtool sign /q /fd SHA256 /sha1 $cert.Thumbprint /s My $Msix
if ($LASTEXITCODE) { throw "signtool failed" }
Write-Host "Built $Msix"

if (-not $Install) { return }

# --- install -----------------------------------------------------------------------------------------

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $trusted) {
    Step "Trusting the certificate on this PC (Windows will ask for permission)"
    $cer = Join-Path $Out "Convert.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        "-NoProfile", "-Command", "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null")
    Remove-Item $cer
    if ($p.ExitCode) { throw "Couldn't trust the certificate (exit code $($p.ExitCode))" }
}

Step "Installing to $InstallDir"
Get-Process Convert -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force
$old = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($old) { Remove-AppxPackage $old.PackageFullName }
# dllhost may still hold the old context-menu DLL for a moment after the package is removed.
for ($i = 0; $i -lt 10; $i++) {
    try {
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
        break
    } catch { Start-Sleep -Milliseconds 500 }
}
if (Test-Path $InstallDir) { throw "Couldn't replace $InstallDir (files in use). Close Convert and Explorer windows and try again." }
Copy-Item $AppOut $InstallDir -Recurse

Step "Registering the context menu"
Add-AppxPackage -Path $Msix -ExternalLocation $InstallDir
$pkg = Get-AppxPackage -Name $PackageName
Write-Host ""
Write-Host "Installed Convert $($pkg.Version). Right-click a file in Explorer -> Convert..." -ForegroundColor Green
