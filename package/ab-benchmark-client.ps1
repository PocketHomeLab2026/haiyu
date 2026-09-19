[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$MaxModels = 0,
    [ValidateRange(1, 32)][int]$MaxPairs = 1,
    [ValidateRange(128, 1048576)][int]$SampleElements = 4096,
    [ValidateRange(1, 20)][int]$Iterations = 3,
    [switch]$Force
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
$candidates = @($adaptation.models | Where-Object { [bool]$_.directedNumericSamplingReady })
if ($MaxModels -gt 0) { $candidates = @($candidates | Select-Object -First $MaxModels) }
$resultRoot = Join-Path $dataRoot 'ab-benchmark'
New-Item -ItemType Directory -Force -Path $resultRoot | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$results = @()
$index = 0

foreach ($model in $candidates) {
    $index++
    $safeName = Get-SafeName ([string]$model.modelId)
    $output = Join-Path $resultRoot ('{0:D3}-{1}-selected-position-ab.json' -f $index, $safeName)
    if ($Force -or -not (Test-Path -LiteralPath $output)) {
        $arguments = @(
            'ab-benchmark', '--map', [string]$model.mapFile,
            '--from', [string]$model.recommendedNumericRoute.from,
            '--to', [string]$model.recommendedNumericRoute.to,
            '--max-pairs', [string]$MaxPairs,
            '--sample-elements', [string]$SampleElements,
            '--iterations', [string]$Iterations,
            '--output', $output
        )
        $commandOutput = @(& $exe @arguments 2>&1)
        $commandExit = $LASTEXITCODE
    }
    else {
        $commandOutput = @('复用现有同参数证据文件。')
        $commandExit = 0
    }

    $entry = [ordered]@{
        modelId = [string]$model.modelId
        adapterId = [string]$model.adapterId
        attempted = $true
        passed = $false
        status = if ($commandExit -eq 0) { 'missing-output' } else { "command-failed:$commandExit" }
        pairCount = 0
        iterations = 0
        allOutputsEquivalent = $false
        fullSelectedTensorTraversedByBaseline = $false
        totalModelPayloadBytes = [long]0
        selectedRoutePayloadBytes = [long]0
        baselineBytesRead = [long]0
        directionalBytesRead = [long]0
        byteAvoidanceWithinSelectedRoute = 0.0
        medianBaselineMilliseconds = 0.0
        medianDirectionalMilliseconds = 0.0
        medianExtractionSpeedRatio = 0.0
        observedDirectionalExtractionSpeedup = $false
        evidence = $output
        note = ($commandOutput -join ' ')
    }
    if ($commandExit -eq 0 -and (Test-Path -LiteralPath $output)) {
        $evidence = Get-Content -LiteralPath $output -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry.status = [string]$evidence.status
        $entry.pairCount = [int]$evidence.pairCount
        $entry.iterations = [int]$evidence.completedIterations
        $entry.allOutputsEquivalent = [bool]$evidence.allOutputsEquivalent
        $entry.fullSelectedTensorTraversedByBaseline = [bool]$evidence.fullSelectedTensorTraversedByBaseline
        $entry.totalModelPayloadBytes = [long]$evidence.totalModelPayloadBytes
        $entry.selectedRoutePayloadBytes = [long]$evidence.selectedRoutePayloadBytes
        $entry.baselineBytesRead = [long]$evidence.baselineBytesRead
        $entry.directionalBytesRead = [long]$evidence.directionalBytesRead
        $entry.byteAvoidanceWithinSelectedRoute = [double]$evidence.byteAvoidanceWithinSelectedRoute
        $entry.medianBaselineMilliseconds = [double]$evidence.medianBaselineMilliseconds
        $entry.medianDirectionalMilliseconds = [double]$evidence.medianDirectionalMilliseconds
        $entry.medianExtractionSpeedRatio = [double]$evidence.medianExtractionSpeedRatio
        $entry.observedDirectionalExtractionSpeedup = [bool]$evidence.observedDirectionalExtractionSpeedup
        $entry.passed = [bool]$evidence.supported -and $entry.allOutputsEquivalent -and $entry.fullSelectedTensorTraversedByBaseline -and $entry.directionalBytesRead -gt 0 -and $entry.directionalBytesRead -le $entry.baselineBytesRead
        $entry.note = '顺序基线与定向读取使用相同采样窗口；输出摘要与样本数必须完全一致。'
    }
    $results += $entry
}

$watch.Stop()
$attempted = 0
$passed = 0
$baselineBytes = [long]0
$directionalBytes = [long]0
$baselineMilliseconds = [double]0
$directionalMilliseconds = [double]0
$allEquivalent = $true
$allBaselineTraversed = $true
foreach ($item in $results) {
    if ([bool]$item['attempted']) {
        $attempted++
        $baselineBytes += [long]$item['baselineBytesRead']
        $directionalBytes += [long]$item['directionalBytesRead']
        $baselineMilliseconds += [double]$item['medianBaselineMilliseconds']
        $directionalMilliseconds += [double]$item['medianDirectionalMilliseconds']
        if (-not [bool]$item['allOutputsEquivalent']) {
            $allEquivalent = $false
        }
        if (-not [bool]$item['fullSelectedTensorTraversedByBaseline']) {
            $allBaselineTraversed = $false
        }
    }
    if ([bool]$item['passed']) {
        $passed++
    }
}
if ($attempted -eq 0) {
    $allEquivalent = $false
    $allBaselineTraversed = $false
}
$byteAvoidance = if ($baselineBytes -gt 0) { 1.0 - ($directionalBytes / [double]$baselineBytes) } else { 0.0 }
$speedRatio = if ($directionalMilliseconds -gt 0) { $baselineMilliseconds / $directionalMilliseconds } else { 0.0 }

$report = [ordered]@{
    schema = 'haiyu-customer-selected-position-ab/v1'
    productVersion = '0.7.0'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only selected causal-position A/B; original model bytes are never rewritten'
    }
    adaptationReport = $adaptationPath
    availableNumericModelCount = @($adaptation.models | Where-Object { [bool]$_.directedNumericSamplingReady }).Count
    attemptedModelCount = $attempted
    passedModelCount = $passed
    allOutputsEquivalent = $allEquivalent
    fullSelectedTensorTraversedByBaseline = $allBaselineTraversed
    baselineBytesRead = $baselineBytes
    directionalBytesRead = $directionalBytes
    weightedByteAvoidanceWithinSelectedRoute = $byteAvoidance
    aggregateMedianBaselineMilliseconds = [Math]::Round($baselineMilliseconds, 6)
    aggregateMedianDirectionalMilliseconds = [Math]::Round($directionalMilliseconds, 6)
    aggregateExtractionSpeedRatio = [Math]::Round($speedRatio, 6)
    observedDirectionalExtractionSpeedup = $allEquivalent -and $speedRatio -gt 1.0
    elapsedMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
    readOnly = $true
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    authority = 'selected causal-position extraction A/B only; semantic correctness and full-model inference replacement remain unproven'
    models = $results
}

