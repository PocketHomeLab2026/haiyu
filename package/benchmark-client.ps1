[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$MaxPairs = 2,
    [int]$SampleElements = 4096
)

$ErrorActionPreference = 'Stop'
$appRoot = if (Test-Path -LiteralPath (Join-Path $InstallRoot 'app\haiyu-directional-compare.exe')) { Join-Path $InstallRoot 'app' } else { $PSScriptRoot }
$exe = Join-Path $appRoot 'haiyu-directional-compare.exe'
$dataRoot = Join-Path $InstallRoot 'data'
$adaptationPath = Join-Path $dataRoot 'customer-adaptation-report.json'
if (-not (Test-Path -LiteralPath $adaptationPath)) {
    throw '还没有客户模型适配报告，请先运行“自动适配本机模型”。'
}

function Get-SafeName([string]$Value) {
    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $safe = -join ($Value.ToCharArray() | ForEach-Object { if ($invalid -contains $_) { '_' } else { $_ } })
    if ($safe.Length -gt 80) { return $safe.Substring(0, 80) }
    return $safe
}

$adaptation = Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$benchmarkRoot = Join-Path $dataRoot 'benchmark'
New-Item -ItemType Directory -Force -Path $benchmarkRoot | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$results = @()
$index = 0
$totalModelPayloadBytes = [long]0
$selectedRoutePayloadBytes = [long]0
$actualPayloadBytesRead = [long]0

foreach ($model in @($adaptation.models)) {
    $index++
    $prefix = ('{0:D3}-{1}' -f $index, (Get-SafeName ([string]$model.modelId)))
    $entry = [ordered]@{
        modelId = [string]$model.modelId
        adapterId = [string]$model.adapterId
        structuralDirectionReady = [bool]$model.structuralDirectionReady
        structuralPassed = $false
        structuralDecision = 'not-run'
        structuralStatus = 'not-run'
        directedNumericSamplingReady = [bool]$model.directedNumericSamplingReady
        numericPassed = $false
        numericStatus = 'not-run'
        totalModelPayloadBytes = [long]0
        selectedRoutePayloadBytes = [long]0
        actualPayloadBytesRead = [long]0
        endToEndByteAvoidance = $null
        note = ''
    }

    if (-not [bool]$model.structuralDirectionReady) {
        $entry.structuralStatus = 'skipped-not-ready'
        $entry.numericStatus = 'skipped-not-ready'
        $entry.note = [string]$model.note
        $results += $entry
        continue
    }

    $structuralOutput = Join-Path $benchmarkRoot ($prefix + '-structural.json')
    $compareArgs = @(
        'compare', '--map', [string]$model.mapFile,
        '--from', [string]$model.recommendedStructuralRoute.from,
        '--to', [string]$model.recommendedStructuralRoute.to,
        '--max-paths', '32', '--output', $structuralOutput
    )
    $null = @(& $exe @compareArgs 2>&1)
    $compareExit = $LASTEXITCODE
    if ($compareExit -eq 0 -and (Test-Path -LiteralPath $structuralOutput)) {
        $structural = Get-Content -LiteralPath $structuralOutput -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry.structuralDecision = [string]$structural.decision
        $entry.structuralStatus = [string]$structural.status
        $entry.structuralPassed = [bool]$structural.supported -and [string]$structural.decision -eq 'from-to'
    }
    else {
        $entry.structuralStatus = "command-failed:$compareExit"
    }

    if (-not [bool]$model.directedNumericSamplingReady) {
        $entry.numericStatus = 'skipped-no-readable-safetensors-route'
        $entry.note = '结构方向已验收；本机仅有元数据或当前格式不支持安全浮点窗口解码，未伪造数值证据。'
        $results += $entry
        continue
    }

    $numericOutput = Join-Path $benchmarkRoot ($prefix + '-numeric.json')
    $measureArgs = @(
        'measure', '--map', [string]$model.mapFile,
        '--from', [string]$model.recommendedNumericRoute.from,
        '--to', [string]$model.recommendedNumericRoute.to,
        '--max-pairs', [string]$MaxPairs,
        '--sample-elements', [string]$SampleElements,
        '--output', $numericOutput
    )
    $null = @(& $exe @measureArgs 2>&1)
    $measureExit = $LASTEXITCODE
    if ($measureExit -eq 0 -and (Test-Path -LiteralPath $numericOutput)) {
        $numeric = Get-Content -LiteralPath $numericOutput -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry.numericStatus = [string]$numeric.status
        $entry.numericPassed = [bool]$numeric.supported -and [int]$numeric.pairCount -gt 0
        $entry.totalModelPayloadBytes = [long]$numeric.totalModelPayloadBytes
        $entry.selectedRoutePayloadBytes = [long]$numeric.selectedRoutePayloadBytes
        $entry.actualPayloadBytesRead = [long]$numeric.actualPayloadBytesRead
        $entry.endToEndByteAvoidance = [double]$numeric.endToEndByteAvoidance
        $totalModelPayloadBytes += [long]$numeric.totalModelPayloadBytes
        $selectedRoutePayloadBytes += [long]$numeric.selectedRoutePayloadBytes
        $actualPayloadBytesRead += [long]$numeric.actualPayloadBytesRead
    }
    else {
        $entry.numericStatus = "command-failed:$measureExit"
    }
    $results += $entry
}

