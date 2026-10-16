param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$project = Get-Content src/Capture.Windows/Capture.Windows.csproj
    $version = [string]$project.Project.PropertyGroup.Version
    if ($Tag -ne "v$version") { throw 'Version tag must match the project Version.' }
    $report = Get-Content artifacts/smoke-results/installed-update.json -Raw | ConvertFrom-Json
    if ($report.Status -ne 'passed' -or $report.Version -ne $version) { throw 'Installed update verification must pass before publishing.' }
    $updates = Get-ChildItem artifacts/updates -File | Where-Object { $_.Extension -in '.exe', '.nupkg' -or $_.Name -like 'releases.*.json' -or $_.Name -eq 'SHA256SUMS' }
    if (-not ($updates | Where-Object { $_.Name -like '*Setup.exe' })) { throw 'Installer missing' }
    $assets = @('artifacts/Snaplet-Windows-x64.zip') + @($updates.FullName)
    gh release create $Tag @assets --title "Snaplet $Tag" --notes-file docs/RELEASE.md --prerelease --target $env:GITHUB_SHA
    if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
} finally { Pop-Location }
