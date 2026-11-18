param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK가 필요합니다: https://dotnet.microsoft.com/download/dotnet/10.0' }
    if (-not $SkipTests) {
        dotnet run --project tests/Capture.Core.Tests/Capture.Core.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw '공통 로직 검증 실패' }
    }
    dotnet publish src/Capture.Windows/Capture.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/snaplet-win-x64 -m:1 --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows 빌드 실패' }
    Copy-Item docs/START-HERE.ko.md artifacts/snaplet-win-x64/START-HERE.ko.md
    Copy-Item docs/THIRD-PARTY-NOTICES.md artifacts/snaplet-win-x64/THIRD-PARTY-NOTICES.md
    if (-not $SkipTests) {
        $results = Join-Path $projectRoot 'artifacts/smoke-results'
        $process = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/snaplet-win-x64/Snaplet.exe') -ArgumentList @('--self-test', "`"$results`"") -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw "Windows 렌더러 검증 실패: $results/result.txt" }
        # Recording phase 0: a real-screen probe for information only. A runner may have no capture or encoder.
        $probe = Join-Path $results 'record-probe'
        $recording = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/snaplet-win-x64/Snaplet.exe') -ArgumentList @('--record-probe', "`"$probe`"", '--seconds', '4', '--pause-at', '1.8', '--pause-for', '1') -PassThru
        if (-not $recording.WaitForExit(90000)) { $recording.Kill(); Write-Host 'Recording probe did not finish within 90 s.' }
        if (Test-Path "$probe/probe-report.txt") { Get-Content "$probe/probe-report.txt" | Write-Host }
        Remove-Item "$probe/probe.mp4" -ErrorAction SilentlyContinue
        # Recovery check: end the process mid-recording and inspect what is left on disk.
        $crash = Join-Path $results 'record-crash'
        $recording = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/snaplet-win-x64/Snaplet.exe') -ArgumentList @('--record-probe', "`"$crash`"", '--seconds', '6', '--crash-at', '3') -PassThru
        if (-not $recording.WaitForExit(60000)) { $recording.Kill() }
        if (Test-Path "$crash/probe.mp4") {
            $inspect = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/snaplet-win-x64/Snaplet.exe') -ArgumentList @('--record-inspect', "`"$crash/probe.mp4`"") -PassThru
            if (-not $inspect.WaitForExit(30000)) { $inspect.Kill() }
            if (Test-Path "$crash/probe.mp4.inspect.txt") { Get-Content "$crash/probe.mp4.inspect.txt" | Write-Host }
            Remove-Item "$crash/probe.mp4" -ErrorAction SilentlyContinue
        } else { Write-Host 'Crash check left no file.' }
    }
    Compress-Archive -Path artifacts/snaplet-win-x64/* -DestinationPath artifacts/Snaplet-Windows-x64.zip -Force
    & ./scripts/package-updates.ps1 -PreviousRelease:($env:GITHUB_REF_TYPE -eq 'tag')
    Write-Host '완료: artifacts/Snaplet-Windows-x64.zip 및 artifacts/updates/'
} finally { Pop-Location }
