[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.7.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj'
$packageSource = Join-Path $root 'package'
$readme = Join-Path $root 'README.md'
$artifactRoot = Join-Path $root 'artifacts\directional-compare-packages'
$stage = Join-Path $artifactRoot ("haiyu-directional-compare-$Version-$Runtime")
$publish = Join-Path $artifactRoot "publish-$Version-$Runtime"

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径越出构建产物目录：$resolvedPath"
    }
}

dotnet publish $project -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Assert-PathUnderRoot -Path $stage -AllowedRoot $artifactRoot
Assert-PathUnderRoot -Path $publish -AllowedRoot $artifactRoot
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'haiyu-directional-compare.exe') -Destination (Join-Path $stage 'haiyu-directional-compare.exe')
Copy-Item -Path (Join-Path $packageSource '*') -Destination $stage -Recurse -Force
Copy-Item -LiteralPath $readme -Destination (Join-Path $stage 'README-zh-CN.md') -Force

# Windows PowerShell 5.1 parses BOM-less UTF-8 source files through the active
# ANSI code page.  The customer shortcuts deliberately use powershell.exe, so
# package every executable script as UTF-8 with BOM before hashing the package.
$utf8Bom = [Text.UTF8Encoding]::new($true)
foreach ($scriptFile in @(Get-ChildItem -LiteralPath $stage -File -Filter '*.ps1')) {
    $scriptText = [IO.File]::ReadAllText($scriptFile.FullName, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($scriptFile.FullName, $scriptText, $utf8Bom)
}

$stagePrefix = [IO.Path]::GetFullPath($stage).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object Name -ne 'package-manifest.json' | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = ([IO.Path]::GetFullPath($_.FullName).Substring($stagePrefix.Length)).Replace('\','/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$digestSource = ($files | ForEach-Object { "$($_.path)|$($_.bytes)|$($_.sha256)" }) -join "`n"
$digestBytes = [Text.Encoding]::UTF8.GetBytes($digestSource)
$sha = [Security.Cryptography.SHA256]::Create()
$packageDigest = ([BitConverter]::ToString($sha.ComputeHash($digestBytes))).Replace('-','').ToLowerInvariant()
$manifest = [ordered]@{
    schema = 'haiyu-directional-compare-package/v2'
    version = $Version
    runtime = $Runtime
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    packageDigest = $packageDigest
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
        rule = 'read-only metadata scan and selected tensor-range sampling; original model bytes are never rewritten'
    }
    files = $files
    boundaries = [ordered]@{
        adminRequired = $false
        networkUsed = $false
        fullWeightLoaded = $false
        gpuUsed = $false
        originalWeightsModified = $false
        semanticAuthority = $false
        automaticModelBinding = $true
        activeProcessPathPriority = $true
        perModelCausalMaps = $true
        oneClickDiscoveryAdaptationAndReadiness = $true
        accelerationEnabledOnlyAfterEquivalentOutput = $true
        accelerationEnabledOnlyAfterObservedWallClockGain = $true
        originalComparisonPreservedOnGateFailure = $true
        ambiguousSelectionReported = $true
        directedPayloadSampling = $true
        selectedPositionWallClockAB = $true
        fullSelectedTensorTraversedByBaseline = $true
        robustnessAudit = $true
        multiScaleDeterminism = $true
        precisionCoverage = $true
        inMemoryNoiseOnly = $true
        layerCoverage = $true
        faultClosedTests = $true
        runtimeComparisonOrgan = $true
        runtimeReadinessIdentityGate = $true
        runtimeBaselineFallback = $true
        runtimeSourceSnapshotVerification = $true
        quantizedGgufPayloadDecoding = $false
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding UTF8

$zip = "$stage.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
[ordered]@{
    package = $zip
    packageBytes = (Get-Item -LiteralPath $zip).Length
    packageSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    stage = $stage
    packageDigest = $packageDigest
    files = $files.Count
} | ConvertTo-Json -Depth 5
