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
    }
    Compress-Archive -Path artifacts/snaplet-win-x64/* -DestinationPath artifacts/Snaplet-Windows-x64.zip -Force
    & ./scripts/package-updates.ps1 -PreviousRelease:($env:GITHUB_REF_TYPE -eq 'tag')
    Write-Host '완료: artifacts/Snaplet-Windows-x64.zip 및 artifacts/updates/'
} finally { Pop-Location }
