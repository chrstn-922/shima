<#
.SYNOPSIS
Signs Shima executables, Sparse MSIX identity packages, and Velopack installers using Authenticode SHA-256 + RFC 3161 timestamping.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths,

    [string]$PfxPath,

    [string]$PfxPassword = $env:SHIMA_SIGN_PFX_PASSWORD,

    [string]$CertThumbprint,

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [switch]$SkipTimestamp,

    [switch]$CreateDevSelfSignedCert,

    [string]$Subject = 'CN=Shima',

    [switch]$AllowUntrustedSelfSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SignToolPath {
    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -Path $kitsRoot -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    $cmd = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }

    throw "SignTool.exe was not found. Install the Windows 10/11 SDK."
}

$signTool = Resolve-SignToolPath
$createdTempCertThumbprint = $null
$usedDevSelfSigned = $false

try {
    if ($CreateDevSelfSignedCert -and [string]::IsNullOrWhiteSpace($PfxPath) -and [string]::IsNullOrWhiteSpace($CertThumbprint)) {
        $usedDevSelfSigned = $true
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $devCertDir = Join-Path $repoRoot 'artifacts\dev-cert'
        New-Item -ItemType Directory -Force -Path $devCertDir | Out-Null
        $PfxPath = Join-Path $devCertDir 'shima-dev.pfx'

        if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
            $PfxPassword = [Guid]::NewGuid().ToString('N')
        }

        $securePwd = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Subject `
            -KeyUsage DigitalSignature `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(1)

        $createdTempCertThumbprint = $cert.Thumbprint
        Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $securePwd | Out-Null
    }

    if ([string]::IsNullOrWhiteSpace($PfxPath) -and [string]::IsNullOrWhiteSpace($CertThumbprint)) {
        throw "Provide either -PfxPath, -CertThumbprint, or -CreateDevSelfSignedCert."
    }

    $allowSelfSignedVerify = $usedDevSelfSigned -or $AllowUntrustedSelfSigned.IsPresent

    foreach ($artifactPath in $Paths) {
        if (-not (Test-Path $artifactPath)) {
            throw "Signing target does not exist: $artifactPath"
        }

        $signArgs = @('sign', '/fd', 'SHA256')
        if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
            $signArgs += @('/f', $PfxPath)
            if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) {
                $signArgs += @('/p', $PfxPassword)
            }
        }
        else {
            $signArgs += @('/sha1', $CertThumbprint)
        }

        if (-not $SkipTimestamp -and -not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256')
        }

        $signArgs += $artifactPath

        Write-Host "Signing '$artifactPath'..."
        & $signTool @signArgs
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed for '$artifactPath' with exit code $LASTEXITCODE."
        }

        if ($allowSelfSignedVerify) {
            $sig = Get-AuthenticodeSignature -FilePath $artifactPath
            if ($null -eq $sig -or $null -eq $sig.SignerCertificate -or $sig.Status -in @([System.Management.Automation.SignatureStatus]::NotSigned, [System.Management.Automation.SignatureStatus]::HashMismatch)) {
                throw "Embedded self-signed Authenticode verification failed for '$artifactPath' (Status=$($sig.Status))."
            }
            Write-Host "Verified embedded Authenticode signature structure on '$artifactPath' (dev self-signed, thumbprint $($sig.SignerCertificate.Thumbprint))."
        }
        else {
            & $signTool verify /pa $artifactPath
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool verification failed for '$artifactPath' with exit code $LASTEXITCODE."
            }
        }
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdTempCertThumbprint)) {
        Remove-Item -Path "Cert:\CurrentUser\My\$createdTempCertThumbprint" -ErrorAction SilentlyContinue
    }
}
