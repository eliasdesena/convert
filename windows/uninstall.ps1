<#
.SYNOPSIS
  Removes Convert: the context-menu package, the installed files and (with -All) settings and the
  signing certificate.
#>
param([switch]$All)
$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\Convert"
Get-Process Convert -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force

$pkg = Get-AppxPackage -Name Convert -ErrorAction SilentlyContinue
if ($pkg) { Remove-AppxPackage $pkg.PackageFullName; Write-Host "Removed package $($pkg.PackageFullName)" }

for ($i = 0; $i -lt 10 -and (Test-Path $InstallDir); $i++) {
    try { Remove-Item $InstallDir -Recurse -Force } catch { Start-Sleep -Milliseconds 500 }
}
if (Test-Path $InstallDir) { Write-Warning "Couldn't delete $InstallDir completely (files in use); delete it after restarting Explorer." }
else { Write-Host "Removed $InstallDir" }

if ($All) {
    $data = Join-Path $env:LOCALAPPDATA "Convert"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force; Write-Host "Removed settings ($data)" }
    $certs = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=Convert Dev" }
    $certs | Remove-Item
    if ($certs) {
        $thumbs = ($certs | ForEach-Object { "'$($_.Thumbprint)'" }) -join ","
        Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList @("-NoProfile", "-Command",
            "Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { @($thumbs) -contains `$_.Thumbprint } | Remove-Item")
        Write-Host "Removed signing certificate"
    }
}
