param([string]$PackDirectory = 'artifacts/snaplet-win-x64', [string]$OutputDirectory = 'artifacts/updates', [string]$Version, [switch]$PreviousRelease)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if (-not $Version) { [xml]$project = Get-Content src/Capture.Windows/Capture.Windows.csproj; $Version = [string]$project.Project.PropertyGroup.Version }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 도구 복원 실패' }
    if ($PreviousRelease) {
        dotnet tool run vpk -- download github --repoUrl https://github.com/sabsalee/snaplet --channel win-x64 --pre --timeout 2 --outputDir $OutputDirectory
        if ($LASTEXITCODE -ne 0) { Write-Host 'No previous update package available; building a full package.' }
    }
    dotnet tool run vpk -- pack --packId SnapletApp --packTitle Snaplet --packAuthors sabsalee --packVersion $Version --packDir $PackDirectory --mainExe Snaplet.exe --runtime win-x64 --channel win-x64 --framework vcredist143-x64 --noPortable --outputDir $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw '자동 업데이트 패키지 생성 실패' }
    $feed = Join-Path $OutputDirectory 'releases.win-x64.json'
    if (-not (Test-Path $feed)) { throw '업데이트 피드가 없습니다.' }
    $assets = Get-ChildItem $OutputDirectory -File | Where-Object { $_.Extension -in '.exe', '.nupkg' -or $_.Name -like 'releases.*.json' }
    $lines = $assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
    if (Test-Path artifacts/Snaplet-Windows-x64.zip) { $zip = Get-Item artifacts/Snaplet-Windows-x64.zip; $lines = @($lines) + ('{0}  {1}' -f (Get-FileHash $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $zip.Name) }
    $lines | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS') -Encoding utf8NoBOM
} finally { Pop-Location }
