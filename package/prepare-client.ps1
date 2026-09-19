[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]]$AdditionalRoot = @(),
    [string]$ModelId = '',
    [ValidateRange(1, 32)][int]$MaxPairs = 1,
    [ValidateRange(128, 1048576)][int]$SampleElements = 4096,
    [ValidateRange(1, 20)][int]$Iterations = 3,
    [switch]$RequireReady
)

$ErrorActionPreference = 'Stop'
$appRoot = if (Test-Path -LiteralPath (Join-Path $InstallRoot 'app\haiyu-directional-compare.exe')) { Join-Path $InstallRoot 'app' } else { $PSScriptRoot }
$exe = Join-Path $appRoot 'haiyu-directional-compare.exe'
$dataRoot = Join-Path $InstallRoot 'data'
$mapsRoot = Join-Path $dataRoot 'maps'
$evidenceRoot = Join-Path $dataRoot 'deployment-readiness'
$adaptScript = Join-Path $appRoot 'adapt-client.ps1'
$bindScript = Join-Path $appRoot 'bind-model.ps1'
$reportPath = Join-Path $dataRoot 'customer-deployment-readiness.json'
$readablePath = Join-Path $dataRoot '客户机一键部署验收.txt'
New-Item -ItemType Directory -Force -Path $dataRoot,$mapsRoot,$evidenceRoot | Out-Null

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

$watch = [Diagnostics.Stopwatch]::StartNew()
$null = & $adaptScript -InstallRoot $InstallRoot -AdditionalRoot $AdditionalRoot
if ([string]::IsNullOrWhiteSpace($ModelId)) {
    $null = & $bindScript -InstallRoot $InstallRoot
}
else {
    $null = & $bindScript -InstallRoot $InstallRoot -ModelId $ModelId
}

$adaptationPath = Join-Path $dataRoot 'customer-adaptation-report.json'
$bindingPath = Join-Path $dataRoot 'customer-model-binding.json'
if (-not (Test-Path -LiteralPath $adaptationPath)) { throw '自动发现完成后缺少客户模型适配报告。' }
if (-not (Test-Path -LiteralPath $bindingPath)) { throw '自动发现完成后缺少客户模型绑定报告。' }
$adaptation = Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$binding = Get-Content -LiteralPath $bindingPath -Raw -Encoding UTF8 | ConvertFrom-Json

$status = 'fail-closed-no-compatible-model'
$structural = [ordered]@{
    attempted = $false
    passed = $false
    decision = ''
    status = ''
    evidence = ''
    note = ''
}
$numeric = [ordered]@{
    attempted = $false
    passed = $false
    outputsEquivalent = $false
    fullSelectedTensorTraversedByBaseline = $false
    baselineBytesRead = [long]0
    directionalBytesRead = [long]0
    byteAvoidanceWithinSelectedRoute = 0.0
    medianBaselineMilliseconds = 0.0
    medianDirectionalMilliseconds = 0.0
    extractionSpeedRatio = 0.0
    accelerationObserved = $false
    evidence = ''
    note = ''
}