$watch.Stop()
$structuralReadyCount = @($results | Where-Object { $_.structuralDirectionReady }).Count
$structuralPassedCount = @($results | Where-Object { $_.structuralPassed }).Count
$numericReadyCount = @($results | Where-Object { $_.directedNumericSamplingReady }).Count
$numericPassedCount = @($results | Where-Object { $_.numericPassed }).Count
$numericEvidenceAvailable = $numericPassedCount -gt 0 -and $totalModelPayloadBytes -gt 0
$weightedAvoidance = if ($numericEvidenceAvailable) { 1.0 - ($actualPayloadBytesRead / [double]$totalModelPayloadBytes) } else { $null }

$report = [ordered]@{
    schema = 'haiyu-customer-directional-resource-acceptance/v1'
    productVersion = '0.7.0'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only local acceptance; original model bytes are never rewritten'
    }
    adaptationReport = $adaptationPath
    discoveredModelCount = @($adaptation.models).Count
    structuralDirectionReadyModelCount = $structuralReadyCount
    structuralPassedModelCount = $structuralPassedCount
    directedNumericReadyModelCount = $numericReadyCount
    directedNumericPassedModelCount = $numericPassedCount
    numericEvidenceAvailable = $numericEvidenceAvailable
    totalModelPayloadBytes = $totalModelPayloadBytes
    selectedRoutePayloadBytes = $selectedRoutePayloadBytes
    actualPayloadBytesRead = $actualPayloadBytesRead
    weightedEndToEndByteAvoidance = $weightedAvoidance
    elapsedMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
    readOnly = $true
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    endToEndSpeedupClaimed = $false
    customerWorkloadWallClockABRequired = $true
    authority = 'structural direction and selected safetensors-window I/O evidence only; semantic accuracy and end-to-end speed remain unproven until customer workload A/B'
    models = $results
}

$reportPath = Join-Path $dataRoot 'customer-benchmark-report.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$textPath = Join-Path $dataRoot '客户机资源与定向验收.txt'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('海域模型定向对比器：客户机资源与定向验收')
$lines.Add("生成时间：$($report.generatedAt)")
$lines.Add("结构定向：就绪 $structuralReadyCount，严格通过 $structuralPassedCount")
$lines.Add("真实浮点窗口：就绪 $numericReadyCount，通过 $numericPassedCount")
if ($numericEvidenceAvailable) {
    $lines.Add("数值路径实际读取：$actualPayloadBytesRead / $totalModelPayloadBytes 字节；加权字节规避率：$([Math]::Round($weightedAvoidance * 100, 6))%")
}
else {
    $lines.Add('本机未发现可安全读取的 safetensors 数值路径；本报告不生成虚假的节省率或速度结论。')
}
$lines.Add('边界：字节规避率只代表 I/O 范围，不等于端到端速度倍数；语义命中率与真实耗时仍需客户任务同口径 A/B。')
$lines.Add('运行边界：只读、本地、未加载完整权重、未使用显卡、未联网、未修改模型。')
[IO.File]::WriteAllLines($textPath, $lines, [Text.UTF8Encoding]::new($false))

[ordered]@{
    ok = $structuralReadyCount -eq 0 -or $structuralPassedCount -eq $structuralReadyCount
    command = 'benchmark-client'
    report = $reportPath
    readableReport = $textPath
    structuralReadyModelCount = $structuralReadyCount
    structuralPassedModelCount = $structuralPassedCount
    directedNumericReadyModelCount = $numericReadyCount
    directedNumericPassedModelCount = $numericPassedCount
    numericEvidenceAvailable = $numericEvidenceAvailable
    weightedEndToEndByteAvoidance = $weightedAvoidance
    ownerTaijiPresent = $true
} | ConvertTo-Json -Depth 6

if ($structuralReadyCount -gt 0 -and $structuralPassedCount -ne $structuralReadyCount) {
    throw "结构定向验收失败：$structuralPassedCount/$structuralReadyCount"
}
if ($numericReadyCount -gt 0 -and $numericPassedCount -ne $numericReadyCount) {
    throw "真实浮点窗口验收失败：$numericPassedCount/$numericReadyCount"
}
