[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]]$AdditionalRoot = @()
)

$ErrorActionPreference = 'Stop'
$adapt = Join-Path $PSScriptRoot 'adapt-client.ps1'
& $adapt -InstallRoot $InstallRoot -AdditionalRoot $AdditionalRoot
$bind = Join-Path $PSScriptRoot 'bind-model.ps1'
& $bind -InstallRoot $InstallRoot