$selected = $binding.selected
if ([string]$binding.status -eq 'bound' -and $null -ne $selected -and -not [string]::IsNullOrWhiteSpace([string]$selected.modelId)) {
    $mapPath = [IO.Path]::GetFullPath([string]$selected.mapFile)
    Assert-PathUnderRoot -Path $mapPath -Root $mapsRoot
    if (-not (Test-Path -LiteralPath $mapPath)) { throw '绑定的因果位图不存在。' }

    $from = [string]$selected.structuralRoute.from
    $to = [string]$selected.structuralRoute.to
    if (-not [string]::IsNullOrWhiteSpace($from) -and -not [string]::IsNullOrWhiteSpace($to)) {
        $structural.attempted = $true
        $structuralPath = Join-Path $evidenceRoot 'bound-model-structural-compare.json'
        $structuralOutput = @(& $exe compare --map $mapPath --from $from --to $to --max-pairs $MaxPairs --output $structuralPath 2>&1)
        $structuralExit = $LASTEXITCODE
        if ($structuralExit -eq 0 -and (Test-Path -LiteralPath $structuralPath)) {
            $structuralEvidence = Get-Content -LiteralPath $structuralPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $structural.passed = [bool]$structuralEvidence.supported
            $structural.decision = [string]$structuralEvidence.decision
            $structural.status = [string]$structuralEvidence.status
            $structural.evidence = $structuralPath
            $structural.note = '结构路线由已审查架构适配器生成，不使用关键词路由。'
        }
        else {
            $structural.status = "command-failed:$structuralExit"
            $structural.note = ($structuralOutput -join ' ')
        }
    }
    else {
        $structural.status = 'missing-structural-route'
        $structural.note = '绑定模型没有可用结构路线。'
    }

    if ([bool]$selected.directedNumericSamplingReady -and [bool]$structural.passed) {
        $numericFrom = [string]$selected.numericRoute.from
        $numericTo = [string]$selected.numericRoute.to
        if (-not [string]::IsNullOrWhiteSpace($numericFrom) -and -not [string]::IsNullOrWhiteSpace($numericTo)) {
            $numeric.attempted = $true
            $numericPath = Join-Path $evidenceRoot 'bound-model-selected-position-ab.json'
            $numericOutput = @(& $exe ab-benchmark --map $mapPath --from $numericFrom --to $numericTo --max-pairs $MaxPairs --sample-elements $SampleElements --iterations $Iterations --output $numericPath 2>&1)
            $numericExit = $LASTEXITCODE
            if ($numericExit -eq 0 -and (Test-Path -LiteralPath $numericPath)) {
                $numericEvidence = Get-Content -LiteralPath $numericPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $numeric.outputsEquivalent = [bool]$numericEvidence.allOutputsEquivalent
                $numeric.fullSelectedTensorTraversedByBaseline = [bool]$numericEvidence.fullSelectedTensorTraversedByBaseline
                $numeric.baselineBytesRead = [long]$numericEvidence.baselineBytesRead
                $numeric.directionalBytesRead = [long]$numericEvidence.directionalBytesRead
                $numeric.byteAvoidanceWithinSelectedRoute = [double]$numericEvidence.byteAvoidanceWithinSelectedRoute
                $numeric.medianBaselineMilliseconds = [double]$numericEvidence.medianBaselineMilliseconds
                $numeric.medianDirectionalMilliseconds = [double]$numericEvidence.medianDirectionalMilliseconds
                $numeric.extractionSpeedRatio = [double]$numericEvidence.medianExtractionSpeedRatio
                $numeric.accelerationObserved = [bool]$numericEvidence.observedDirectionalExtractionSpeedup
                $numeric.passed = [bool]$numericEvidence.supported -and $numeric.outputsEquivalent -and $numeric.fullSelectedTensorTraversedByBaseline -and $numeric.baselineBytesRead -gt 0 -and $numeric.directionalBytesRead -gt 0 -and $numeric.directionalBytesRead -lt $numeric.baselineBytesRead
                $numeric.evidence = $numericPath
                $numeric.note = '原方式完整顺序遍历所选张量；定向方式只读取相同采样窗口，输出摘要与样本数必须完全一致。'
            }
            else {
                $numeric.note = "A/B命令失败：$numericExit；$($numericOutput -join ' ')"
            }
        }
    }

    if (-not [bool]$structural.passed) {
        $status = 'fail-closed-structural-route'
    }
    elseif (-not [bool]$selected.directedNumericSamplingReady) {
        $status = 'structural-only-numeric-disabled'
    }
    elseif (-not [bool]$numeric.passed) {
        $status = 'fail-closed-numeric-equivalence'
    }
    elseif (-not [bool]$numeric.accelerationObserved) {
        $status = 'verified-equivalent-no-wallclock-speedup'
    }
    else {
        $status = 'ready-selected-position-acceleration'
    }
}

