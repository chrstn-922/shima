<#
.SYNOPSIS
Registers or unregisters the Shima Sparse MSIX Package Identity for UserNotificationListener and StartupTask support,
and optionally trusts an embedded self-signed certificate into LocalMachine stores during elevated setup.
Requires Windows 10 Version 2004 (Build 19041) or newer; older builds fall back gracefully.
#>
[CmdletBinding()]
param(
    [ValidateSet('Register', 'Unregister')]
    [string]$Action = 'Register',

    [switch]$Unregister,

    [switch]$TrustEmbeddedCert,

    [switch]$TrustOnly,

    [switch]$BestEffort,

    [string]$PackagePath,

    [string]$ExternalLocation,

    [string]$PackageName = 'Shima.DesktopCompanion',

    [switch]$PurgeUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Unregister) {
    $Action = 'Unregister'
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-PackagePath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return $ExplicitPath
    }

    $archToken = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
    $candidateLocal = Join-Path $PSScriptRoot "Shima.Identity-$archToken.msix"
    if (Test-Path $candidateLocal) {
        return $candidateLocal
    }

    $fallbackAny = Get-ChildItem -Path $PSScriptRoot -Filter 'Shima.Identity-*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $fallbackAny) {
        return $fallbackAny.FullName
    }

    return $null
}

function Import-CertToMachineStore {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,

        [Parameter(Mandatory = $true)]
        [ValidateSet('TrustedPeople', 'Root')]
        [string]$StoreName
    )

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $existing = $store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Certificate.Thumbprint,
            $false)
        if ($existing.Count -eq 0) {
            $store.Add($Certificate)
            Write-Host "Imported certificate '$($Certificate.Subject)' ($($Certificate.Thumbprint)) into LocalMachine\$StoreName."
        }
        else {
            Write-Host "Certificate '$($Certificate.Thumbprint)' is already trusted in LocalMachine\$StoreName."
        }
    }
    finally {
        $store.Close()
        $store.Dispose()
    }
}

function Invoke-TrustEmbeddedCert {
    param([string]$MsixPath)

    if ([string]::IsNullOrWhiteSpace($MsixPath) -or -not (Test-Path $MsixPath)) {
        throw "Sparse identity MSIX package not found for certificate inspection: '$MsixPath'."
    }

    $signature = Get-AuthenticodeSignature -FilePath $MsixPath
    if ($null -eq $signature -or $null -eq $signature.SignerCertificate) {
        Write-Host "Package '$MsixPath' has no embedded Authenticode signature; skipping certificate trust."
        return
    }

    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) {
        Write-Host "Embedded certificate for '$MsixPath' is already trusted."
        return
    }

    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::HashMismatch) {
        throw "Package '$MsixPath' failed Authenticode integrity verification (HashMismatch); refusing to trust certificate."
    }

    if (-not (Test-IsAdministrator)) {
        throw "Elevated administrator privileges are required to import the signer certificate into LocalMachine stores."
    }

    $cert = $signature.SignerCertificate
    $isSelfSigned = [string]::Equals($cert.Subject, $cert.Issuer, [StringComparison]::OrdinalIgnoreCase)

    Import-CertToMachineStore -Certificate $cert -StoreName 'TrustedPeople'
    if ($isSelfSigned) {
        Import-CertToMachineStore -Certificate $cert -StoreName 'Root'
    }
}

