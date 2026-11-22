<#
.SYNOPSIS
Executes Shima's release verification gates: static security/UI audits, Release build + tests,
dual-RID Sparse MSIX packaging check, live idle/zero-socket/memory soak probe, and AgentBridge fail-open timing check.
Pass -SoakDurationSeconds 28800 for a full 8-hour soak verification run.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [int]$SoakDurationSeconds = 15,

    [switch]$SkipBuild,

    [switch]$SkipPackaging,

    [switch]$SkipLiveProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
$results = [System.Collections.Generic.List[object]]::new()

function Record-Gate {
    param(
        [string]$Gate,
        [bool]$Passed,
        [string]$Details
    )

    $results.Add([pscustomobject]@{
        Gate    = $Gate
        Status  = if ($Passed) { 'PASS' } else { 'FAIL' }
        Details = $Details
    }) | Out-Null

    if (-not $Passed) {
        throw "Verification gate '$Gate' failed: $Details"
    }
}

try {
    # Gate 1: Static safeguard & syntax audit
    $srcDir = Join-Path $repoRoot 'src'
    $drawingMatches = Get-ChildItem -Path $srcDir -Recurse -Include *.cs, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -Pattern '^\s*using\s+System\.Drawing|PackageReference.*System\.Drawing' -ErrorAction SilentlyContinue
    Record-Gate -Gate 'Zero System.Drawing references' -Passed ($null -eq $drawingMatches) -Details "$(@($drawingMatches).Count) matches in src/"

    $hexMatches = Get-ChildItem -Path $srcDir -Recurse -Filter *.xaml |
        Where-Object { $_.Name -ne 'ThemeTokens.xaml' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -Pattern '#[0-9A-Fa-f]{6,8}' -ErrorAction SilentlyContinue
    Record-Gate -Gate 'Zero hardcoded XAML hex literals' -Passed ($null -eq $hexMatches) -Details "$(@($hexMatches).Count) matches outside ThemeTokens.xaml"

    $psFiles = Get-ChildItem -Path (Join-Path $repoRoot 'build') -Filter *.ps1 -File
    $syntaxErrors = @()
    foreach ($psFile in $psFiles) {
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($psFile.FullName, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            $syntaxErrors += "$($psFile.Name): $($parseErrors[0].Message)"
        }
    }
    Record-Gate -Gate 'Build script PowerShell syntax' -Passed ($syntaxErrors.Count -eq 0) -Details "$($psFiles.Count) scripts parsed clean"

    # Gate 2 & 3: Solution Build & Test Suite
    if (-not $SkipBuild) {
        Write-Host "Running dotnet build Shima.sln -c $Configuration..."
        dotnet build Shima.sln -c $Configuration
        Record-Gate -Gate 'Solution Release Build' -Passed ($LASTEXITCODE -eq 0) -Details "ExitCode=$LASTEXITCODE"
    }

    Write-Host "Running dotnet test Shima.sln -c $Configuration --no-build..."
    dotnet test Shima.sln -c $Configuration --no-build
    Record-Gate -Gate 'Automated Test Suite' -Passed ($LASTEXITCODE -eq 0) -Details "ExitCode=$LASTEXITCODE"

    # Gate 4: Sparse MSIX + Dual-RID Release Packaging Verification
    $manifestPath = Join-Path $repoRoot 'artifacts\releases\manifest-stable.json'
    if (-not $SkipPackaging) {
        & (Join-Path $repoRoot 'build\build-release.ps1') -Version '1.0.0' -Configuration $Configuration -Runtimes @('win-x64', 'win-arm64') -SkipTimestamp
        $manifestDoc = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
        $verifiedCount = 0
        foreach ($pkg in $manifestDoc.packages) {
            $ridDir = Join-Path $repoRoot "artifacts\releases\$($pkg.architecture)"
            $msixFile = Join-Path $ridDir $pkg.msixFileName
            $pkgFile = Join-Path $ridDir $pkg.fileName
            if (-not (Test-Path $msixFile) -or -not (Test-Path $pkgFile)) {
                throw "Missing package artifact for $($pkg.architecture)."
            }
            $actualSha = (Get-FileHash -Path $pkgFile -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualSha -ne $pkg.sha256) {
                throw "SHA-256 mismatch for $($pkg.fileName)."
            }
            if ($pkg.PSObject.Properties['installerFileName'] -and -not [string]::IsNullOrWhiteSpace($pkg.installerFileName)) {
                $installerFile = Join-Path $ridDir $pkg.installerFileName
                if (-not (Test-Path $installerFile)) {
                    throw "Missing installer artifact '$($pkg.installerFileName)' for $($pkg.architecture)."
                }
                $actualInstallerSha = (Get-FileHash -Path $installerFile -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualInstallerSha -ne $pkg.installerSha256) {
                    throw "SHA-256 mismatch for $($pkg.installerFileName)."
                }
            }
            $verifiedCount++
        }
        Record-Gate -Gate 'Dual-RID Packaging & Sparse MSIX' -Passed ($verifiedCount -eq 2) -Details "$verifiedCount RIDs (win-x64, win-arm64) + MSIX + SHA-256 manifest verified"
    }

    # Gate 5: Live Runtime Probe (AgentBridge Fail-Open, 0 Sockets, Memory & Idle CPU Soak)
    $liveSummary = $null
    if (-not $SkipLiveProbe) {
        $appDir = Join-Path $repoRoot "src\Shima.App\bin\$Configuration\net8.0-windows10.0.22621.0"
        $appExe = Join-Path $appDir 'Shima.App.exe'
        $bridgeExe = Join-Path $appDir 'Shima.AgentBridge.exe'

        if (-not (Test-Path $appExe) -or -not (Test-Path $bridgeExe)) {
            throw "Release executables not found under '$appDir'."
        }

        # Verify AgentBridge fail-open with Shima closed
        $bridgeSw = [System.Diagnostics.Stopwatch]::StartNew()
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $bridgeExe
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        $bridgeProc = [System.Diagnostics.Process]::Start($psi)
        $bridgeProc.StandardInput.WriteLine('{"hook_event_name":"PreToolUse","session_id":"verify","tool_name":"Bash"}')
        $bridgeProc.StandardInput.Close()
        $bridgeStdout = $bridgeProc.StandardOutput.ReadToEnd()
        $bridgeProc.WaitForExit(3000) | Out-Null
        $bridgeSw.Stop()

        $bridgePass = ($bridgeProc.ExitCode -eq 0) -and [string]::IsNullOrWhiteSpace($bridgeStdout)
        Record-Gate -Gate 'AgentBridge Fail-Open (Shima Closed)' -Passed $bridgePass -Details "Exit=$($bridgeProc.ExitCode), StdoutLen=$($bridgeStdout.Length), ElapsedMs=$([int]$bridgeSw.ElapsedMilliseconds)"

        # Launch Shima.App against a clean isolated profile for zero-socket & idle soak sampling
        $soakProfileDir = Join-Path $repoRoot 'artifacts\verification\soak-profile'
        if (Test-Path $soakProfileDir) {
            Remove-Item -Path $soakProfileDir -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $soakProfileDir | Out-Null

        $launchSw = [System.Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process -FilePath $appExe -ArgumentList @('--data-dir', "`"$soakProfileDir`"") -PassThru
        try {
            $ready = $false
            while ($launchSw.Elapsed.TotalMilliseconds -lt 5000) {
                $proc.Refresh()
                if ($proc.HasExited) {
                    throw "Shima.App exited unexpectedly during startup with code $($proc.ExitCode)."
                }
                if ($proc.WaitForInputIdle(100)) {
                    $ready = $true
                    break
                }
                Start-Sleep -Milliseconds 50
            }
            $launchSw.Stop()

            # Settle past startup animations into idle collapsed state
            Start-Sleep -Seconds 3
            $proc.Refresh()
            $cpuStart = $proc.TotalProcessorTime
            $wallStart = Get-Date
            $samples = @()
            $maxSockets = 0
            $maxExternalSockets = 0
            $loopbackOrUnspecified = @('::', '0.0.0.0', '127.0.0.1', '::1', '')
            $sampleInterval = if ($SoakDurationSeconds -ge 120) { 10 } else { 1 }
            $deadline = (Get-Date).AddSeconds([Math]::Max(3, $SoakDurationSeconds))

            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Seconds $sampleInterval
                $proc.Refresh()
                if ($proc.HasExited) {
                    throw "Shima.App exited prematurely during soak probe with code $($proc.ExitCode)."
                }

                $sockets = @(Get-NetTCPConnection -OwningProcess $proc.Id -ErrorAction SilentlyContinue)
                if ($sockets.Count -gt $maxSockets) {
                    $maxSockets = $sockets.Count
                }

                $externalSockets = @($sockets | Where-Object { $loopbackOrUnspecified -notcontains $_.RemoteAddress })
                if ($externalSockets.Count -gt $maxExternalSockets) {
                    $maxExternalSockets = $externalSockets.Count
                }

                $samples += [pscustomobject]@{
                    WorkingSetMb = [Math]::Round($proc.WorkingSet64 / 1MB, 2)
                    PrivateMb    = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 2)
                    Handles      = $proc.HandleCount
                }
            }

            $proc.Refresh()
            $cpuDeltaSec = ($proc.TotalProcessorTime - $cpuStart).TotalSeconds
            $wallDeltaSec = ((Get-Date) - $wallStart).TotalSeconds
            $avgCpuPct = [Math]::Round(($cpuDeltaSec / ($wallDeltaSec * [Environment]::ProcessorCount)) * 100.0, 3)
            $maxWsMb = ($samples | Measure-Object -Property WorkingSetMb -Maximum).Maximum
            $maxPrivateMb = ($samples | Measure-Object -Property PrivateMb -Maximum).Maximum
            $initialPrivateMb = ($samples | Select-Object -First 1).PrivateMb
            $finalPrivateMb = ($samples | Select-Object -Last 1).PrivateMb
            $privateGrowthMb = [Math]::Round($finalPrivateMb - $initialPrivateMb, 2)
            $initialHandles = ($samples | Select-Object -First 1).Handles
            $finalHandles = ($samples | Select-Object -Last 1).Handles
            $handleGrowth = $finalHandles - $initialHandles

            $liveSummary = [ordered]@{
                soakDurationSeconds = $SoakDurationSeconds
                startupReadyMs      = [int]$launchSw.ElapsedMilliseconds
                inputIdleReady      = $ready
                maxTcpSockets       = $maxSockets
                maxExternalSockets  = $maxExternalSockets
                avgIdleCpuPercent   = $avgCpuPct
                initialPrivateMb    = $initialPrivateMb
                finalPrivateMb      = $finalPrivateMb
                privateGrowthMb     = $privateGrowthMb
                maxPrivateMb        = $maxPrivateMb
                maxWorkingSetMb     = $maxWsMb
                initialHandleCount  = $initialHandles
                finalHandleCount    = $finalHandles
                handleGrowth        = $handleGrowth
                samplesCollected    = $samples.Count
            }

            Record-Gate -Gate 'Zero Outbound/Listening Sockets' -Passed ($maxSockets -eq 0 -and $maxExternalSockets -eq 0) -Details "MaxObservedSockets=$maxSockets, MaxExternalSockets=$maxExternalSockets over ${SoakDurationSeconds}s"
            Record-Gate -Gate 'Idle CPU & Memory Envelope' -Passed ($avgCpuPct -lt 0.5 -and $maxPrivateMb -lt 110.0 -and $maxWsMb -lt 200.0 -and $privateGrowthMb -le 15.0 -and $handleGrowth -le 25) -Details "AvgIdleCpu=${avgCpuPct}%, MaxPrivate=${maxPrivateMb}MB (delta=${privateGrowthMb}MB), MaxWorkingSet=${maxWsMb}MB, Handles=${initialHandles}->${finalHandles} (delta=${handleGrowth}), WindowReadyMs=$($liveSummary.startupReadyMs)"
        }
        finally {
            if (-not $proc.HasExited) {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                $proc.WaitForExit(3000) | Out-Null
            }
            if (Test-Path $soakProfileDir) {
                Remove-Item -Path $soakProfileDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $reportDir = Join-Path $repoRoot 'artifacts\verification'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $reportPath = Join-Path $reportDir 'release-verification-report.json'
    $reportDoc = [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        configuration  = $Configuration
        gates          = $results
        liveProbe      = $liveSummary
    }
    [System.IO.File]::WriteAllText($reportPath, ($reportDoc | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "`n=== Release Verification Summary ==="
    $results | Format-Table -AutoSize
    Write-Host "Report written to '$reportPath'."
}
finally {
    Pop-Location
}
