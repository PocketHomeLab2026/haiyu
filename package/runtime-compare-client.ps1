[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$From = '',
    [string]$To = '',
    [ValidateRange(1, 32)][int]$MaxPairs = 1,
    [ValidateRange(128, 1048576)][int]$SampleElements = 4096,
    [switch]$VerifyBaseline,
    [bool]$AllowBaselineFallback = $true,
    [string]$RequestId = '',
    [string]$OutputPath = '',
    [ValidateSet('none', 'directional-error', 'digest-mismatch')][string]$InjectFault = 'none'
)

$ErrorActionPreference = 'Stop'

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径越出允许根目录：$resolvedPath"
    }
}

$exe = Join-Path $InstallRoot 'app\haiyu-directional-compare.exe'
$dataRoot = Join-Path $InstallRoot 'data'
$mapsRoot = Join-Path $dataRoot 'maps'
$bindingPath = Join-Path $dataRoot 'customer-model-binding.json'
$readinessPath = Join-Path $dataRoot 'customer-deployment-readiness.json'
foreach ($required in @($exe, $bindingPath, $readinessPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "运行器官缺少文件：$required"
    }
}

$binding = Get-Content -LiteralPath $bindingPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$binding.status -ne 'bound' -or $null -eq $binding.selected) {
    throw '客户机尚未绑定可用模型，请先运行“一键发现适配并验收本机模型”。'
}

$mapPath = [IO.Path]::GetFullPath([string]$binding.selected.mapFile)
Assert-PathUnderRoot -Path $mapPath -Root $mapsRoot
if (-not (Test-Path -LiteralPath $mapPath -PathType Leaf)) {
    throw "绑定的因果位图不存在：$mapPath"
}

$route = $binding.selected.numericRoute
if ($null -eq $route -or [string]::IsNullOrWhiteSpace([string]$route.from) -or [string]::IsNullOrWhiteSpace([string]$route.to)) {
    $route = $binding.selected.structuralRoute
}
if ([string]::IsNullOrWhiteSpace($From)) { $From = [string]$route.from }
if ([string]::IsNullOrWhiteSpace($To)) { $To = [string]$route.to }
if ([string]::IsNullOrWhiteSpace($From) -or [string]::IsNullOrWhiteSpace($To)) {
    throw '绑定模型没有可用的定向路线。'
}

if ([string]::IsNullOrWhiteSpace($RequestId)) {
    $RequestId = [Guid]::NewGuid().ToString('N')
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeRequestId = $RequestId -replace '[^A-Za-z0-9_.-]', '_'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $OutputPath = Join-Path $dataRoot "runtime-receipts\$stamp-$safeRequestId.json"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputParent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputParent | Out-Null

$verifyValue = if ($VerifyBaseline) { 'true' } else { 'false' }
$fallbackValue = if ($AllowBaselineFallback) { 'true' } else { 'false' }
$raw = & $exe runtime-compare `
    --map $mapPath `
    --readiness $readinessPath `
    --from $From `
    --to $To `
    --max-pairs $MaxPairs `
    --sample-elements $SampleElements `
    --verify-baseline $verifyValue `
    --allow-baseline-fallback $fallbackValue `
    --request-id $RequestId `
    --inject-fault $InjectFault `
    --output $OutputPath
$code = $LASTEXITCODE
$raw
if ($code -ne 0) {
    throw "本地运行器官严格闭锁，退出码：$code；回执：$OutputPath"
}
