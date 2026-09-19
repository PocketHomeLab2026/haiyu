[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Haiyu\DirectionalCompare'),
    [switch]$SkipInitialScan,
    [switch]$NoShortcuts
)

$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$manifestPath = Join-Path $sourceRoot 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "安装包清单不存在：$manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $source = Join-Path $sourceRoot $entry.path
    if (-not (Test-Path -LiteralPath $source)) {
        throw "安装包缺少文件：$($entry.path)"
    }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.sha256) {
        throw "安装包校验失败：$($entry.path)"
    }
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
$appRoot = Join-Path $InstallRoot 'app'
$dataRoot = Join-Path $InstallRoot 'data'
New-Item -ItemType Directory -Force -Path $appRoot,$dataRoot | Out-Null

$copyNames = @(
    'haiyu-directional-compare.exe',
    'prepare-client.ps1',
    'adapt-client.ps1',
    'bind-model.ps1',
    'scan-default.ps1',
    'compare-interactive.ps1',
    'benchmark-client.ps1',
    'ab-benchmark-client.ps1',
    'robustness-audit-client.ps1',
    'runtime-compare-client.ps1',
    'uninstall.ps1',
    'README-zh-CN.md',
    'package-manifest.json'
)
foreach ($name in $copyNames) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $appRoot $name) -Force
}

$shortcutPaths = @()
if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) '海域定向对比器'
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    $prepareShortcut = Join-Path $startMenu '一键发现适配并验收本机模型.lnk'
    $prepare = $shell.CreateShortcut($prepareShortcut)
    $prepare.TargetPath = 'powershell.exe'
    $prepare.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'prepare-client.ps1')`""
    $prepare.WorkingDirectory = $InstallRoot
    $prepare.Save()
    $shortcutPaths += $prepareShortcut

    $scanShortcut = Join-Path $startMenu '重新扫描与适配本机模型.lnk'
    $scan = $shell.CreateShortcut($scanShortcut)
    $scan.TargetPath = 'powershell.exe'
    $scan.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'scan-default.ps1')`""
    $scan.WorkingDirectory = $InstallRoot
    $scan.Save()
    $shortcutPaths += $scanShortcut

    $bindShortcut = Join-Path $startMenu '重新绑定默认模型.lnk'
    $bind = $shell.CreateShortcut($bindShortcut)
    $bind.TargetPath = 'powershell.exe'
    $bind.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'bind-model.ps1')`""
    $bind.WorkingDirectory = $InstallRoot
    $bind.Save()
    $shortcutPaths += $bindShortcut

    $compareShortcut = Join-Path $startMenu '定向对比.lnk'
    $compare = $shell.CreateShortcut($compareShortcut)
    $compare.TargetPath = 'powershell.exe'
    $compare.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'compare-interactive.ps1')`""
    $compare.WorkingDirectory = $InstallRoot
    $compare.Save()
    $shortcutPaths += $compareShortcut

    $benchmarkShortcut = Join-Path $startMenu '本机资源与定向验收.lnk'
    $benchmark = $shell.CreateShortcut($benchmarkShortcut)
    $benchmark.TargetPath = 'powershell.exe'
    $benchmark.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'benchmark-client.ps1')`""
    $benchmark.WorkingDirectory = $InstallRoot
    $benchmark.Save()
    $shortcutPaths += $benchmarkShortcut

    $abShortcut = Join-Path $startMenu '原始读取与定向读取A-B验收.lnk'
    $ab = $shell.CreateShortcut($abShortcut)
    $ab.TargetPath = 'powershell.exe'
    $ab.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'ab-benchmark-client.ps1')`""
    $ab.WorkingDirectory = $InstallRoot
    $ab.Save()
    $shortcutPaths += $abShortcut

    $robustnessShortcut = Join-Path $startMenu '定向读取生产稳健性验收.lnk'
    $robustness = $shell.CreateShortcut($robustnessShortcut)
    $robustness.TargetPath = 'powershell.exe'
    $robustness.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'robustness-audit-client.ps1')`""
    $robustness.WorkingDirectory = $InstallRoot
    $robustness.Save()
    $shortcutPaths += $robustnessShortcut

    $runtimeShortcut = Join-Path $startMenu '运行时定向对比与自动回退.lnk'
    $runtime = $shell.CreateShortcut($runtimeShortcut)
    $runtime.TargetPath = 'powershell.exe'
    $runtime.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'runtime-compare-client.ps1')`""
    $runtime.WorkingDirectory = $InstallRoot
    $runtime.Save()
    $shortcutPaths += $runtimeShortcut

    $uninstallShortcut = Join-Path $startMenu '卸载.lnk'
    $uninstall = $shell.CreateShortcut($uninstallShortcut)
    $uninstall.TargetPath = 'powershell.exe'
    $uninstall.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $appRoot 'uninstall.ps1')`" -InstallRoot `"$InstallRoot`""
    $uninstall.WorkingDirectory = $InstallRoot
    $uninstall.Save()
    $shortcutPaths += $uninstallShortcut
}

