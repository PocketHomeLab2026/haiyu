[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$MaxModels = 0,
    [ValidateRange(1, 8)][int]$MaxPairs = 1,
    [string]$SampleScales = '128,1024,4096,16384',
    [string]$NoiseLevels = '0.000001,0.0001,0.01',
    [int]$Seed = 20260917,
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

function Get-LayeredRoute($Map) {
    $readable = @($Map.tensors | Where-Object {
        $_.storageFormat -eq 'safetensors' -and
        [long]$_.payloadBytes -gt 0 -and
        [int]$_.layer -ge 0 -and
        [long]$_.directionRank -ge 0 -and
        -not [string]::IsNullOrWhiteSpace([string]$_.standardRole) -and
        -not ([string]$_.standardRole).StartsWith('native_unmapped.')
    })
    $roles = @($readable | Group-Object standardRole | ForEach-Object {
        $roleItems = @($_.Group)
        [pscustomobject]@{
            role = [string]$_.Name
            minRank = [long](($roleItems | Measure-Object directionRank -Minimum).Minimum)
            maxRank = [long](($roleItems | Measure-Object directionRank -Maximum).Maximum)
            layers = @($roleItems | ForEach-Object { [int]$_.layer } | Sort-Object -Unique)
        }
    })

    $best = $null
    foreach ($from in $roles) {
        foreach ($to in $roles) {
            if ($from.role -eq $to.role -or $to.maxRank -le $from.minRank) { continue }
            $common = @($from.layers | Where-Object { $to.layers -contains $_ })
            if ($common.Count -eq 0) { continue }
            $candidate = [pscustomobject]@{
                from = $from.role
                to = $to.role
                commonLayerCount = $common.Count
                rankSpan = $to.maxRank - $from.minRank
            }
            if ($null -eq $best -or
                $candidate.commonLayerCount -gt $best.commonLayerCount -or
                ($candidate.commonLayerCount -eq $best.commonLayerCount -and $candidate.rankSpan -gt $best.rankSpan)) {
                $best = $candidate
            }
        }
    }
    return $best
}

$adaptation = Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$allNumeric = @($adaptation.models | Where-Object { [bool]$_.directedNumericSamplingReady })
$candidates = $allNumeric
if ($MaxModels -gt 0) { $candidates = @($candidates | Select-Object -First $MaxModels) }
$resultRoot = Join-Path $dataRoot 'robustness-audit'
New-Item -ItemType Directory -Force -Path $resultRoot | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$results = @()
$index = 0

foreach ($model in $candidates) {
    $index++
    $map = Get-Content -LiteralPath ([string]$model.mapFile) -Raw -Encoding UTF8 | ConvertFrom-Json
    $route = Get-LayeredRoute $map
    $safeName = Get-SafeName ([string]$model.modelId)
    $output = Join-Path $resultRoot ('{0:D3}-{1}-robustness.json' -f $index, $safeName)
    $entry = [ordered]@{
        modelId = [string]$model.modelId
        adapterId = [string]$model.adapterId
        attempted = $false
        passed = $false
        status = 'no-layered-readable-route'
        fromRole = ''
        toRole = ''
        commonLayerCount = 0
        scaleDeterminismPassed = $false
        precisionCoveragePassed = $false
        noiseResponsePassed = $false
        layerCoveragePassed = $false
        faultClosedPassed = $false
        sourceSnapshotsUnchanged = $false
        precisionDTypes = @()
        evidence = $output
        note = '没有找到同层、方向递增、可安全解码的角色对；严格停止。'
    }
    if ($null -eq $route) {
        $results += $entry
        continue
    }

    $entry.attempted = $true
    $entry.fromRole = [string]$route.from
    $entry.toRole = [string]$route.to
    $entry.commonLayerCount = [int]$route.commonLayerCount
    if ($Force -or -not (Test-Path -LiteralPath $output)) {
        $arguments = @(
            'robustness-audit', '--map', [string]$model.mapFile,
            '--from', [string]$route.from,
            '--to', [string]$route.to,
            '--max-pairs', [string]$MaxPairs,
            '--sample-scales', $SampleScales,
            '--noise-levels', $NoiseLevels,
            '--seed', [string]($Seed + $index),
            '--output', $output
        )
        $commandOutput = @(& $exe @arguments 2>&1)
        $commandExit = $LASTEXITCODE
    }
    else {
        $commandOutput = @('复用现有同参数证据文件。')
        $commandExit = 0
    }

    if ($commandExit -eq 0 -and (Test-Path -LiteralPath $output)) {
        $evidence = Get-Content -LiteralPath $output -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry.status = [string]$evidence.status
        $entry.scaleDeterminismPassed = [bool]$evidence.scaleDeterminismPassed
        $entry.precisionCoveragePassed = [bool]$evidence.precisionCoveragePassed
        $entry.noiseResponsePassed = [bool]$evidence.noiseResponsePassed
        $entry.layerCoveragePassed = [bool]$evidence.layerCoveragePassed
        $entry.faultClosedPassed = [bool]$evidence.faultClosedPassed
        $entry.sourceSnapshotsUnchanged = [bool]$evidence.sourceSnapshotsUnchanged
        $entry.precisionDTypes = @($evidence.precisionCases | ForEach-Object { [string]$_.dType } | Sort-Object -Unique)
        $entry.passed = [bool]$evidence.passed -and
            $entry.scaleDeterminismPassed -and
            $entry.precisionCoveragePassed -and
            $entry.noiseResponsePassed -and
            $entry.layerCoveragePassed -and
            $entry.faultClosedPassed -and
            $entry.sourceSnapshotsUnchanged
        $entry.note = '本地只读稳健性门：多规模、精度、噪声、浅中深层、故障闭锁均需通过。'
    }
    else {
        $entry.status = "command-failed:$commandExit"
        $entry.note = ($commandOutput -join ' ')
    }
    $results += $entry
}

$watch.Stop()
$attempted = @($results | Where-Object { [bool]$_.attempted }).Count
$passed = @($results | Where-Object { [bool]$_.passed }).Count
$uncovered = @($results | Where-Object { -not [bool]$_.attempted }).Count
$allCovered = $candidates.Count -gt 0 -and $attempted -eq $candidates.Count
$allPassed = $allCovered -and $passed -eq $attempted
$report = [ordered]@{
    schema = 'haiyu-customer-directional-robustness/v1'
    productVersion = '0.7.0'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only robustness audit; original model bytes are never rewritten'
    }
    adaptationReport = $adaptationPath
    availableNumericModelCount = $allNumeric.Count
    selectedModelCount = $candidates.Count
    attemptedModelCount = $attempted
    passedModelCount = $passed
    uncoveredModelCount = $uncovered
    allSelectedModelsCovered = $allCovered
    allAttemptedModelsPassed = $attempted -gt 0 -and $passed -eq $attempted
    passed = $allPassed
    sampleScales = $SampleScales
    noiseLevels = $NoiseLevels
    elapsedMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
    readOnly = $true
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    noiseAppliedInMemoryOnly = $true
    authority = 'directional extraction robustness only; semantic correctness and full-model inference replacement remain unproven'
    models = $results
}

