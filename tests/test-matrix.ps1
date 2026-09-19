[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$ScanDirectory,
    [Parameter(Mandatory=$true)]
    [string]$EnginePath,
    [Parameter(Mandatory=$true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$scanRoot = [IO.Path]::GetFullPath($ScanDirectory)
$engine = [IO.Path]::GetFullPath($EnginePath)
$output = [IO.Path]::GetFullPath($OutputPath)
$started = [Diagnostics.Stopwatch]::StartNew()
$rows = @()

foreach ($mapFile in @(Get-ChildItem -LiteralPath $scanRoot -Filter '*.causal-position-map.json' -File | Sort-Object Name)) {
    $map = Get-Content -LiteralPath $mapFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $ranked = @($map.tensors | Where-Object { [long]$_.directionRank -ge 0 } | Sort-Object @{Expression={[long]$_.directionRank}}, name)
    if ($ranked.Count -lt 2) {
        $rows += [ordered]@{
            modelId = $map.modelId
            adapterId = $map.adapter.id
            forwardPassed = $false
            reversePassed = $false
            sameStageChecked = $false
            sameStagePassed = $false
            note = 'fewer-than-two-ranked-tensors'
        }
        continue
    }

    $from = $ranked[0]
    $to = $ranked[-1]
    $forward = (& $engine compare --map $mapFile.FullName --from $from.name --to $to.name --max-paths 2 | Out-String) | ConvertFrom-Json
    $reverse = (& $engine compare --map $mapFile.FullName --from $to.name --to $from.name --max-paths 2 | Out-String) | ConvertFrom-Json

    $sameStage = $null
    $sameStageGroup = @($ranked | Group-Object directionRank | Where-Object Count -ge 2 | Select-Object -First 1)
    if ($sameStageGroup.Count -gt 0) {
        $pair = @($sameStageGroup[0].Group | Select-Object -First 2)
        $sameStage = (& $engine compare --map $mapFile.FullName --from $pair[0].name --to $pair[1].name --max-paths 2 | Out-String) | ConvertFrom-Json
    }

    $rows += [ordered]@{
        modelId = $map.modelId
        modelType = $map.modelType
        adapterId = $map.adapter.id
        tensorCount = @($map.tensors).Count
        forwardFrom = $from.name
        forwardTo = $to.name
        forwardDecision = $forward.decision
        forwardScore = $forward.directionScore
        forwardPassed = $forward.decision -eq 'from-to' -and $forward.directionScore -eq 1
        reverseDecision = $reverse.decision
        reverseScore = $reverse.reverseScore
        reversePassed = $reverse.decision -eq 'to-from' -and $reverse.reverseScore -eq 1
        sameStageChecked = $null -ne $sameStage
        sameStageDecision = if ($null -eq $sameStage) { $null } else { $sameStage.decision }
        sameStagePassed = if ($null -eq $sameStage) { $null } else { $sameStage.decision -eq 'undetermined' }
        ownerTaijiPresent = [bool]$map.ownerTaiji.present
    }
}

$started.Stop()
$sameStageRows = @($rows | Where-Object sameStageChecked)
$receipt = [ordered]@{
    schema = 'haiyu-directional-compare-matrix/v1'
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only metadata scan; original model bytes are never rewritten'
    }
    modelCount = $rows.Count
    forwardPassed = @($rows | Where-Object forwardPassed).Count
    reversePassed = @($rows | Where-Object reversePassed).Count
    sameStageChecked = $sameStageRows.Count
    sameStagePassed = @($sameStageRows | Where-Object sameStagePassed).Count
    allPassed = @($rows | Where-Object { -not $_.forwardPassed -or -not $_.reversePassed -or ($_.sameStageChecked -and -not $_.sameStagePassed) }).Count -eq 0
    elapsedMilliseconds = [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
    authority = 'structural-direction-only; semantic meaning and answer correctness remain unproven'
    rows = $rows
}
$parent = Split-Path -Parent $output
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding UTF8
$receipt | ConvertTo-Json -Depth 8
if (-not $receipt.allPassed) { exit 1 }
