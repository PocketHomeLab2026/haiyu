[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]]$AdditionalRoot = @()
)

$ErrorActionPreference = 'Stop'
$appRoot = if (Test-Path -LiteralPath (Join-Path $InstallRoot 'app\haiyu-directional-compare.exe')) { Join-Path $InstallRoot 'app' } else { $PSScriptRoot }
$exe = Join-Path $appRoot 'haiyu-directional-compare.exe'
$dataRoot = Join-Path $InstallRoot 'data'
$mapsRoot = Join-Path $dataRoot 'maps'
New-Item -ItemType Directory -Force -Path $mapsRoot | Out-Null

$arguments = @('scan', '--output', $mapsRoot, '--max-depth', '8', '--max-models', '512')
foreach ($root in $AdditionalRoot) {
    if (-not [string]::IsNullOrWhiteSpace($root)) {
        $arguments += @('--root', $root)
    }
}

$scanOutput = @(& $exe @arguments 2>&1)
$scanExitCode = $LASTEXITCODE
$manifestPath = Join-Path $mapsRoot 'scan-manifest.json'
if (($scanExitCode -ne 0 -and $scanExitCode -ne 4) -or -not (Test-Path -LiteralPath $manifestPath)) {
    throw "本机模型扫描失败，退出码：$scanExitCode；输出：$($scanOutput -join ' ')"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$models = @()
foreach ($summary in @($manifest.models)) {
    $mapPath = Join-Path $mapsRoot $summary.mapFileName
    if (-not (Test-Path -LiteralPath $mapPath)) { continue }
    $map = Get-Content -LiteralPath $mapPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $ranked = @($map.tensors |
        Where-Object { [long]$_.directionRank -ge 0 } |
        Sort-Object @{Expression={[long]$_.directionRank}}, name)
    $readable = @($ranked |
        Where-Object { $_.storageFormat -eq 'safetensors' -and [long]$_.payloadBytes -gt 0 })
    $formats = @($map.tensors | ForEach-Object { [string]$_.storageFormat } | Where-Object { $_ } | Sort-Object -Unique)
    $mappedCount = @($map.tensors | Where-Object { -not ([string]$_.standardRole).StartsWith('native_unmapped.') }).Count
    $tensorCount = @($map.tensors).Count
    $from = if ($ranked.Count -ge 1) { [string]$ranked[0].name } else { '' }
    $to = if ($ranked.Count -ge 2) { [string]$ranked[-1].name } else { '' }
    $numericFrom = if ($readable.Count -ge 1) { [string]$readable[0].name } else { '' }
    $numericTo = if ($readable.Count -ge 2) { [string]$readable[-1].name } else { '' }
    $supported = [bool]$map.adapter.supported
    $structuralReady = $supported -and $ranked.Count -ge 2
    $numericReady = $structuralReady -and $readable.Count -ge 2
    $reason = if ($tensorCount -eq 0) {
        '只发现配置缓存，未发现可读张量元数据；该条目不计入可用模型命中率。'
    }
    elseif (-not $supported) {
        '未知或未审查架构，严格停用定向比较。'
    }
    elseif (-not $structuralReady) {
        '架构已识别，但当前模型没有足够的已映射原生张量位置，结构定向与数值测量均停用。'
    }
    elseif (-not $numericReady) {
        '结构定向可用；当前存储格式没有可安全解码的 safetensors 浮点窗口，真实数值测量停用。'
    }
    else {
        '结构定向与 safetensors 小窗口数值测量均可用。'
    }

    $models += [ordered]@{
        modelId = [string]$map.modelId
        modelType = [string]$map.modelType
        architectureName = [string]$map.architectureName
        adapterId = [string]$map.adapter.id
        supported = $supported
        layerCount = [int]$map.layerCount
        hiddenSize = [int]$map.hiddenSize
        tensorCount = $tensorCount
        mappedTensorCount = $mappedCount
        mappingCoverage = if ($tensorCount -gt 0) { $mappedCount / $tensorCount } else { 0.0 }
        normalizationType = [string]$map.adapter.normalizationType
        normalizationPlacement = [string]$map.adapter.normalizationPlacement
        residualTopology = [string]$map.adapter.residualTopology
        stateTopology = [string]$map.adapter.stateTopology
        directionPolicy = [string]$map.adapter.directionPolicy
        storageFormats = $formats
        structuralDirectionReady = $structuralReady
        directedNumericSamplingReady = $numericReady
        recommendedStructuralRoute = [ordered]@{ from = $from; to = $to }
        recommendedNumericRoute = [ordered]@{ from = $numericFrom; to = $numericTo }
        mapFile = $mapPath
        modelRoot = [string]$map.modelRoot
        configPath = [string]$map.configPath
        artifactPath = [string]$map.artifactPath
        metadataFingerprint = [string]$map.metadataFingerprint
        decision = if ($tensorCount -eq 0) { 'configuration-only' } elseif (-not $supported) { 'fail-closed' } elseif (-not $structuralReady) { 'recognized-no-route' } elseif ($numericReady) { 'numeric-ready' } else { 'structural-ready' }
        note = $reason
    }
}

$supportedModels = @($models | Where-Object { $_.supported }).Count
$usableModels = @($models | Where-Object { $_.tensorCount -gt 0 }).Count
$configurationOnlyModels = @($models | Where-Object { $_.tensorCount -eq 0 }).Count
$structuralReadyModels = @($models | Where-Object { $_.structuralDirectionReady }).Count
$numericReadyModels = @($models | Where-Object { $_.directedNumericSamplingReady }).Count
$recognizedNoRouteModels = @($models | Where-Object { $_.supported -and $_.tensorCount -gt 0 -and -not $_.structuralDirectionReady }).Count
$failClosedModels = @($models | Where-Object { $_.tensorCount -gt 0 -and -not $_.supported }).Count
$structuralHitRate = if ($usableModels -gt 0) { $structuralReadyModels / $usableModels } else { 0.0 }
$report = [ordered]@{
    schema = 'haiyu-customer-model-adaptation-report/v1'
    productVersion = '0.7.0'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only architecture adaptation; original model bytes are never rewritten'
    }
    scanManifest = $manifestPath
    discoveredModelCount = $models.Count
    supportedModelCount = $supportedModels
    usableModelCount = $usableModels
    configurationOnlyModelCount = $configurationOnlyModels
    structuralDirectionReadyModelCount = $structuralReadyModels
    structuralDirectionHitRate = $structuralHitRate
    recognizedButNoRouteModelCount = $recognizedNoRouteModels
    failClosedModelCount = $failClosedModels
    directedNumericReadyModelCount = $numericReadyModels
    weightPayloadBytesReadDuringAdaptation = 0
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    authority = 'structural-direction-only; semantic accuracy and end-to-end speed require customer workload A/B verification'
    models = $models
}