$scanExitCode = $null
if (-not $SkipInitialScan) {
    & (Join-Path $appRoot 'prepare-client.ps1') -InstallRoot $InstallRoot
    $scanExitCode = $LASTEXITCODE
}
$adaptationReportPath = Join-Path $dataRoot 'customer-adaptation-report.json'
$adaptation = if (Test-Path -LiteralPath $adaptationReportPath) {
    $adapted = Get-Content -LiteralPath $adaptationReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    [ordered]@{
        report = $adaptationReportPath
        discoveredModels = [int]$adapted.discoveredModelCount
        supportedModels = [int]$adapted.supportedModelCount
        usableModels = [int]$adapted.usableModelCount
        configurationOnlyModels = [int]$adapted.configurationOnlyModelCount
        structuralDirectionReadyModels = [int]$adapted.structuralDirectionReadyModelCount
        structuralDirectionHitRate = [double]$adapted.structuralDirectionHitRate
        recognizedButNoRouteModels = [int]$adapted.recognizedButNoRouteModelCount
        failClosedModels = [int]$adapted.failClosedModelCount
        directedNumericReadyModels = [int]$adapted.directedNumericReadyModelCount
    }
} else { $null }

$bindingReportPath = Join-Path $dataRoot 'customer-model-binding.json'
$binding = if (Test-Path -LiteralPath $bindingReportPath) {
    $bound = Get-Content -LiteralPath $bindingReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    [ordered]@{
        report = $bindingReportPath
        status = [string]$bound.status
        selectedModelId = [string]$bound.selected.modelId
        selectedMapFile = [string]$bound.selected.mapFile
        selectedRoute = $bound.selected.structuralRoute
        strategy = [string]$bound.selectionStrategy
        confidence = [string]$bound.selectionConfidence
        activeProcessMatched = [bool]$bound.selected.activeProcessMatch
    }
} else { $null }

$readinessReportPath = Join-Path $dataRoot 'customer-deployment-readiness.json'
$readiness = if (Test-Path -LiteralPath $readinessReportPath) {
    $ready = Get-Content -LiteralPath $readinessReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    [ordered]@{
        report = $readinessReportPath
        status = [string]$ready.status
        safeToEnableDirectionalComparison = [bool]$ready.safeToEnableDirectionalComparison
        selectedModelId = [string]$ready.binding.modelId
        adapterId = [string]$ready.binding.adapterId
        outputsEquivalent = [bool]$ready.selectedPositionAb.outputsEquivalent
        baselineBytesRead = [long]$ready.selectedPositionAb.baselineBytesRead
        directionalBytesRead = [long]$ready.selectedPositionAb.directionalBytesRead
        byteAvoidanceWithinSelectedRoute = [double]$ready.selectedPositionAb.byteAvoidanceWithinSelectedRoute
        extractionSpeedRatio = [double]$ready.selectedPositionAb.extractionSpeedRatio
        accelerationObserved = [bool]$ready.selectedPositionAb.accelerationObserved
    }
} else { $null }

$receipt = [ordered]@{
    schema = 'haiyu-directional-compare-install/v1'
    installedAt = [DateTimeOffset]::Now.ToString('o')
    installRoot = $InstallRoot
    packageVersion = $manifest.version
    packageDigest = $manifest.packageDigest
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only metadata scan and selected tensor-range sampling; original model bytes are never rewritten'
    }
    initialScanExitCode = $scanExitCode
    adaptation = $adaptation
    binding = $binding
    readiness = $readiness
    shortcuts = $shortcutPaths
    adminRequired = $false
    networkUsed = $false
    originalWeightsModified = $false
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $InstallRoot 'install-receipt.json') -Encoding UTF8
Write-Host "安装完成：$InstallRoot"
Write-Host '已自动识别本机模型、生成对应因果位图、绑定默认模型，并执行原始顺序读取与定向读取 A/B。只有输出等价、读取量下降且墙钟实测加速时才标记可启用；运行时定向读取异常会自动回退原顺序读取，模型身份不一致则严格闭锁。'
