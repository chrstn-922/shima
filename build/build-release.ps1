<#
.SYNOPSIS
Builds, packages (Sparse MSIX + Velopack/portable archives), optionally signs, and generates release manifests for win-x64 and win-arm64.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',

    [string]$Configuration = 'Release',

    [string[]]$Runtimes = @('win-x64', 'win-arm64'),

    [string]$Channel = 'stable',

    [string]$Publisher = 'CN=Shima',

    [string]$DownloadBaseUrl = 'https://releases.shima.local/v1.0.0',

    [switch]$SelfContained,

    [switch]$Sign,

    [string]$PfxPath,

    [string]$PfxPassword = $env:SHIMA_SIGN_PFX_PASSWORD,

    [string]$CertThumbprint,

    [switch]$SkipTimestamp,

    [switch]$RequireVelopack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-MakeAppxPath {
    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -Path $kitsRoot -Filter 'makeappx.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    $cmd = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }

    throw "MakeAppx.exe was not found. Install Windows 10/11 SDK."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Shima.App\Shima.App.csproj'
$bridgeProject = Join-Path $repoRoot 'src\Shima.AgentBridge\Shima.AgentBridge.csproj'
$sparseTemplateDir = Join-Path $repoRoot 'build\sparse-msix'
$registerScriptPath = Join-Path $repoRoot 'build\register-sparse-identity.ps1'
$signScriptPath = Join-Path $repoRoot 'build\sign-artifacts.ps1'
$releasesRoot = Join-Path $repoRoot 'artifacts\releases'
New-Item -ItemType Directory -Force -Path $releasesRoot | Out-Null

$makeAppx = Resolve-MakeAppxPath
$vpkCmd = Get-Command 'vpk' -ErrorAction SilentlyContinue
if ($RequireVelopack -and $null -eq $vpkCmd) {
    throw "Velopack CLI ('vpk') is not installed. Run 'dotnet tool install -g vpk' or omit -RequireVelopack."
}

$selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }
$versionParts = ($Version.Split('-')[0].Split('+')[0]).Split('.')
$fourPartVersion = switch ($versionParts.Count) {
    1 { "$($versionParts[0]).0.0.0" }
    2 { "$($versionParts[0]).$($versionParts[1]).0.0" }
    3 { "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0" }
    default { "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).$($versionParts[3])" }
}

$manifestPackages = @()

