[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$PackageDirectory,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($PackageDirectory)
$manifestPath = Join-Path $directory 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'package-manifest.json missing' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$failures = @()
foreach ($entry in $manifest.files) {
    $path = Join-Path $directory $entry.path
    if (-not (Test-Path -LiteralPath $path)) {
        $failures += "missing:$($entry.path)"
        continue
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.sha256) { $failures += "digest:$($entry.path)" }
}
$exe = Join-Path $directory 'haiyu-directional-compare.exe'
$selfTestText = & $exe self-test | Out-String
$selfTest = $selfTestText | ConvertFrom-Json
if (-not $selfTest.ok) { $failures += 'self-test' }
$receipt = [ordered]@{
    schema = 'haiyu-directional-compare-package-verification/v1'
    ok = $failures.Count -eq 0
    failures = $failures
    version = $manifest.version
    packageDigest = $manifest.packageDigest
    manifestFileCount = $manifest.files.Count
    selfTestPassed = $selfTest.passed
    selfTestTotal = $selfTest.total
    ownerTaijiPresent = $selfTest.ownerTaiji.present
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    originalWeightsModified = $false
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
}
$receipt | ConvertTo-Json -Depth 6
if ($failures.Count -gt 0) { exit 1 }