$reportPath = Join-Path $dataRoot 'customer-adaptation-report.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

$textPath = Join-Path $dataRoot '客户模型适配报告.txt'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('海域模型定向对比器：客户模型适配报告')
$lines.Add("生成时间：$($report.generatedAt)")
$lines.Add("发现条目：$($models.Count)；可用模型：$usableModels；仅配置缓存：$configurationOnlyModels；架构已识别：$supportedModels；结构定向就绪：$structuralReadyModels；可用模型结构命中率：$([Math]::Round($structuralHitRate * 100, 4))%；已识别但无可用路线：$recognizedNoRouteModels；严格停用：$failClosedModels；可做定向浮点抽样：$numericReadyModels")
$lines.Add('边界：自动适配阶段仅读取配置与张量头，不读取权重正文、不使用显卡、不联网、不修改模型。')
$lines.Add('')
foreach ($model in $models) {
    $lines.Add("[$($model.decision)] $($model.modelId)")
    $lines.Add("  架构：$($model.modelType) / $($model.adapterId)；层数：$($model.layerCount)；张量映射：$($model.mappedTensorCount)/$($model.tensorCount)")
    $lines.Add("  原生路线：$($model.directionPolicy)；归一：$($model.normalizationType) / $($model.normalizationPlacement)；残差：$($model.residualTopology)")
    $lines.Add("  结构定向就绪：$($model.structuralDirectionReady)；默认定向：$($model.recommendedStructuralRoute.from) -> $($model.recommendedStructuralRoute.to)")
    $lines.Add("  数值抽样：$($model.directedNumericSamplingReady)；说明：$($model.note)")
    $lines.Add('')
}
[IO.File]::WriteAllLines($textPath, $lines, [Text.UTF8Encoding]::new($false))

[ordered]@{
    ok = $true
    command = 'adapt-client'
    report = $reportPath
    readableReport = $textPath
    discoveredModelCount = $models.Count
    supportedModelCount = $supportedModels
    usableModelCount = $usableModels
    configurationOnlyModelCount = $configurationOnlyModels
    structuralDirectionReadyModelCount = $structuralReadyModels
    structuralDirectionHitRate = $structuralHitRate
    recognizedButNoRouteModelCount = $recognizedNoRouteModels
    failClosedModelCount = $failClosedModels
    directedNumericReadyModelCount = $numericReadyModels
    weightPayloadBytesReadDuringAdaptation = 0
    ownerTaijiPresent = $true
} | ConvertTo-Json -Depth 5
