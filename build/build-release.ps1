<#
.SYNOPSIS
Builds, packages (Sparse MSIX + Inno Setup installers + Velopack/portable archives), optionally signs, and generates release manifests for win-x64 and win-arm64.
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

    [switch]$CreateDevSelfSignedCert,

    [switch]$SkipInstaller,

    [switch]$AutoInstallInnoSetup,

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

function Resolve-MakePriPath {
    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -Path $kitsRoot -Filter 'makepri.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\makepri\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    $cmd = Get-Command 'makepri.exe' -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }

    return $null
}

function Resolve-IsccPath {
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

function Install-InnoSetupWithWinget {
    $wingetCmd = Get-Command 'winget.exe' -ErrorAction SilentlyContinue
    if ($null -eq $wingetCmd) {
        Write-Warning "winget.exe was not found; cannot install Inno Setup 6 automatically."
        return $null
    }

    Write-Host "Installing Inno Setup 6 silently via winget..."
    & $wingetCmd.Source install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    $installExitCode = $LASTEXITCODE

    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($env:Path, $machinePath, $userPath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'

    $resolved = Resolve-IsccPath
    if ($null -eq $resolved -and $installExitCode -ne 0) {
        Write-Warning "winget exited with code $installExitCode while installing Inno Setup 6, and ISCC.exe was still not found."
    }

    return $resolved
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Shima.App\Shima.App.csproj'
$bridgeProject = Join-Path $repoRoot 'src\Shima.AgentBridge\Shima.AgentBridge.csproj'
$sparseTemplateDir = Join-Path $repoRoot 'build\sparse-msix'
$registerScriptPath = Join-Path $repoRoot 'build\register-sparse-identity.ps1'
$signScriptPath = Join-Path $repoRoot 'build\sign-artifacts.ps1'
$innoScriptPath = Join-Path $repoRoot 'build\installer\shima.iss'
$releasesRoot = Join-Path $repoRoot 'artifacts\releases'
New-Item -ItemType Directory -Force -Path $releasesRoot | Out-Null

$makeAppx = Resolve-MakeAppxPath
$makePri = Resolve-MakePriPath
if ($null -eq $makePri) {
    Write-Warning "MakePri.exe was not found in Windows 10/11 SDK or PATH; Sparse MSIX packages will be built without a generated resources.pri index."
}
$isccPath = $null
if (-not $SkipInstaller) {
    if (-not (Test-Path $innoScriptPath)) {
        Write-Warning "Inno Setup script '$innoScriptPath' was not found; skipping Shima-Setup installer compilation."
    }
    else {
        $isccPath = Resolve-IsccPath
        if ($null -eq $isccPath) {
            $shouldAutoInstall = $AutoInstallInnoSetup.IsPresent
            $isNonInteractiveHost = ($env:CI -eq 'true') -or ($env:GITHUB_ACTIONS -eq 'true') -or (@([Environment]::GetCommandLineArgs() -match '^-NonI').Count -gt 0)
            if (-not $shouldAutoInstall -and -not $isNonInteractiveHost -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
                if ($null -ne (Get-Command 'winget.exe' -ErrorAction SilentlyContinue)) {
                    try {
                        $reply = Read-Host "Inno Setup 6 (ISCC.exe) was not found. Install it silently via winget now? [y/N]"
                        $shouldAutoInstall = $reply -match '^(y|yes)$'
                    }
                    catch {
                        $shouldAutoInstall = $false
                    }
                }
            }

            if ($shouldAutoInstall) {
                $isccPath = Install-InnoSetupWithWinget
            }

            if ($null -eq $isccPath) {
                Write-Warning "Inno Setup compiler ('ISCC.exe') was not found in PATH or standard install locations. Portable .zip and Sparse MSIX artifacts will still be built; install Inno Setup 6 or pass -AutoInstallInnoSetup to build Shima-Setup installers."
            }
        }
    }
}
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

if ($CreateDevSelfSignedCert -and -not $Sign) {
    $Sign = $true
}

$createdDevCertThumbprint = $null
$createdDevPfxPath = $null
$allowUntrustedSelfSigned = $false

try {
    if ($Sign -and $CreateDevSelfSignedCert -and [string]::IsNullOrWhiteSpace($PfxPath) -and [string]::IsNullOrWhiteSpace($CertThumbprint)) {
        $devCertDir = Join-Path $repoRoot 'artifacts\dev-cert'
        New-Item -ItemType Directory -Force -Path $devCertDir | Out-Null
        $PfxPath = Join-Path $devCertDir 'shima-dev.pfx'
        $createdDevPfxPath = $PfxPath

        if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
            $PfxPassword = [Guid]::NewGuid().ToString('N')
        }

        $securePwd = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        $devCert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Publisher `
            -KeyUsage DigitalSignature `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(1)

        $createdDevCertThumbprint = $devCert.Thumbprint
        $allowUntrustedSelfSigned = $true
        Export-PfxCertificate -Cert $devCert -FilePath $PfxPath -Password $securePwd | Out-Null
        Write-Host "Generated session dev self-signed code-signing certificate ($Publisher, Thumbprint=$createdDevCertThumbprint)."
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
    $installerPublishDir = Join-Path $repoRoot "artifacts\publish-installer\$rid"
    $bridgeTempDir = Join-Path $repoRoot "artifacts\bridge-tmp\$rid"
    $releaseDir = Join-Path $releasesRoot $rid
    $sparseStageDir = Join-Path $repoRoot "artifacts\sparse-stage\$rid"
    $appObjRidDir = Join-Path $repoRoot "src\Shima.App\obj\$Configuration\net8.0-windows10.0.22621.0\$rid"

    foreach ($dir in @($publishDir, $installerPublishDir, $bridgeTempDir, $releaseDir, $sparseStageDir)) {
        if (Test-Path $dir) {
            Remove-Item -Path $dir -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

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

    foreach ($staleArtifact in @('apphost.exe', 'Shima.App.dll')) {
        $stalePath = Join-Path $appObjRidDir $staleArtifact
        if (Test-Path $stalePath) {
            Remove-Item -Path $stalePath -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Publishing portable unpackaged Shima.App ($rid, UseSparsePackageManifest=false)..."
    dotnet publish $appProject `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContainedFlag `
        -p:Version=$Version `
        -p:UseSparsePackageManifest=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for portable Shima.App ($rid)."
    }

    Copy-Item -Path (Join-Path $bridgeTempDir 'Shima.AgentBridge.*') -Destination $publishDir -Force

    $portableAppExe = Join-Path $publishDir 'Shima.App.exe'
    if (-not (Test-Path $portableAppExe)) {
        throw "Published portable output missing Shima.App.exe for $rid."
    }
    if (-not (Test-Path (Join-Path $publishDir 'Shima.AgentBridge.exe'))) {
        throw "Published portable output missing Shima.AgentBridge.exe for $rid."
    }

    $portableAppText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($portableAppExe))
    if ($portableAppText.Contains('urn:schemas-microsoft-com:msix.v1')) {
        throw "Portable Shima.App.exe for $rid unexpectedly contains an <msix> manifest element."
    }

    $forbiddenDrawing = Get-ChildItem -Path $publishDir -Filter 'System.Drawing.Common*.dll' -Recurse -ErrorAction SilentlyContinue
    if ($null -ne $forbiddenDrawing) {
        throw "Forbidden System.Drawing.Common binary detected in portable publish output for $rid."
    }

    foreach ($staleArtifact in @('apphost.exe', 'Shima.App.dll')) {
        $stalePath = Join-Path $appObjRidDir $staleArtifact
        if (Test-Path $stalePath) {
            Remove-Item -Path $stalePath -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Publishing Sparse MSIX / installer Shima.App ($rid, UseSparsePackageManifest=true)..."
    dotnet publish $appProject `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContainedFlag `
        -p:Version=$Version `
        -p:UseSparsePackageManifest=true `
        -o $installerPublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for installer Shima.App ($rid)."
    }

    Copy-Item -Path (Join-Path $bridgeTempDir 'Shima.AgentBridge.*') -Destination $installerPublishDir -Force
    Remove-Item -Path $bridgeTempDir -Recurse -Force

    $installerAppExe = Join-Path $installerPublishDir 'Shima.App.exe'
    if (-not (Test-Path $installerAppExe)) {
        throw "Published installer output missing Shima.App.exe for $rid."
    }
    if (-not (Test-Path (Join-Path $installerPublishDir 'Shima.AgentBridge.exe'))) {
        throw "Published installer output missing Shima.AgentBridge.exe for $rid."
    }

    $installerAppText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($installerAppExe))
    if (-not $installerAppText.Contains('urn:schemas-microsoft-com:msix.v1')) {
        throw "Installer Shima.App.exe for $rid is missing the required Sparse MSIX <msix> manifest element."
    }

    $forbiddenInstallerDrawing = Get-ChildItem -Path $installerPublishDir -Filter 'System.Drawing.Common*.dll' -Recurse -ErrorAction SilentlyContinue
    if ($null -ne $forbiddenInstallerDrawing) {
        throw "Forbidden System.Drawing.Common binary detected in installer publish output for $rid."
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

    if ($null -ne $makePri) {
        $sparsePriTempDir = Join-Path $repoRoot "artifacts\sparse-pri\$rid"
        $priConfigPath = Join-Path $sparsePriTempDir 'priconfig.xml'
        $stagedPriPath = Join-Path $sparseStageDir 'resources.pri'

        try {
            if (Test-Path $sparsePriTempDir) {
                Remove-Item -Path $sparsePriTempDir -Recurse -Force
            }
            New-Item -ItemType Directory -Force -Path $sparsePriTempDir | Out-Null

            Write-Host "Generating MRT resource index (resources.pri) via MakePri for $rid..."
            & $makePri createconfig /cf $priConfigPath /dq en-US /pv 10.0.0 /o | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "MakePri createconfig failed for $rid with exit code $LASTEXITCODE."
            }

            & $makePri new /pr $sparseStageDir /cf $priConfigPath /mn $stagedManifestPath /of $stagedPriPath /o
            if ($LASTEXITCODE -ne 0) {
                throw "MakePri new failed for $rid with exit code $LASTEXITCODE."
            }

            if (-not (Test-Path $stagedPriPath) -or (Get-Item $stagedPriPath).Length -le 0) {
                throw "Expected generated resources.pri was not created at '$stagedPriPath'."
            }
        }
        finally {
            if (Test-Path $sparsePriTempDir) {
                Remove-Item -Path $sparsePriTempDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
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
        Copy-Item -Path (Join-Path $installerPublishDir 'Shima.App.exe') -Destination $sparseValidateDir -Force

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
        $requiredEntries = @(
            'AppxManifest.xml',
            'Assets/StoreLogo.png',
            'Assets/Square44x44Logo.png',
            'Assets/Square150x150Logo.png',
            'Assets/Square44x44Logo.targetsize-16.png',
            'Assets/Square44x44Logo.targetsize-24.png',
            'Assets/Square44x44Logo.targetsize-32.png',
            'Assets/Square44x44Logo.targetsize-48.png',
            'Assets/Square44x44Logo.targetsize-256.png',
            'Assets/Square44x44Logo.altform-unplated_targetsize-16.png',
            'Assets/Square44x44Logo.altform-unplated_targetsize-24.png',
            'Assets/Square44x44Logo.altform-unplated_targetsize-32.png',
            'Assets/Square44x44Logo.altform-unplated_targetsize-48.png',
            'Assets/Square44x44Logo.altform-unplated_targetsize-256.png'
        )
        if ($null -ne $makePri) {
            $requiredEntries += 'resources.pri'
        }
        foreach ($requiredEntry in $requiredEntries) {
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

    $publishSparseDir = Join-Path $installerPublishDir 'sparse-identity'
    New-Item -ItemType Directory -Force -Path $publishSparseDir | Out-Null
    Copy-Item -Path $msixPath -Destination $publishSparseDir -Force
    Copy-Item -Path $registerScriptPath -Destination $publishSparseDir -Force
    $bundledMsixPath = Join-Path $publishSparseDir (Split-Path -Leaf $msixPath)

    $publishAssetsDir = Join-Path $installerPublishDir 'Assets'
    New-Item -ItemType Directory -Force -Path $publishAssetsDir | Out-Null
    Copy-Item -Path (Join-Path $sparseStageDir 'Assets\*.png') -Destination $publishAssetsDir -Force

    $stagedPriPath = Join-Path $sparseStageDir 'resources.pri'
    if (Test-Path $stagedPriPath) {
        Copy-Item -Path $stagedPriPath -Destination (Join-Path $installerPublishDir 'resources.pri') -Force
    }

    if ($Sign) {
        $signTargets = @(
            (Join-Path $publishDir 'Shima.App.exe'),
            (Join-Path $publishDir 'Shima.AgentBridge.exe'),
            (Join-Path $installerPublishDir 'Shima.App.exe'),
            (Join-Path $installerPublishDir 'Shima.AgentBridge.exe'),
            $msixPath,
            $bundledMsixPath
        )
        & $signScriptPath -Paths $signTargets -PfxPath $PfxPath -PfxPassword $PfxPassword -CertThumbprint $CertThumbprint -SkipTimestamp:$SkipTimestamp -AllowUntrustedSelfSigned:$allowUntrustedSelfSigned
    }

    $zipPath = Join-Path $releaseDir "Shima-$Version-$rid.zip"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force

    $primaryArtifactPath = $zipPath
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
                & $signScriptPath -Paths @($setupExe.FullName) -PfxPath $PfxPath -PfxPassword $PfxPassword -CertThumbprint $CertThumbprint -SkipTimestamp:$SkipTimestamp -AllowUntrustedSelfSigned:$allowUntrustedSelfSigned
            }
            $primaryArtifactPath = $setupExe.FullName
        }
    }

    $innoInstallerPath = $null
    if ($null -ne $isccPath) {
        Write-Host "Compiling Inno Setup installer for $rid via '$isccPath'..."
        & $isccPath `
            "/DAppVersion=$Version" `
            "/DAppFileVersion=$fourPartVersion" `
            "/DRid=$rid" `
            "/DSourceDir=$installerPublishDir" `
            "/DOutputDir=$releaseDir" `
            $innoScriptPath
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compiler failed for $rid with exit code $LASTEXITCODE."
        }

        $expectedInstallerPath = Join-Path $releaseDir "Shima-Setup-$Version-$rid.exe"
        if (-not (Test-Path $expectedInstallerPath)) {
            throw "Expected Inno Setup output not found at '$expectedInstallerPath'."
        }

        if ($Sign) {
            & $signScriptPath -Paths @($expectedInstallerPath) -PfxPath $PfxPath -PfxPassword $PfxPassword -CertThumbprint $CertThumbprint -SkipTimestamp:$SkipTimestamp -AllowUntrustedSelfSigned:$allowUntrustedSelfSigned
        }

        $innoInstallerPath = $expectedInstallerPath
    }

    $fileItem = Get-Item $primaryArtifactPath
    $sha256 = (Get-FileHash -Path $primaryArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $baseUrl = $DownloadBaseUrl.TrimEnd('/')

    $packageEntry = [ordered]@{
        architecture = $rid
        downloadUrl  = "$baseUrl/$($fileItem.Name)"
        sha256       = $sha256
        sizeBytes    = [int64]$fileItem.Length
        fileName     = $fileItem.Name
        msixFileName = (Split-Path -Leaf $msixPath)
        msixSha256   = (Get-FileHash -Path $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    if ($null -ne $innoInstallerPath) {
        $installerItem = Get-Item $innoInstallerPath
        $packageEntry['installerFileName'] = $installerItem.Name
        $packageEntry['installerDownloadUrl'] = "$baseUrl/$($installerItem.Name)"
        $packageEntry['installerSha256'] = (Get-FileHash -Path $innoInstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $packageEntry['installerSizeBytes'] = [int64]$installerItem.Length
    }

    $manifestPackages += $packageEntry
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
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdDevCertThumbprint)) {
        Remove-Item -Path "Cert:\CurrentUser\My\$createdDevCertThumbprint" -ErrorAction SilentlyContinue
    }
    if (-not [string]::IsNullOrWhiteSpace($createdDevPfxPath) -and (Test-Path $createdDevPfxPath)) {
        Remove-Item -Path $createdDevPfxPath -Force -ErrorAction SilentlyContinue
    }
}
