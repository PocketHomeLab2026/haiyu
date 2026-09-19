[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ModelId = ''
)

$ErrorActionPreference = 'Stop'
$dataRoot = Join-Path $InstallRoot 'data'
$mapsRoot = Join-Path $dataRoot 'maps'
$adaptationPath = Join-Path $dataRoot 'customer-adaptation-report.json'
$bindingPath = Join-Path $dataRoot 'customer-model-binding.json'
$readablePath = Join-Path $dataRoot '客户模型绑定.txt'

if (-not (Test-Path -LiteralPath $adaptationPath)) {
    throw '尚未生成客户模型适配报告，请先运行自动适配本机模型。'
}

$report = Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$candidates = @($report.models | Where-Object {
    [bool]$_.supported -and [bool]$_.structuralDirectionReady -and [int]$_.tensorCount -gt 0
})

$processInventory = @()
try {
    $processInventory = @(Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object {
        [ordered]@{
            name = [string]$_.Name
            searchText = (([string]$_.ExecutablePath) + "`n" + ([string]$_.CommandLine))
        }
    })
}
catch {
    $processInventory = @()
}

$ranked = @()
foreach ($candidate in $candidates) {
    $mapPath = [IO.Path]::GetFullPath([string]$candidate.mapFile)
    $resolvedMapsRoot = [IO.Path]::GetFullPath($mapsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $mapPath.StartsWith($resolvedMapsRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
    if (-not (Test-Path -LiteralPath $mapPath)) { continue }

    $map = Get-Content -LiteralPath $mapPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [bool]$map.adapter.supported) { continue }
    if ([string]$map.modelId -ne [string]$candidate.modelId) { continue }

    $needles = @([string]$map.modelRoot, [string]$map.artifactPath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 4 }
    $matchedProcesses = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($process in $processInventory) {
        foreach ($needle in $needles) {
            if ($process.searchText.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                [void]$matchedProcesses.Add($process.name)
                break
            }
        }
    }

    $active = $matchedProcesses.Count -gt 0
    $numeric = [bool]$candidate.directedNumericSamplingReady
    $coverage = [double]$candidate.mappingCoverage
    $score = 0L
    if ($active) { $score += 1000000L }
    if ($numeric) { $score += 100000L }
    $score += [long][Math]::Round($coverage * 10000.0)
    $score += [Math]::Min([long]$candidate.tensorCount, 9999L)

    $ranked += [pscustomobject][ordered]@{
        candidate = $candidate
        map = $map
        mapPath = $mapPath
        activeProcessMatch = $active
        matchedProcessNames = @($matchedProcesses | Sort-Object)
        score = $score
    }
}
$ranked = @($ranked | Sort-Object @{Expression={$_.score};Descending=$true}, @{Expression={$_.candidate.modelId};Descending=$false})

$selected = $null
$strategy = 'none'
$confidence = 'none'
if (-not [string]::IsNullOrWhiteSpace($ModelId)) {
    $selected = $ranked | Where-Object { [string]$_.candidate.modelId -eq $ModelId } | Select-Object -First 1
    if ($null -eq $selected) { throw "指定模型未通过结构定向门或不存在：$ModelId" }
    $strategy = 'explicit-model-id'
    $confidence = 'high'
}
else {
    $activeCandidates = @($ranked | Where-Object { $_.activeProcessMatch })
    if ($activeCandidates.Count -eq 1) {
        $selected = $activeCandidates[0]
        $strategy = 'active-process-path-match'
        $confidence = 'high'
    }
    elseif ($ranked.Count -eq 1) {
        $selected = $ranked[0]
        $strategy = 'single-compatible-model'
        $confidence = 'high'
    }
    elseif ($ranked.Count -gt 0) {
        $selected = $ranked[0]
        $strategy = if ($activeCandidates.Count -gt 1) { 'ranked-among-active-models' } else { 'ranked-compatible-default' }
        $confidence = if ($activeCandidates.Count -gt 1) { 'medium' } else { 'low' }
    }
}

$candidateSummaries = @($ranked | ForEach-Object {
    [ordered]@{
        modelId = [string]$_.candidate.modelId
        adapterId = [string]$_.candidate.adapterId
        decision = [string]$_.candidate.decision
        numericReady = [bool]$_.candidate.directedNumericSamplingReady
        mappingCoverage = [double]$_.candidate.mappingCoverage
        activeProcessMatch = [bool]$_.activeProcessMatch
        matchedProcessNames = @($_.matchedProcessNames)
        score = [long]$_.score
        mapFile = [string]$_.mapPath
    }
})

$binding = [ordered]@{
    schema = 'haiyu-customer-model-binding/v1'
    productVersion = '0.7.0'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only model discovery and causal-position-map binding; original model bytes are never rewritten'
    }
    status = if ($null -eq $selected) { 'fail-closed-no-compatible-model' } else { 'bound' }
    selectionStrategy = $strategy
    selectionConfidence = $confidence
    compatibleCandidateCount = $ranked.Count
    selected = $null
    candidates = $candidateSummaries
    fullWeightLoaded = $false
    weightPayloadBytesRead = 0
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    authority = 'default local model binding for directional extraction only; not semantic or full-inference authority'
}

