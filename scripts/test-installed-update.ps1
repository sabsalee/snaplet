$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This installation test runs only in a disposable GitHub Actions Windows runner.' }
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('snaplet-update-' + [guid]::NewGuid().ToString('N'))
$previousTestFlag = $env:SNAPLET_UPDATE_TESTS
$settingsFile = Join-Path $env:LOCALAPPDATA 'Snaplet/settings.json'
$hadSettings = Test-Path $settingsFile
$originalSettings = if ($hadSettings) { [IO.File]::ReadAllBytes($settingsFile) } else { $null }
try {
    $env:SNAPLET_UPDATE_TESTS = '1'
    New-Item $testRoot -ItemType Directory | Out-Null
    New-Item ([IO.Path]::GetDirectoryName($settingsFile)) -ItemType Directory -Force | Out-Null
    @{ AutoUpdate = $false; ShowPreview = $true; SaveFolder = (Join-Path $testRoot 'saved-captures') } | ConvertTo-Json | Set-Content $settingsFile -Encoding utf8NoBOM
    $payload = Join-Path $root 'artifacts/snaplet-win-x64'
    $feed = Join-Path $root 'artifacts/updates'
    [xml]$project = Get-Content src/Capture.Windows/Capture.Windows.csproj
    $version = [string]$project.Project.PropertyGroup.Version
    $baseline = '0.0.1'
    $baselineFiles = Join-Path $testRoot 'baseline-files'
    New-Item $baselineFiles -ItemType Directory | Out-Null
    Copy-Item "$payload/*" $baselineFiles -Recurse
    Set-Content (Join-Path $baselineFiles 'baseline-only.marker') 'baseline'
    $baselinePackages = Join-Path $testRoot 'baseline-packages'
    & ./scripts/package-updates.ps1 -PackDirectory $baselineFiles -OutputDirectory $baselinePackages -Version $baseline
    $setup = Get-ChildItem $baselinePackages -Filter '*Setup.exe' | Select-Object -First 1
    if (-not $setup) { throw 'Baseline installer missing' }
    $installation = Join-Path $testRoot 'installed'
    $setupProcess = Start-Process $setup.FullName -ArgumentList @('--silent', '--installto', "`"$installation`"") -PassThru
    if (-not $setupProcess.WaitForExit(120000)) { $setupProcess.Kill(); throw 'Baseline installation timed out' }
    if ($setupProcess.ExitCode -ne 0) { throw "Baseline install failed: $($setupProcess.ExitCode)" }
    $executable = Join-Path $installation 'current/Snaplet.exe'
    if (-not (Test-Path $executable)) { throw "Installed executable not found: $executable" }
    $corruptFeed = Join-Path $testRoot 'corrupt-feed'
    New-Item $corruptFeed -ItemType Directory | Out-Null
    Copy-Item "$feed/*.json" $corruptFeed
    Copy-Item "$feed/*.nupkg" $corruptFeed
    foreach ($package in Get-ChildItem $corruptFeed -Filter '*.nupkg') {
        $stream = [IO.File]::Open($package.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
        try { $first = $stream.ReadByte(); $stream.Position = 0; $stream.WriteByte([byte]($first -bxor 255)) } finally { $stream.Dispose() }
    }
    $report = Join-Path $root 'artifacts/smoke-results/installed-update.json'
    $process = Start-Process $executable -ArgumentList @('--update-integration', "`"$feed`"", "`"$corruptFeed`"", "`"$report`"", $baseline, $version) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    do {
        Start-Sleep -Milliseconds 500
        if (Test-Path $report) {
            try { $result = Get-Content $report -Raw | ConvertFrom-Json } catch { continue }
            if ($result.Status -eq 'failed') { throw $result.Error }
            if ($result.Status -eq 'passed') { break }
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($result.Status -ne 'passed' -or $result.Version -ne $version) { throw 'Installed update/restart verification timed out or version mismatch' }
    Write-Host "PASS: installed $baseline -> $version; checksum rejection, directory replacement, restart and settings preservation."
} finally {
    $env:SNAPLET_UPDATE_TESTS = $previousTestFlag
    if ($hadSettings) { [IO.File]::WriteAllBytes($settingsFile, $originalSettings) } else { Remove-Item $settingsFile -ErrorAction SilentlyContinue }
    if ($result.Marker -and [IO.Path]::GetFileName($result.Marker).StartsWith('update-check-')) { Remove-Item $result.Marker -ErrorAction SilentlyContinue }
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Pop-Location
}
