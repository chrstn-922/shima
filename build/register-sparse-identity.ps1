<#
.SYNOPSIS
Registers or unregisters the Shima Sparse MSIX Package Identity for UserNotificationListener and StartupTask support.
Requires Windows 10 Version 2004 (Build 19041) or newer; older builds fall back gracefully.
#>
[CmdletBinding()]
param(
    [ValidateSet('Register', 'Unregister')]
    [string]$Action = 'Register',

    [string]$PackagePath,

    [string]$ExternalLocation,

    [string]$PackageName = 'Shima.DesktopCompanion',

    [switch]$PurgeUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$osBuild = [Environment]::OSVersion.Version.Build
if ($Action -eq 'Register' -and $osBuild -lt 19041) {
    Write-Warning "Windows build $osBuild is below 19041. Sparse MSIX Package Identity requires Windows 10 2004+. Inbox will operate in graceful IdentityUnavailable guidance mode."
    exit 0
}

if ($Action -eq 'Register') {
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $archToken = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
        $candidateLocal = Join-Path $PSScriptRoot "Shima.Identity-$archToken.msix"
        if (Test-Path $candidateLocal) {
            $PackagePath = $candidateLocal
        }
        else {
            $fallbackAny = Get-ChildItem -Path $PSScriptRoot -Filter 'Shima.Identity-*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $fallbackAny) {
                $PackagePath = $fallbackAny.FullName
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($PackagePath) -or -not (Test-Path $PackagePath)) {
        throw "Sparse identity MSIX package not found. Specify -PackagePath explicitly."
    }

    if ([string]::IsNullOrWhiteSpace($ExternalLocation)) {
        $parentDir = Split-Path -Parent $PSScriptRoot
        if (Test-Path (Join-Path $PSScriptRoot 'Shima.App.exe')) {
            $ExternalLocation = $PSScriptRoot
        }
        elseif (-not [string]::IsNullOrWhiteSpace($parentDir) -and (Test-Path (Join-Path $parentDir 'Shima.App.exe'))) {
            $ExternalLocation = $parentDir
        }
        else {
            throw "ExternalLocation containing Shima.App.exe could not be resolved. Pass -ExternalLocation explicitly."
        }
    }

    if (-not (Test-Path (Join-Path $ExternalLocation 'Shima.App.exe'))) {
        throw "ExternalLocation '$ExternalLocation' does not contain Shima.App.exe."
    }

    Write-Host "Registering Sparse MSIX package '$PackagePath' with external location '$ExternalLocation'..."
    Add-AppxPackage -Path $PackagePath -ExternalLocation $ExternalLocation
    Write-Host "Sparse MSIX package registration completed cleanly."
    exit 0
}

if ($Action -eq 'Unregister') {
    $packages = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    foreach ($pkg in $packages) {
        Write-Host "Removing Sparse MSIX package '$($pkg.PackageFullName)'..."
        Remove-AppxPackage -Package $pkg.PackageFullName
    }

    $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path $runKeyPath) {
        Remove-ItemProperty -Path $runKeyPath -Name 'Shima' -ErrorAction SilentlyContinue
    }

    $userDataRoot = Join-Path $env:LOCALAPPDATA 'Shima'
    if ($PurgeUserData -and (Test-Path $userDataRoot)) {
        Write-Host "Purging user data directory '$userDataRoot' as requested..."
        Remove-Item -Path $userDataRoot -Recurse -Force
    }
    elseif (Test-Path $userDataRoot) {
        Write-Host "Preserved local user data at '$userDataRoot' (pass -PurgeUserData to delete)."
    }

    Write-Host "Unregistration complete."
}