if ($null -ne $selected) {
    $binding.selected = [ordered]@{
        modelId = [string]$selected.candidate.modelId
        modelType = [string]$selected.candidate.modelType
        architectureName = [string]$selected.candidate.architectureName
        adapterId = [string]$selected.candidate.adapterId
        modelRoot = [string]$selected.map.modelRoot
        configPath = [string]$selected.map.configPath
        artifactPath = [string]$selected.map.artifactPath
        metadataFingerprint = [string]$selected.map.metadataFingerprint
        mapFile = [string]$selected.mapPath
        mapSha256 = (Get-FileHash -LiteralPath $selected.mapPath -Algorithm SHA256).Hash.ToLowerInvariant()
        decision = [string]$selected.candidate.decision
        structuralDirectionReady = [bool]$selected.candidate.structuralDirectionReady
        directedNumericSamplingReady = [bool]$selected.candidate.directedNumericSamplingReady
        structuralRoute = $selected.candidate.recommendedStructuralRoute
        numericRoute = $selected.candidate.recommendedNumericRoute
        activeProcessMatch = [bool]$selected.activeProcessMatch
        matchedProcessNames = @($selected.matchedProcessNames)
    }
}

$binding | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $bindingPath -Encoding UTF8

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('海域模型定向对比器：客户模型绑定')
$lines.Add("生成时间：$($binding.generatedAt)")
$lines.Add("状态：$($binding.status)；策略：$strategy；置信级：$confidence；兼容候选：$($ranked.Count)")
if ($null -ne $selected) {
    $lines.Add("默认模型：$($binding.selected.modelId)")
    $lines.Add("架构适配器：$($binding.selected.adapterId)")
    $lines.Add("结构路线：$($binding.selected.structuralRoute.from) -> $($binding.selected.structuralRoute.to)")
    $lines.Add("数值定向就绪：$($binding.selected.directedNumericSamplingReady)")
    $lines.Add("因果位图：$($binding.selected.mapFile)")
}
else {
    $lines.Add('没有模型通过已审查架构与结构路线门，定向对比保持关闭。')
}
$lines.Add('边界：绑定只决定默认因果位图；不读取权重正文、不加载整模、不使用GPU、不联网、不修改原始模型。')
[IO.File]::WriteAllLines($readablePath, $lines, [Text.UTF8Encoding]::new($false))

[ordered]@{
    ok = $null -ne $selected
    command = 'bind-model'
    status = $binding.status
    binding = $bindingPath
    readableBinding = $readablePath
    selectedModelId = if ($null -ne $selected) { [string]$binding.selected.modelId } else { '' }
    selectionStrategy = $strategy
    selectionConfidence = $confidence
    compatibleCandidateCount = $ranked.Count
    ownerTaijiPresent = $true
} | ConvertTo-Json -Depth 5