foreach ($rid in $Runtimes) {
    $msixArch = switch ($rid.ToLowerInvariant()) {
        'win-x64' { 'x64' }
        'win-arm64' { 'arm64' }
        default { throw "Unsupported target runtime '$rid'. Expected 'win-x64' or 'win-arm64'." }
    }

    Write-Host "=== Packaging Shima $Version for $rid ($msixArch) ==="

    $publishDir = Join-Path $repoRoot "artifacts\publish\$rid"
    $bridgeTempDir = Join-Path $publishDir 'bridge-tmp'
    $releaseDir = Join-Path $releasesRoot $rid
    $sparseStageDir = Join-Path $repoRoot "artifacts\sparse-stage\$rid"

    foreach ($dir in @($publishDir, $releaseDir, $sparseStageDir)) {
        if (Test-Path $dir) {
            Remove-Item -Path $dir -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    New-Item -ItemType Directory -Force -Path $bridgeTempDir | Out-Null

    Write-Host "Publishing Shima.AgentBridge ($rid)..."
    dotnet publish $bridgeProject `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContainedFlag `
        -p:Version=$Version `
        -o $bridgeTempDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for Shima.AgentBridge ($rid)."
    }

    Write-Host "Publishing Shima.App ($rid)..."
    dotnet publish $appProject `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContainedFlag `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for Shima.App ($rid)."
    }

    Copy-Item -Path (Join-Path $bridgeTempDir 'Shima.AgentBridge.*') -Destination $publishDir -Force
    Remove-Item -Path $bridgeTempDir -Recurse -Force

    if (-not (Test-Path (Join-Path $publishDir 'Shima.App.exe'))) {
        throw "Published output missing Shima.App.exe for $rid."
    }
    if (-not (Test-Path (Join-Path $publishDir 'Shima.AgentBridge.exe'))) {
        throw "Published output missing Shima.AgentBridge.exe for $rid."
    }

    $forbiddenDrawing = Get-ChildItem -Path $publishDir -Filter 'System.Drawing.Common*.dll' -Recurse -ErrorAction SilentlyContinue
    if ($null -ne $forbiddenDrawing) {
        throw "Forbidden System.Drawing.Common binary detected in publish output for $rid."
    }

    # Stage and pack Sparse MSIX Identity Package (strict validation; no /nv)
    Copy-Item -Path (Join-Path $sparseTemplateDir '*') -Destination $sparseStageDir -Recurse -Force
    $stagedManifestPath = Join-Path $sparseStageDir 'AppxManifest.xml'
    [xml]$manifestXml = Get-Content -Path $stagedManifestPath -Raw -Encoding UTF8
    $manifestXml.Package.Identity.ProcessorArchitecture = $msixArch
    $manifestXml.Package.Identity.Version = $fourPartVersion
    $manifestXml.Package.Identity.Publisher = $Publisher

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $xmlWriterSettings = New-Object System.Xml.XmlWriterSettings
    $xmlWriterSettings.Encoding = $utf8NoBom
    $xmlWriterSettings.Indent = $true
    $xmlWriter = [System.Xml.XmlWriter]::Create($stagedManifestPath, $xmlWriterSettings)
    try {
        $manifestXml.Save($xmlWriter)
    }
    finally {
        $xmlWriter.Dispose()
    }

    $msixPath = Join-Path $releaseDir "Shima.Identity-$rid.msix"
    $sparseValidateRoot = Join-Path $repoRoot 'artifacts\sparse-validate'
    $sparseValidateDir = Join-Path $sparseValidateRoot $rid
    $tempValidateMsix = Join-Path $sparseValidateRoot "validate-$rid.msix"

    try {
        if (Test-Path $sparseValidateDir) {
            Remove-Item -Path $sparseValidateDir -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $sparseValidateDir | Out-Null
        Copy-Item -Path (Join-Path $sparseStageDir '*') -Destination $sparseValidateDir -Recurse -Force
        Copy-Item -Path (Join-Path $publishDir 'Shima.App.exe') -Destination $sparseValidateDir -Force

        Write-Host "Validating Sparse MSIX manifest & assets (strict MakeAppx, without /nv) for $rid..."
        & $makeAppx pack /o /d $sparseValidateDir /p $tempValidateMsix
        if ($LASTEXITCODE -ne 0) {
            throw "Strict MakeAppx validation failed for $rid with exit code $LASTEXITCODE."
        }
    }
    finally {
        if (Test-Path $sparseValidateDir) {
            Remove-Item -Path $sparseValidateDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path $tempValidateMsix) {
            Remove-Item -Path $tempValidateMsix -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Packing lean external-location Sparse MSIX '$msixPath' (/nv per Microsoft Sparse Package docs)..."
    & $makeAppx pack /o /nv /d $sparseStageDir /p $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx sparse pack failed for $rid with exit code $LASTEXITCODE."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $msixZip = [System.IO.Compression.ZipFile]::OpenRead($msixPath)
    try {
        $entryNames = @($msixZip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($requiredEntry in @('AppxManifest.xml', 'Assets/StoreLogo.png', 'Assets/Square44x44Logo.png', 'Assets/Square150x150Logo.png')) {
            if ($entryNames -notcontains $requiredEntry) {
                throw "Sparse MSIX '$msixPath' missing required entry '$requiredEntry'."
            }
        }
        if ($entryNames -contains 'Shima.App.exe') {
            throw "Sparse MSIX '$msixPath' must remain external-location only and must not bundle Shima.App.exe."
        }
    }
    finally {
        $msixZip.Dispose()
    }

    $publishSparseDir = Join-Path $publishDir 'sparse-identity'
    New-Item -ItemType Directory -Force -Path $publishSparseDir | Out-Null
    Copy-Item -Path $msixPath -Destination $publishSparseDir -Force
    Copy-Item -Path $registerScriptPath -Destination $publishSparseDir -Force
    $bundledMsixPath = Join-Path $publishSparseDir (Split-Path -Leaf $msixPath)

    if ($Sign) {
        $signTargets = @(
            (Join-Path $publishDir 'Shima.App.exe'),
            (Join-Path $publishDir 'Shima.AgentBridge.exe'),
            $msixPath,
            $bundledMsixPath
        )
        & $signScriptPath -Paths $signTargets -PfxPath $PfxPath -PfxPassword $PfxPassword -CertThumbprint $CertThumbprint -SkipTimestamp:$SkipTimestamp
    }

    $primaryArtifactPath = $null
    if ($null -ne $vpkCmd) {
        Write-Host "Running Velopack pack for $rid..."
        & $vpkCmd.Source pack `
            --packId Shima `
            --packVersion $Version `
            --packDir $publishDir `
            --mainExe Shima.App.exe `
            --outputDir $releaseDir `
            --runtime $rid
        if ($LASTEXITCODE -ne 0) {
            throw "Velopack pack failed for $rid with exit code $LASTEXITCODE."
        }

        $setupExe = Get-ChildItem -Path $releaseDir -Filter '*-Setup.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $setupExe) {
            if ($Sign) {
                & $signScriptPath -Paths @($setupExe.FullName) -PfxPath $PfxPath -PfxPassword $PfxPassword -CertThumbprint $CertThumbprint -SkipTimestamp:$SkipTimestamp
            }
            $primaryArtifactPath = $setupExe.FullName
        }
    }

    if ([string]::IsNullOrWhiteSpace($primaryArtifactPath)) {
        if ($null -eq $vpkCmd) {
            Write-Warning "Velopack CLI ('vpk') not found in PATH; generating portable release archive alongside Sparse MSIX. Install via 'dotnet tool install -g vpk' to emit Setup.exe + delta .nupkg."
        }

        $zipPath = Join-Path $releaseDir "Shima-$Version-$rid.zip"
        Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
        $primaryArtifactPath = $zipPath
    }

    $fileItem = Get-Item $primaryArtifactPath
    $sha256 = (Get-FileHash -Path $primaryArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $baseUrl = $DownloadBaseUrl.TrimEnd('/')

    $manifestPackages += [ordered]@{
        architecture = $rid
        downloadUrl  = "$baseUrl/$($fileItem.Name)"
        sha256       = $sha256
        sizeBytes    = [int64]$fileItem.Length
        fileName     = $fileItem.Name
        msixFileName = (Split-Path -Leaf $msixPath)
        msixSha256   = (Get-FileHash -Path $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifestDocument = [ordered]@{
    version        = $Version
    channel        = $Channel
    publishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    releaseNotes   = "Shima $Version local-first release package."
    packages       = $manifestPackages
}

$manifestOutPath = Join-Path $releasesRoot "manifest-$Channel.json"
$manifestJson = $manifestDocument | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestOutPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Release manifest generated at '$manifestOutPath'."