$watch.Stop()
$safeToEnable = $status -eq 'ready-selected-position-acceleration'
$report = [ordered]@{
    schema = 'haiyu-customer-deployment-readiness/v1'
    productVersion = '0.7.0'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only discovery, architecture adaptation and selected causal-position A/B; original model bytes are never rewritten'
    }
    status = $status
    safeToEnableDirectionalComparison = $safeToEnable
    discovery = [ordered]@{
        discoveredModels = [int]$adaptation.discoveredModelCount
        usableModels = [int]$adaptation.usableModelCount
        supportedModels = [int]$adaptation.supportedModelCount
        structuralDirectionReadyModels = [int]$adaptation.structuralDirectionReadyModelCount
        directedNumericReadyModels = [int]$adaptation.directedNumericReadyModelCount
        failClosedModels = [int]$adaptation.failClosedModelCount
        report = $adaptationPath
    }
    binding = [ordered]@{
        status = [string]$binding.status
        modelId = if ($null -ne $selected) { [string]$selected.modelId } else { '' }
        architectureName = if ($null -ne $selected) { [string]$selected.architectureName } else { '' }
        adapterId = if ($null -ne $selected) { [string]$selected.adapterId } else { '' }
        mapFile = if ($null -ne $selected) { [string]$selected.mapFile } else { '' }
        strategy = [string]$binding.selectionStrategy
        confidence = [string]$binding.selectionConfidence
        activeProcessMatched = if ($null -ne $selected) { [bool]$selected.activeProcessMatch } else { $false }
        report = $bindingPath
    }
    structuralVerification = $structural
    selectedPositionAb = $numeric
    elapsedMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
    readOnly = $true
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    baselinePreserved = -not $safeToEnable
    authority = 'customer-machine selected causal-position comparison only; full-model inference, generation quality and semantic correctness are not replaced or proven'
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('海域模型定向对比器：客户机一键发现、适配与验收')
$lines.Add("生成时间：$($report.generatedAt)")
$lines.Add("状态：$status；允许启用定向比较：$safeToEnable")
$lines.Add("发现模型：$($report.discovery.discoveredModels)；可用：$($report.discovery.usableModels)；架构已识别：$($report.discovery.supportedModels)；数值定向就绪：$($report.discovery.directedNumericReadyModels)")
$lines.Add("绑定模型：$($report.binding.modelId)；架构适配器：$($report.binding.adapterId)；选择策略：$($report.binding.strategy)；置信级：$($report.binding.confidence)")
$lines.Add("结构定向：尝试=$($structural.attempted)，通过=$($structural.passed)，状态=$($structural.status)")
$lines.Add("A/B：尝试=$($numeric.attempted)，输出等价=$($numeric.outputsEquivalent)，基线读取=$($numeric.baselineBytesRead)，定向读取=$($numeric.directionalBytesRead)，字节规避率=$([Math]::Round(([double]$numeric.byteAvoidanceWithinSelectedRoute) * 100, 6))%")
$lines.Add("墙钟：基线=$([Math]::Round([double]$numeric.medianBaselineMilliseconds, 3)) ms，定向=$([Math]::Round([double]$numeric.medianDirectionalMilliseconds, 3)) ms，观测倍数=$([Math]::Round([double]$numeric.extractionSpeedRatio, 3))x")
if (-not $safeToEnable) {
    $lines.Add('未通过完整启用门：保留客户原有比较方式，不自动切换。')
}
$lines.Add('边界：这里只替代所选因果位的对比读取路径，不宣称替代完整模型推理、生成或语义判断。')
$lines.Add('运行边界：主人太极锚点在场；只读、本地、未加载完整权重、未使用显卡、未联网、未修改模型。')
[IO.File]::WriteAllLines($readablePath, $lines, [Text.UTF8Encoding]::new($false))

[ordered]@{
    ok = if ($RequireReady) { $safeToEnable } else { $true }
    command = 'prepare-client'
    status = $status
    safeToEnableDirectionalComparison = $safeToEnable
    report = $reportPath
    readableReport = $readablePath
    selectedModelId = [string]$report.binding.modelId
    adapterId = [string]$report.binding.adapterId
    outputsEquivalent = [bool]$numeric.outputsEquivalent
    byteAvoidanceWithinSelectedRoute = [double]$numeric.byteAvoidanceWithinSelectedRoute
    extractionSpeedRatio = [double]$numeric.extractionSpeedRatio
    ownerTaijiPresent = $true
} | ConvertTo-Json -Depth 6

if ($RequireReady -and -not $safeToEnable) {
    exit 1
}