function Invoke-RegisterSparsePackage {
    param(
        [string]$MsixPath,
        [string]$AppLocation,
        [string]$IdentityPackageName
    )

    if ([string]::IsNullOrWhiteSpace($MsixPath) -or -not (Test-Path $MsixPath)) {
        throw "Sparse identity MSIX package not found. Specify -PackagePath explicitly."
    }

    if ([string]::IsNullOrWhiteSpace($AppLocation)) {
        $parentDir = Split-Path -Parent $PSScriptRoot
        if (Test-Path (Join-Path $PSScriptRoot 'Shima.App.exe')) {
            $AppLocation = $PSScriptRoot
        }
        elseif (-not [string]::IsNullOrWhiteSpace($parentDir) -and (Test-Path (Join-Path $parentDir 'Shima.App.exe'))) {
            $AppLocation = $parentDir
        }
        else {
            throw "ExternalLocation containing Shima.App.exe could not be resolved. Pass -ExternalLocation explicitly."
        }
    }

    if (-not (Test-Path (Join-Path $AppLocation 'Shima.App.exe'))) {
        throw "ExternalLocation '$AppLocation' does not contain Shima.App.exe."
    }

    Write-Host "Registering Sparse MSIX package '$MsixPath' with external location '$AppLocation'..."
    try {
        Add-AppxPackage -Path $MsixPath -ExternalLocation $AppLocation -ForceUpdateFromAnyVersion
    }
    catch {
        # If an existing registration conflicts (e.g. moved install directory with same version), remove and retry once.
        $existingPackages = @(Get-AppxPackage -Name $IdentityPackageName -ErrorAction SilentlyContinue)
        if ($existingPackages.Count -gt 0) {
            foreach ($existing in $existingPackages) {
                Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction SilentlyContinue
            }
            Add-AppxPackage -Path $MsixPath -ExternalLocation $AppLocation -ForceUpdateFromAnyVersion
        }
        else {
            throw
        }
    }

    Write-Host "Sparse MSIX package registration completed cleanly."
}

function Invoke-UnregisterSparsePackage {
    param(
        [string]$IdentityPackageName,
        [bool]$ShouldPurgeUserData
    )

    $packages = @(Get-AppxPackage -Name $IdentityPackageName -ErrorAction SilentlyContinue)
    foreach ($pkg in $packages) {
        Write-Host "Removing Sparse MSIX package '$($pkg.PackageFullName)'..."
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
    }

    if (Test-IsAdministrator) {
        try {
            $allUserPackages = @(Get-AppxPackage -AllUsers -Name $IdentityPackageName -ErrorAction SilentlyContinue)
            foreach ($pkg in $allUserPackages) {
                Write-Host "Removing Sparse MSIX package '$($pkg.PackageFullName)' for all users..."
                Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction SilentlyContinue
            }
        }
        catch {
            Write-Warning "All-users sparse package cleanup reported: $($_.Exception.Message)"
        }
    }

    $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path $runKeyPath) {
        Remove-ItemProperty -Path $runKeyPath -Name 'Shima' -ErrorAction SilentlyContinue
    }

    $userDataRoot = Join-Path $env:LOCALAPPDATA 'Shima'
    if ($ShouldPurgeUserData -and (Test-Path $userDataRoot)) {
        Write-Host "Purging user data directory '$userDataRoot' as requested..."
        Remove-Item -Path $userDataRoot -Recurse -Force
    }
    elseif (Test-Path $userDataRoot) {
        Write-Host "Preserved local user data at '$userDataRoot' (pass -PurgeUserData to delete)."
    }

    Write-Host "Unregistration complete."
}

$osBuild = [Environment]::OSVersion.Version.Build

try {
    if ($Action -eq 'Unregister') {
        Invoke-UnregisterSparsePackage -IdentityPackageName $PackageName -ShouldPurgeUserData $PurgeUserData.IsPresent
        exit 0
    }

    if ($osBuild -lt 19041) {
        Write-Warning "Windows build $osBuild is below 19041. Sparse MSIX Package Identity requires Windows 10 2004+. Inbox will operate in graceful IdentityUnavailable guidance mode."
        exit 0
    }

    $resolvedPackagePath = Resolve-PackagePath -ExplicitPath $PackagePath

    if ($TrustEmbeddedCert) {
        Invoke-TrustEmbeddedCert -MsixPath $resolvedPackagePath
        if ($TrustOnly) {
            exit 0
        }
    }

    Invoke-RegisterSparsePackage -MsixPath $resolvedPackagePath -AppLocation $ExternalLocation -IdentityPackageName $PackageName
    exit 0
}
catch {
    if ($BestEffort) {
        Write-Warning "Sparse MSIX identity step skipped gracefully: $($_.Exception.Message)"
        exit 0
    }

    throw
}
