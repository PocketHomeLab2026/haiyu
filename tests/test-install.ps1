[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

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

$zip = (Resolve-Path -LiteralPath $ZipPath).Path
$models = (Resolve-Path -LiteralPath $ModelRoot).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$artifactRoot = Split-Path -Parent $output
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$runId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$extractRoot = Join-Path $artifactRoot "package-extract-$runId"
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$installRoot = Join-Path $localAppData "Haiyu\DirectionalCompare-Acceptance-$runId"
Assert-PathUnderRoot -Path $extractRoot -Root $artifactRoot
Assert-PathUnderRoot -Path $installRoot -Root $localAppData

$receipt = [ordered]@{
    schema = 'haiyu-directional-compare-install-acceptance/v1'
    testedAt = [DateTimeOffset]::Now.ToString('o')
    zipPath = $zip
    zipSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    modelRoot = $models
    ownerTaiji = [ordered]@{
        present = $true
        kind = 'local-owner-consent-boundary'
    }
    extracted = $false
    installed = $false
    selfTestPassed = 0
    selfTestTotal = 0
    scan = $null
    adaptation = $null
    binding = $null
    readiness = $null
    runtimeOrgan = $null
    runtimeFallback = $null
    benchmark = $null
    directionalAb = $null
    robustness = $null
    directedPayload = $null
    uninstalled = $false
    installRootRemoved = $false
    originalWeightsModified = $false
    fullWeightLoaded = $false
    gpuUsed = $false
    networkUsed = $false
    failures = @()
}

try {
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $extractRoot -Force
    $receipt.extracted = $true

    $rootInstallScript = Join-Path $extractRoot 'install.ps1'
    if (Test-Path -LiteralPath $rootInstallScript) {
        $packageRootPath = $extractRoot
    }
    else {
        $packageRoot = Get-ChildItem -LiteralPath $extractRoot -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'install.ps1') } |
            Select-Object -First 1
        if ($null -eq $packageRoot) {
            throw 'ZIP 中未找到 install.ps1。'
        }
        $packageRootPath = $packageRoot.FullName
    }

    $installScript = Join-Path $packageRootPath 'install.ps1'
    & $installScript -InstallRoot $installRoot -SkipInitialScan -NoShortcuts
    $receipt.installed = $true

    $exe = Join-Path $installRoot 'app\haiyu-directional-compare.exe'
    $selfTestRaw = & $exe self-test
    if ($LASTEXITCODE -ne 0) {
        throw "安装后自检失败，退出码：$LASTEXITCODE"
    }
    $selfTest = ($selfTestRaw -join [Environment]::NewLine) | ConvertFrom-Json
    $receipt.selfTestPassed = [int]$selfTest.passed
    $receipt.selfTestTotal = [int]$selfTest.total

    $prepareScript = Join-Path $installRoot 'app\prepare-client.ps1'
    & $prepareScript -InstallRoot $installRoot -AdditionalRoot $models -MaxPairs 1 -SampleElements 4096 -Iterations 3 -RequireReady
    if ($LASTEXITCODE -ne 0) {
        throw "安装后一键发现、适配与验收失败，退出码：$LASTEXITCODE"
    }

    $scanManifestPath = Join-Path $installRoot 'data\maps\scan-manifest.json'
    $scan = Get-Content -LiteralPath $scanManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.scan = [ordered]@{
        discoveredModels = [int]$scan.modelCount
        supportedModels = [int]$scan.supportedModelCount
        unsupportedModels = [int]$scan.failClosedModelCount
        totalTensors = [int]$scan.tensorCount
        mappedTensors = [int]$scan.standardRoleMappedTensorCount
        headerBytesRead = [long]$scan.headerBytesRead
        weightPayloadBytesRead = [long]$scan.weightPayloadBytesRead
        elapsedMilliseconds = [double]$scan.elapsedMilliseconds
        ownerTaijiPresent = [bool]$scan.ownerTaiji.present
    }

    $adaptationPath = Join-Path $installRoot 'data\customer-adaptation-report.json'
    if (-not (Test-Path -LiteralPath $adaptationPath)) {
        throw '安装后未生成客户模型适配报告。'
    }
    $adaptation = Get-Content -LiteralPath $adaptationPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.adaptation = [ordered]@{
        discoveredModels = [int]$adaptation.discoveredModelCount
        supportedModels = [int]$adaptation.supportedModelCount
        structuralDirectionReadyModels = [int]$adaptation.structuralDirectionReadyModelCount
        recognizedButNoRouteModels = [int]$adaptation.recognizedButNoRouteModelCount
        failClosedModels = [int]$adaptation.failClosedModelCount
        directedNumericReadyModels = [int]$adaptation.directedNumericReadyModelCount
        weightPayloadBytesRead = [long]$adaptation.weightPayloadBytesReadDuringAdaptation
        ownerTaijiPresent = [bool]$adaptation.ownerTaiji.present
        fullWeightLoaded = [bool]$adaptation.fullWeightLoaded
        gpuUsed = [bool]$adaptation.gpuUsed
        networkUsed = [bool]$adaptation.networkUsed
        originalWeightsModified = [bool]$adaptation.originalWeightsModified
    }
    if ($receipt.adaptation.discoveredModels -ne $receipt.scan.discoveredModels) {
        throw '适配报告与扫描清单的模型数量不一致。'
    }
    if ($receipt.adaptation.supportedModels -ne $receipt.scan.supportedModels) {
        throw '适配报告与扫描清单的支持模型数量不一致。'
    }

    $bindingPath = Join-Path $installRoot 'data\customer-model-binding.json'
    if (-not (Test-Path -LiteralPath $bindingPath)) {
        throw '安装后未生成客户模型绑定报告。'
    }
    $binding = Get-Content -LiteralPath $bindingPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $boundMap = [string]$binding.selected.mapFile
    if ([string]$binding.status -ne 'bound' -or [string]::IsNullOrWhiteSpace([string]$binding.selected.modelId)) {
        throw '安装后没有绑定可用模型。'
    }
    Assert-PathUnderRoot -Path $boundMap -Root (Join-Path $installRoot 'data\maps')
    if (-not (Test-Path -LiteralPath $boundMap)) {
        throw '绑定报告指向的因果位图不存在。'
    }
    $receipt.binding = [ordered]@{
        status = [string]$binding.status
        selectedModelId = [string]$binding.selected.modelId
        adapterId = [string]$binding.selected.adapterId
        strategy = [string]$binding.selectionStrategy
        confidence = [string]$binding.selectionConfidence
        selectedMapFile = $boundMap
        structuralRoute = $binding.selected.structuralRoute
        compatibleCandidates = [int]$binding.compatibleCandidateCount
        ownerTaijiPresent = [bool]$binding.ownerTaiji.present
        fullWeightLoaded = [bool]$binding.fullWeightLoaded
        weightPayloadBytesRead = [long]$binding.weightPayloadBytesRead
        gpuUsed = [bool]$binding.gpuUsed
        networkUsed = [bool]$binding.networkUsed
        originalWeightsModified = [bool]$binding.originalWeightsModified
    }

    $readinessPath = Join-Path $installRoot 'data\customer-deployment-readiness.json'
    if (-not (Test-Path -LiteralPath $readinessPath)) {
        throw '安装后未生成一键部署就绪报告。'
    }
    $readiness = Get-Content -LiteralPath $readinessPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.readiness = [ordered]@{
        status = [string]$readiness.status
        safeToEnableDirectionalComparison = [bool]$readiness.safeToEnableDirectionalComparison
        selectedModelId = [string]$readiness.binding.modelId
        adapterId = [string]$readiness.binding.adapterId
        structuralPassed = [bool]$readiness.structuralVerification.passed
        numericAttempted = [bool]$readiness.selectedPositionAb.attempted
        numericPassed = [bool]$readiness.selectedPositionAb.passed
        outputsEquivalent = [bool]$readiness.selectedPositionAb.outputsEquivalent
        fullSelectedTensorTraversedByBaseline = [bool]$readiness.selectedPositionAb.fullSelectedTensorTraversedByBaseline
        baselineBytesRead = [long]$readiness.selectedPositionAb.baselineBytesRead
        directionalBytesRead = [long]$readiness.selectedPositionAb.directionalBytesRead
        byteAvoidanceWithinSelectedRoute = [double]$readiness.selectedPositionAb.byteAvoidanceWithinSelectedRoute
        extractionSpeedRatio = [double]$readiness.selectedPositionAb.extractionSpeedRatio
        accelerationObserved = [bool]$readiness.selectedPositionAb.accelerationObserved
        ownerTaijiPresent = [bool]$readiness.ownerTaiji.present
        readOnly = [bool]$readiness.readOnly
        fullWeightLoaded = [bool]$readiness.fullWeightLoaded
        gpuUsed = [bool]$readiness.gpuUsed
        networkUsed = [bool]$readiness.networkUsed
        originalWeightsModified = [bool]$readiness.originalWeightsModified
    }
    if ($receipt.readiness.selectedModelId -ne $receipt.binding.selectedModelId) {
        throw '一键部署就绪报告与绑定报告的模型不一致。'
    }

    $runtimeScript = Join-Path $installRoot 'app\runtime-compare-client.ps1'
    if (-not (Test-Path -LiteralPath $runtimeScript)) {
        throw '安装后缺少运行时定向对比与自动回退入口。'
    }
    $runtimeNormalPath = Join-Path $installRoot 'data\runtime-receipts\acceptance-runtime-normal.json'
    $runtimeNormalRaw = @(& $runtimeScript -InstallRoot $installRoot -MaxPairs 1 -SampleElements 4096 -VerifyBaseline -AllowBaselineFallback $true -RequestId 'acceptance-runtime-normal' -OutputPath $runtimeNormalPath)
    if ($LASTEXITCODE -ne 0) {
        throw "安装后运行时定向路径失败，退出码：$LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $runtimeNormalPath)) {
        throw '安装后运行时定向路径没有生成回执。'
    }
    $runtimeNormal = Get-Content -LiteralPath $runtimeNormalPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.runtimeOrgan = [ordered]@{
        supported = [bool]$runtimeNormal.supported
        readinessGatePassed = [bool]$runtimeNormal.readinessGatePassed
        modelId = [string]$runtimeNormal.modelId
        adapterId = [string]$runtimeNormal.adapterId
        mode = [string]$runtimeNormal.mode
        status = [string]$runtimeNormal.status
        baselineVerifiedThisRequest = [bool]$runtimeNormal.baselineVerifiedThisRequest
        pairCount = [int]$runtimeNormal.pairCount
        sampledValues = [int]$runtimeNormal.sampledValues
        baselineBytesRead = [long]$runtimeNormal.baselineBytesRead
        directionalBytesRead = [long]$runtimeNormal.directionalBytesRead
        byteAvoidanceWithinSelectedRoute = [double]$runtimeNormal.byteAvoidanceWithinSelectedRoute
        directionalOutputSha256 = [string]$runtimeNormal.directionalOutputSha256
        baselineOutputSha256 = [string]$runtimeNormal.baselineOutputSha256
        resultOutputSha256 = [string]$runtimeNormal.resultOutputSha256
        sourceSnapshotsUnchanged = [bool]$runtimeNormal.sourceSnapshotsUnchanged
        ownerTaijiPresent = [bool]$runtimeNormal.ownerTaiji.present
        readOnly = [bool]$runtimeNormal.readOnly
        fullWeightLoaded = [bool]$runtimeNormal.fullWeightLoaded
        gpuUsed = [bool]$runtimeNormal.gpuUsed
        networkUsed = [bool]$runtimeNormal.networkUsed
        originalWeightsModified = [bool]$runtimeNormal.originalWeightsModified
        authority = [string]$runtimeNormal.authority
    }
    if ($receipt.runtimeOrgan.modelId -ne $receipt.binding.selectedModelId -or $receipt.runtimeOrgan.adapterId -ne $receipt.binding.adapterId) {
        throw '运行时器官与客户模型绑定身份不一致。'
    }
    if (-not $receipt.runtimeOrgan.supported -or -not $receipt.runtimeOrgan.readinessGatePassed -or $receipt.runtimeOrgan.mode -ne 'directional' -or $receipt.runtimeOrgan.status -ne 'ok-directional-baseline-verified' -or -not $receipt.runtimeOrgan.baselineVerifiedThisRequest -or $receipt.runtimeOrgan.directionalOutputSha256 -ne $receipt.runtimeOrgan.baselineOutputSha256 -or $receipt.runtimeOrgan.resultOutputSha256 -ne $receipt.runtimeOrgan.directionalOutputSha256) {
        throw '运行时定向路径未通过同请求原顺序读取复核。'
    }

    $runtimeFallbackPath = Join-Path $installRoot 'data\runtime-receipts\acceptance-runtime-fallback.json'
    $runtimeFallbackRaw = @(& $runtimeScript -InstallRoot $installRoot -MaxPairs 1 -SampleElements 4096 -AllowBaselineFallback $true -RequestId 'acceptance-runtime-fallback' -InjectFault 'directional-error' -OutputPath $runtimeFallbackPath)
    if ($LASTEXITCODE -ne 0) {
        throw "安装后运行时故障回退路径失败，退出码：$LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $runtimeFallbackPath)) {
        throw '安装后运行时故障回退路径没有生成回执。'
    }
    $runtimeFallback = Get-Content -LiteralPath $runtimeFallbackPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.runtimeFallback = [ordered]@{
        supported = [bool]$runtimeFallback.supported
        readinessGatePassed = [bool]$runtimeFallback.readinessGatePassed
        modelId = [string]$runtimeFallback.modelId
        adapterId = [string]$runtimeFallback.adapterId
        mode = [string]$runtimeFallback.mode
        status = [string]$runtimeFallback.status
        fallbackTriggered = [bool]$runtimeFallback.fallbackTriggered
        fallbackReason = [string]$runtimeFallback.fallbackReason
        pairCount = [int]$runtimeFallback.pairCount
        sampledValues = [int]$runtimeFallback.sampledValues
        baselineBytesRead = [long]$runtimeFallback.baselineBytesRead
        baselineOutputSha256 = [string]$runtimeFallback.baselineOutputSha256
        resultOutputSha256 = [string]$runtimeFallback.resultOutputSha256
        sourceSnapshotsUnchanged = [bool]$runtimeFallback.sourceSnapshotsUnchanged
        ownerTaijiPresent = [bool]$runtimeFallback.ownerTaiji.present
        readOnly = [bool]$runtimeFallback.readOnly
        fullWeightLoaded = [bool]$runtimeFallback.fullWeightLoaded
        gpuUsed = [bool]$runtimeFallback.gpuUsed
        networkUsed = [bool]$runtimeFallback.networkUsed
        originalWeightsModified = [bool]$runtimeFallback.originalWeightsModified
        authority = [string]$runtimeFallback.authority
    }
    if (-not $receipt.runtimeFallback.supported -or -not $receipt.runtimeFallback.readinessGatePassed -or $receipt.runtimeFallback.mode -ne 'baseline-fallback' -or $receipt.runtimeFallback.status -ne 'ok-baseline-fallback' -or -not $receipt.runtimeFallback.fallbackTriggered -or [string]::IsNullOrWhiteSpace($receipt.runtimeFallback.fallbackReason) -or $receipt.runtimeFallback.resultOutputSha256 -ne $receipt.runtimeFallback.baselineOutputSha256) {
        throw '运行时定向读取故障没有正确回退到原顺序读取。'
    }

    $benchmarkScript = Join-Path $installRoot 'app\benchmark-client.ps1'
    & $benchmarkScript -InstallRoot $installRoot -MaxPairs 1 -SampleElements 4096
    if ($LASTEXITCODE -ne 0) {
        throw "安装后客户机定向验收失败，退出码：$LASTEXITCODE"
    }
    $benchmarkPath = Join-Path $installRoot 'data\customer-benchmark-report.json'
    if (-not (Test-Path -LiteralPath $benchmarkPath)) {
        throw '安装后未生成客户机资源与定向验收报告。'
    }
    $benchmark = Get-Content -LiteralPath $benchmarkPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.benchmark = [ordered]@{
        structuralReadyModels = [int]$benchmark.structuralDirectionReadyModelCount
        structuralPassedModels = [int]$benchmark.structuralPassedModelCount
        numericReadyModels = [int]$benchmark.directedNumericReadyModelCount
        numericPassedModels = [int]$benchmark.directedNumericPassedModelCount
        numericEvidenceAvailable = [bool]$benchmark.numericEvidenceAvailable
        totalModelPayloadBytes = [long]$benchmark.totalModelPayloadBytes
        selectedRoutePayloadBytes = [long]$benchmark.selectedRoutePayloadBytes
        actualPayloadBytesRead = [long]$benchmark.actualPayloadBytesRead
        weightedEndToEndByteAvoidance = if ($null -eq $benchmark.weightedEndToEndByteAvoidance) { $null } else { [double]$benchmark.weightedEndToEndByteAvoidance }
        ownerTaijiPresent = [bool]$benchmark.ownerTaiji.present
        readOnly = [bool]$benchmark.readOnly
        fullWeightLoaded = [bool]$benchmark.fullWeightLoaded
        gpuUsed = [bool]$benchmark.gpuUsed
        networkUsed = [bool]$benchmark.networkUsed
        originalWeightsModified = [bool]$benchmark.originalWeightsModified
        endToEndSpeedupClaimed = [bool]$benchmark.endToEndSpeedupClaimed
    }
    if ($receipt.benchmark.structuralReadyModels -ne $receipt.adaptation.structuralDirectionReadyModels) {
        throw '客户机验收报告与适配报告的结构定向就绪数量不一致。'
    }
    if ($receipt.benchmark.numericReadyModels -ne $receipt.adaptation.directedNumericReadyModels) {
        throw '客户机验收报告与适配报告的数值定向就绪数量不一致。'
    }

    $abScript = Join-Path $installRoot 'app\ab-benchmark-client.ps1'
    & $abScript -InstallRoot $installRoot -MaxModels 2 -MaxPairs 1 -SampleElements 4096 -Iterations 2 -Force
    if ($LASTEXITCODE -ne 0) {
        throw "安装后原始读取与定向读取 A/B 验收失败，退出码：$LASTEXITCODE"
    }
    $abPath = Join-Path $installRoot 'data\customer-directional-ab-report.json'
    if (-not (Test-Path -LiteralPath $abPath)) {
        throw '安装后未生成原始读取与定向读取 A/B 报告。'
    }
    $ab = Get-Content -LiteralPath $abPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.directionalAb = [ordered]@{
        attemptedModels = [int]$ab.attemptedModelCount
        passedModels = [int]$ab.passedModelCount
        allOutputsEquivalent = [bool]$ab.allOutputsEquivalent
        fullSelectedTensorTraversedByBaseline = [bool]$ab.fullSelectedTensorTraversedByBaseline
        baselineBytesRead = [long]$ab.baselineBytesRead
        directionalBytesRead = [long]$ab.directionalBytesRead
        weightedByteAvoidanceWithinSelectedRoute = [double]$ab.weightedByteAvoidanceWithinSelectedRoute
        aggregateMedianBaselineMilliseconds = [double]$ab.aggregateMedianBaselineMilliseconds
        aggregateMedianDirectionalMilliseconds = [double]$ab.aggregateMedianDirectionalMilliseconds
        aggregateExtractionSpeedRatio = [double]$ab.aggregateExtractionSpeedRatio
        observedDirectionalExtractionSpeedup = [bool]$ab.observedDirectionalExtractionSpeedup
        ownerTaijiPresent = [bool]$ab.ownerTaiji.present
        readOnly = [bool]$ab.readOnly
        fullWeightLoaded = [bool]$ab.fullWeightLoaded
        gpuUsed = [bool]$ab.gpuUsed
        networkUsed = [bool]$ab.networkUsed
        originalWeightsModified = [bool]$ab.originalWeightsModified
    }
    if ($receipt.directionalAb.attemptedModels -le 0) {
        throw '安装后没有找到可执行 A/B 的真实数值模型。'
    }

    $robustnessScript = Join-Path $installRoot 'app\robustness-audit-client.ps1'
    & $robustnessScript -InstallRoot $installRoot -MaxModels 2 -MaxPairs 1 -SampleScales '128,1024,4096' -NoiseLevels '0.000001,0.0001,0.01' -Force
    if ($LASTEXITCODE -ne 0) {
        throw "安装后生产稳健性验收失败，退出码：$LASTEXITCODE"
    }
    $robustnessPath = Join-Path $installRoot 'data\customer-robustness-audit-report.json'
    if (-not (Test-Path -LiteralPath $robustnessPath)) {
        throw '安装后未生成生产稳健性验收报告。'
    }
    $robustness = Get-Content -LiteralPath $robustnessPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $receipt.robustness = [ordered]@{
        selectedModels = [int]$robustness.selectedModelCount
        attemptedModels = [int]$robustness.attemptedModelCount
        passedModels = [int]$robustness.passedModelCount
        uncoveredModels = [int]$robustness.uncoveredModelCount
        allSelectedModelsCovered = [bool]$robustness.allSelectedModelsCovered
        allAttemptedModelsPassed = [bool]$robustness.allAttemptedModelsPassed
        passed = [bool]$robustness.passed
        ownerTaijiPresent = [bool]$robustness.ownerTaiji.present
        readOnly = [bool]$robustness.readOnly
        fullWeightLoaded = [bool]$robustness.fullWeightLoaded
        gpuUsed = [bool]$robustness.gpuUsed
        networkUsed = [bool]$robustness.networkUsed
        originalWeightsModified = [bool]$robustness.originalWeightsModified
        noiseAppliedInMemoryOnly = [bool]$robustness.noiseAppliedInMemoryOnly
    }
    if ($receipt.robustness.selectedModels -le 0) {
        throw '安装后没有找到可执行生产稳健性验收的真实数值模型。'
    }

    foreach ($mapFile in @(Get-ChildItem -LiteralPath (Join-Path $installRoot 'data\maps') -Filter '*.causal-position-map.json' -File | Sort-Object Name)) {
        $candidate = Get-Content -LiteralPath $mapFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $readable = @($candidate.tensors |
            Where-Object { $_.storageFormat -eq 'safetensors' -and [long]$_.payloadBytes -gt 0 -and [long]$_.directionRank -ge 0 } |
            Sort-Object @{Expression={[long]$_.directionRank}}, name)
        if ($readable.Count -lt 2) { continue }
        $measurePath = Join-Path $installRoot 'data\maps\acceptance-directed-payload.json'
        $measureRaw = & $exe measure --map $mapFile.FullName --from $readable[0].name --to $readable[-1].name --max-pairs 1 --sample-elements 4096 --output $measurePath
        if ($LASTEXITCODE -ne 0) { continue }
        $measure = ($measureRaw -join [Environment]::NewLine) | ConvertFrom-Json
        $receipt.directedPayload = [ordered]@{
            modelId = $measure.modelId
            supported = [bool]$measure.supported
            pairCount = [int]$measure.pairCount
            totalModelPayloadBytes = [long]$measure.totalModelPayloadBytes
            selectedRoutePayloadBytes = [long]$measure.selectedRoutePayloadBytes
            actualPayloadBytesRead = [long]$measure.actualPayloadBytesRead
            endToEndByteAvoidance = [double]$measure.endToEndByteAvoidance
            readOnly = [bool]$measure.readOnly
            ownerTaijiPresent = [bool]$measure.ownerTaiji.present
        }
        break
    }
    if ($null -eq $receipt.directedPayload) {
        throw '安装后未找到可执行真实定向浮点测量的模型。'
    }

    $evidenceRoot = Join-Path $artifactRoot "acceptance-evidence-$runId"
    New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $installRoot 'install-receipt.json') -Destination (Join-Path $evidenceRoot 'install-receipt.json') -Force
    Copy-Item -LiteralPath $scanManifestPath -Destination (Join-Path $evidenceRoot 'scan-manifest.json') -Force
    Copy-Item -LiteralPath $adaptationPath -Destination (Join-Path $evidenceRoot 'customer-adaptation-report.json') -Force
    Copy-Item -LiteralPath $bindingPath -Destination (Join-Path $evidenceRoot 'customer-model-binding.json') -Force
    Copy-Item -LiteralPath $readinessPath -Destination (Join-Path $evidenceRoot 'customer-deployment-readiness.json') -Force
    Copy-Item -LiteralPath $runtimeNormalPath -Destination (Join-Path $evidenceRoot 'acceptance-runtime-normal.json') -Force
    Copy-Item -LiteralPath $runtimeFallbackPath -Destination (Join-Path $evidenceRoot 'acceptance-runtime-fallback.json') -Force
    Copy-Item -LiteralPath $benchmarkPath -Destination (Join-Path $evidenceRoot 'customer-benchmark-report.json') -Force
    Copy-Item -LiteralPath $abPath -Destination (Join-Path $evidenceRoot 'customer-directional-ab-report.json') -Force
    Copy-Item -LiteralPath $robustnessPath -Destination (Join-Path $evidenceRoot 'customer-robustness-audit-report.json') -Force

    $uninstallScript = Join-Path $installRoot 'app\uninstall.ps1'
    & $uninstallScript -InstallRoot $installRoot
    $receipt.uninstalled = $true
    $receipt.installRootRemoved = -not (Test-Path -LiteralPath $installRoot)
    if (-not $receipt.installRootRemoved) {
        throw '卸载后安装目录仍然存在。'
    }
}
catch {
    $receipt.failures += $_.Exception.Message
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Assert-PathUnderRoot -Path $extractRoot -Root $artifactRoot
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $installRoot) {
        Assert-PathUnderRoot -Path $installRoot -Root $localAppData
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }

    $runtimeOrganOk = ($null -ne $receipt.runtimeOrgan -and $receipt.runtimeOrgan.supported -and $receipt.runtimeOrgan.readinessGatePassed -and $receipt.runtimeOrgan.mode -eq 'directional' -and $receipt.runtimeOrgan.status -eq 'ok-directional-baseline-verified' -and $receipt.runtimeOrgan.baselineVerifiedThisRequest -and $receipt.runtimeOrgan.pairCount -gt 0 -and $receipt.runtimeOrgan.sampledValues -gt 0 -and $receipt.runtimeOrgan.baselineBytesRead -gt 0 -and $receipt.runtimeOrgan.directionalBytesRead -gt 0 -and $receipt.runtimeOrgan.directionalBytesRead -lt $receipt.runtimeOrgan.baselineBytesRead -and $receipt.runtimeOrgan.directionalOutputSha256 -eq $receipt.runtimeOrgan.baselineOutputSha256 -and $receipt.runtimeOrgan.resultOutputSha256 -eq $receipt.runtimeOrgan.directionalOutputSha256 -and $receipt.runtimeOrgan.sourceSnapshotsUnchanged -and $receipt.runtimeOrgan.ownerTaijiPresent -and $receipt.runtimeOrgan.readOnly -and -not $receipt.runtimeOrgan.fullWeightLoaded -and -not $receipt.runtimeOrgan.gpuUsed -and -not $receipt.runtimeOrgan.networkUsed -and -not $receipt.runtimeOrgan.originalWeightsModified)
    $runtimeFallbackOk = ($null -ne $receipt.runtimeFallback -and $receipt.runtimeFallback.supported -and $receipt.runtimeFallback.readinessGatePassed -and $receipt.runtimeFallback.mode -eq 'baseline-fallback' -and $receipt.runtimeFallback.status -eq 'ok-baseline-fallback' -and $receipt.runtimeFallback.fallbackTriggered -and -not [string]::IsNullOrWhiteSpace($receipt.runtimeFallback.fallbackReason) -and $receipt.runtimeFallback.pairCount -gt 0 -and $receipt.runtimeFallback.sampledValues -gt 0 -and $receipt.runtimeFallback.baselineBytesRead -gt 0 -and $receipt.runtimeFallback.resultOutputSha256 -eq $receipt.runtimeFallback.baselineOutputSha256 -and $receipt.runtimeFallback.sourceSnapshotsUnchanged -and $receipt.runtimeFallback.ownerTaijiPresent -and $receipt.runtimeFallback.readOnly -and -not $receipt.runtimeFallback.fullWeightLoaded -and -not $receipt.runtimeFallback.gpuUsed -and -not $receipt.runtimeFallback.networkUsed -and -not $receipt.runtimeFallback.originalWeightsModified)
    $receipt.ok = ($receipt.failures.Count -eq 0 -and $receipt.extracted -and $receipt.installed -and $receipt.selfTestPassed -eq $receipt.selfTestTotal -and $null -ne $receipt.scan -and $receipt.scan.supportedModels -gt 0 -and $receipt.scan.weightPayloadBytesRead -eq 0 -and $null -ne $receipt.adaptation -and $receipt.adaptation.supportedModels -gt 0 -and $receipt.adaptation.structuralDirectionReadyModels -gt 0 -and $receipt.adaptation.directedNumericReadyModels -gt 0 -and $receipt.adaptation.weightPayloadBytesRead -eq 0 -and $receipt.adaptation.ownerTaijiPresent -and -not $receipt.adaptation.fullWeightLoaded -and -not $receipt.adaptation.gpuUsed -and -not $receipt.adaptation.networkUsed -and -not $receipt.adaptation.originalWeightsModified -and $null -ne $receipt.binding -and $receipt.binding.status -eq 'bound' -and $receipt.binding.compatibleCandidates -gt 0 -and $receipt.binding.ownerTaijiPresent -and $receipt.binding.weightPayloadBytesRead -eq 0 -and -not $receipt.binding.fullWeightLoaded -and -not $receipt.binding.gpuUsed -and -not $receipt.binding.networkUsed -and -not $receipt.binding.originalWeightsModified -and $null -ne $receipt.readiness -and $receipt.readiness.status -eq 'ready-selected-position-acceleration' -and $receipt.readiness.safeToEnableDirectionalComparison -and $receipt.readiness.structuralPassed -and $receipt.readiness.numericAttempted -and $receipt.readiness.numericPassed -and $receipt.readiness.outputsEquivalent -and $receipt.readiness.fullSelectedTensorTraversedByBaseline -and $receipt.readiness.baselineBytesRead -gt 0 -and $receipt.readiness.directionalBytesRead -gt 0 -and $receipt.readiness.directionalBytesRead -lt $receipt.readiness.baselineBytesRead -and $receipt.readiness.extractionSpeedRatio -gt 1.0 -and $receipt.readiness.accelerationObserved -and $receipt.readiness.ownerTaijiPresent -and $receipt.readiness.readOnly -and -not $receipt.readiness.fullWeightLoaded -and -not $receipt.readiness.gpuUsed -and -not $receipt.readiness.networkUsed -and -not $receipt.readiness.originalWeightsModified -and $runtimeOrganOk -and $runtimeFallbackOk -and $null -ne $receipt.benchmark -and $receipt.benchmark.structuralPassedModels -eq $receipt.benchmark.structuralReadyModels -and $receipt.benchmark.numericPassedModels -eq $receipt.benchmark.numericReadyModels -and $receipt.benchmark.numericEvidenceAvailable -and $receipt.benchmark.actualPayloadBytesRead -gt 0 -and $receipt.benchmark.actualPayloadBytesRead -lt $receipt.benchmark.totalModelPayloadBytes -and $receipt.benchmark.ownerTaijiPresent -and $receipt.benchmark.readOnly -and -not $receipt.benchmark.fullWeightLoaded -and -not $receipt.benchmark.gpuUsed -and -not $receipt.benchmark.networkUsed -and -not $receipt.benchmark.originalWeightsModified -and -not $receipt.benchmark.endToEndSpeedupClaimed -and $null -ne $receipt.directionalAb -and $receipt.directionalAb.attemptedModels -gt 0 -and $receipt.directionalAb.passedModels -eq $receipt.directionalAb.attemptedModels -and $receipt.directionalAb.allOutputsEquivalent -and $receipt.directionalAb.fullSelectedTensorTraversedByBaseline -and $receipt.directionalAb.baselineBytesRead -gt 0 -and $receipt.directionalAb.directionalBytesRead -gt 0 -and $receipt.directionalAb.directionalBytesRead -lt $receipt.directionalAb.baselineBytesRead -and $receipt.directionalAb.ownerTaijiPresent -and $receipt.directionalAb.readOnly -and -not $receipt.directionalAb.fullWeightLoaded -and -not $receipt.directionalAb.gpuUsed -and -not $receipt.directionalAb.networkUsed -and -not $receipt.directionalAb.originalWeightsModified -and $null -ne $receipt.robustness -and $receipt.robustness.selectedModels -gt 0 -and $receipt.robustness.attemptedModels -eq $receipt.robustness.selectedModels -and $receipt.robustness.passedModels -eq $receipt.robustness.attemptedModels -and $receipt.robustness.uncoveredModels -eq 0 -and $receipt.robustness.allSelectedModelsCovered -and $receipt.robustness.allAttemptedModelsPassed -and $receipt.robustness.passed -and $receipt.robustness.ownerTaijiPresent -and $receipt.robustness.readOnly -and -not $receipt.robustness.fullWeightLoaded -and -not $receipt.robustness.gpuUsed -and -not $receipt.robustness.networkUsed -and -not $receipt.robustness.originalWeightsModified -and $receipt.robustness.noiseAppliedInMemoryOnly -and $null -ne $receipt.directedPayload -and $receipt.directedPayload.supported -and $receipt.directedPayload.actualPayloadBytesRead -gt 0 -and $receipt.directedPayload.actualPayloadBytesRead -lt $receipt.directedPayload.totalModelPayloadBytes -and $receipt.uninstalled -and $receipt.installRootRemoved)
    $receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding UTF8
    $receipt | ConvertTo-Json -Depth 10
}

if (-not $receipt.ok) {
    exit 1
}