$reportPath = Join-Path $dataRoot 'customer-directional-ab-report.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$textPath = Join-Path $dataRoot '原始读取与定向读取A-B验收.txt'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('海域模型定向对比器：原始顺序读取 vs 因果位定向读取 A/B')
$lines.Add("生成时间：$($report.generatedAt)")
$lines.Add("模型：尝试 $attempted，通过 $passed；输出完全等价：$allEquivalent")
$lines.Add("读取字节：顺序基线 $baselineBytes，定向 $directionalBytes，规避率 $([Math]::Round($byteAvoidance * 100, 6))%")
$lines.Add("墙钟中位数合计：基线 $([Math]::Round($baselineMilliseconds, 3)) ms，定向 $([Math]::Round($directionalMilliseconds, 3)) ms，观测倍数 $([Math]::Round($speedRatio, 3))x")
$lines.Add('边界：只验证所选因果位提取；不把该倍数写成完整模型推理或开放域语义加速。')
$lines.Add('运行边界：只读、本地、未加载完整权重、未使用显卡、未联网、未修改模型。')
[IO.File]::WriteAllLines($textPath, $lines, [Text.UTF8Encoding]::new($false))

$ok = $attempted -eq 0 -or (
    $passed -eq $attempted -and
    $allEquivalent -and
    $allBaselineTraversed -and
    $baselineBytes -gt 0 -and
    $directionalBytes -gt 0 -and
    $directionalBytes -lt $baselineBytes
)
[ordered]@{
    ok = $ok
    command = 'ab-benchmark-client'
    report = $reportPath
    readableReport = $textPath
    attemptedModelCount = $attempted
    passedModelCount = $passed
    allOutputsEquivalent = $allEquivalent
    baselineBytesRead = $baselineBytes
    directionalBytesRead = $directionalBytes
    weightedByteAvoidanceWithinSelectedRoute = $byteAvoidance
    aggregateExtractionSpeedRatio = $speedRatio
    ownerTaijiPresent = $true
} | ConvertTo-Json -Depth 6

if (-not $ok) {
    throw "定向 A/B 验收失败：$passed/$attempted"
}
