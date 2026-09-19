[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ModelId = '',
    [switch]$ChooseModel
)

$ErrorActionPreference = 'Stop'
$appRoot = if (Test-Path -LiteralPath (Join-Path $InstallRoot 'app\haiyu-directional-compare.exe')) { Join-Path $InstallRoot 'app' } else { $PSScriptRoot }
$exe = Join-Path $appRoot 'haiyu-directional-compare.exe'
$maps = Join-Path $InstallRoot 'data\maps'
if (-not (Test-Path -LiteralPath $maps)) {
    throw '还没有本机因果位图，请先运行“自动适配本机模型”。'
}
$mapFiles = @(Get-ChildItem -LiteralPath $maps -Filter '*.causal-position-map.json' -File | Sort-Object Name)
if ($mapFiles.Count -eq 0) {
    throw '没有发现可比较的模型图。'
}
$entries = @()
foreach ($file in $mapFiles) {
    $map = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [bool]$map.adapter.supported) { continue }
    $ranked = @($map.tensors | Where-Object { [long]$_.directionRank -ge 0 } | Sort-Object @{Expression={[long]$_.directionRank}}, name)
    if ($ranked.Count -lt 2) { continue }
    $readable = @($ranked | Where-Object { $_.storageFormat -eq 'safetensors' -and [long]$_.payloadBytes -gt 0 })
    $entries += [ordered]@{ file = $file; map = $map; from = [string]$ranked[0].name; to = [string]$ranked[-1].name; numericReady = $readable.Count -ge 2; numericFrom = if ($readable.Count -ge 1) { [string]$readable[0].name } else { '' }; numericTo = if ($readable.Count -ge 2) { [string]$readable[-1].name } else { '' } }
}
if ($entries.Count -eq 0) { throw '发现了模型，但没有通过已审查架构门的可比较模型。请查看客户模型适配报告。' }

$entry = $null
if (-not [string]::IsNullOrWhiteSpace($ModelId)) {
    $entry = @($entries | Where-Object { [string]$_.map.modelId -eq $ModelId } | Select-Object -First 1)[0]
    if ($null -eq $entry) { throw "指定模型不存在或未通过结构门：$ModelId" }
}
elseif (-not $ChooseModel) {
    $bindingPath = Join-Path $InstallRoot 'data\customer-model-binding.json'
    if (Test-Path -LiteralPath $bindingPath) {
        $binding = Get-Content -LiteralPath $bindingPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]$binding.status -eq 'bound') {
            $boundMap = [IO.Path]::GetFullPath([string]$binding.selected.mapFile)
            $entry = @($entries | Where-Object { [IO.Path]::GetFullPath($_.file.FullName) -eq $boundMap } | Select-Object -First 1)[0]
            if ($null -ne $entry) {
                Write-Host "已使用自动绑定模型：$($entry.map.modelId)（策略：$($binding.selectionStrategy)；置信：$($binding.selectionConfidence)）"
            }
        }
    }
}

if ($null -eq $entry) {
    Write-Host '已适配模型：'
    for ($index = 0; $index -lt $entries.Count; $index++) {
        Write-Host ("[{0}] {1}  ({2}, {3})" -f ($index + 1), $entries[$index].map.modelId, $entries[$index].map.modelType, $entries[$index].map.adapter.id)
    }
    $selection = [int](Read-Host '请选择编号') - 1
    if ($selection -lt 0 -or $selection -ge $entries.Count) { throw '编号无效。' }
    $entry = $entries[$selection]
}
$from = Read-Host "起点（直接回车使用原生默认：$($entry.from)）"
$to = Read-Host "终点（直接回车使用原生默认：$($entry.to)）"
if ([string]::IsNullOrWhiteSpace($from)) { $from = $entry.from }
if ([string]::IsNullOrWhiteSpace($to)) { $to = $entry.to }
& $exe compare --map $entry.file.FullName --from $from --to $to --max-paths 32
if ($entry.numericReady) {
    $measure = Read-Host '是否继续读取定向浮点样本做数值对比？[Y/n]'
    if ([string]::IsNullOrWhiteSpace($measure) -or $measure -match '^(y|yes|是)$') {
        & $exe measure --map $entry.file.FullName --from $entry.numericFrom --to $entry.numericTo --max-pairs 8 --sample-elements 32768
    }
}
else {
    Write-Host '当前模型只有结构位图或不支持安全数值解码，已跳过浮点窗口读取。'
}
Write-Host '说明：结构方向和抽样数值关系均不等于语义正确率；字节规避率也不直接等于速度倍数。'
Read-Host '按回车结束' | Out-Null
