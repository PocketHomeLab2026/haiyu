[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Haiyu\DirectionalCompare'),
    [switch]$KeepMaps
)

$ErrorActionPreference = 'Stop'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) '海域定向对比器'
if (Test-Path -LiteralPath $startMenu) {
    Remove-Item -LiteralPath $startMenu -Recurse -Force
}

if (-not (Test-Path -LiteralPath $InstallRoot)) {
    Write-Host '未找到已安装目录。'
    return
}

if ($KeepMaps) {
    $data = Join-Path $InstallRoot 'data'
    $backup = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('Haiyu-Directional-Maps-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    if (Test-Path -LiteralPath $data) {
        Copy-Item -LiteralPath $data -Destination $backup -Recurse -Force
        Write-Host "因果位图已备份到：$backup"
    }
}

$resolved = [IO.Path]::GetFullPath($InstallRoot)
$localRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
if (-not $resolved.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝删除非本地应用目录：$resolved"
}
Remove-Item -LiteralPath $resolved -Recurse -Force
Write-Host '卸载完成。原始模型权重未被修改。'