$reportPath = Join-Path $dataRoot 'customer-robustness-audit-report.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$textPath = Join-Path $dataRoot '定向读取生产稳健性验收.txt'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('海域模型定向对比器：定向读取生产稳健性验收')
$lines.Add("生成时间：$($report.generatedAt)")
$lines.Add("数值就绪模型：$($allNumeric.Count)；本轮选择：$($candidates.Count)；覆盖：$attempted；通过：$passed；无分层路线：$uncovered")
$lines.Add("多规模：$SampleScales；内存噪声：$NoiseLevels；全覆盖：$allCovered；总门：$allPassed")
$lines.Add('验收项：重复读取确定性、可读精度覆盖、噪声响应、浅中深层覆盖、畸形输入/越界/不支持精度严格闭锁、源文件长度与修改时间不变。')
$lines.Add('边界：噪声只作用于内存副本；不写权重、不加载完整权重、不用显卡、不联网。通过不等于语义正确或完整前向已替代。')
$lines.Add('')
foreach ($item in $results) {
    $lines.Add("[$($item.status)] $($item.modelId)")
    $lines.Add("  路线：$($item.fromRole) -> $($item.toRole)；共同层：$($item.commonLayerCount)；精度：$($item.precisionDTypes -join ',')")
    $lines.Add("  多规模=$($item.scaleDeterminismPassed)；精度=$($item.precisionCoveragePassed)；噪声=$($item.noiseResponsePassed)；分层=$($item.layerCoveragePassed)；闭锁=$($item.faultClosedPassed)；只读=$($item.sourceSnapshotsUnchanged)")
    $lines.Add('')
}
[IO.File]::WriteAllLines($textPath, $lines, [Text.UTF8Encoding]::new($false))

[ordered]@{
    ok = $allPassed
    command = 'robustness-audit-client'
    report = $reportPath
    readableReport = $textPath
    availableNumericModelCount = $allNumeric.Count
    selectedModelCount = $candidates.Count
    attemptedModelCount = $attempted
    passedModelCount = $passed
    uncoveredModelCount = $uncovered
    ownerTaijiPresent = $true
} | ConvertTo-Json -Depth 6

if (-not $allPassed) {
    throw "生产稳健性验收失败：通过 $passed/$attempted，未覆盖 $uncovered"
}
