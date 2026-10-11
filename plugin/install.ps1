#requires -Version 7.6
param([Parameter(Mandatory)][string]$FanControlDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process FanControl -ErrorAction SilentlyContinue) { throw 'Exit FanControl before installing or updating the plugin.' }
if (-not (Test-Path -LiteralPath (Join-Path $FanControlDirectory 'FanControl.Plugins.dll'))) { throw 'Invalid FanControl directory.' }
$dll = Join-Path $PSScriptRoot 'dist\FanControl.MechrevoPtl.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Run build.ps1 first.' }
$destination = Join-Path $FanControlDirectory 'Plugins\MechrevoPtl'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $dll, (Join-Path $PSScriptRoot 'README.md') -Destination $destination -Force
$installed = Join-Path $destination 'FanControl.MechrevoPtl.dll'
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw 'Installed file verification failed.' }
Write-Output "Installed: $installed"
