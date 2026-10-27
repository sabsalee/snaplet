# Surfaces build/test failure details as GitHub annotations so they are readable from the checks API
# (useful when logs and artifacts cannot be downloaded). Never fails the job itself.
param([switch]$Summary)
$ErrorActionPreference = 'Continue'
function Emit([string]$title, [string[]]$lines) {
    if (-not $lines -or $lines.Count -eq 0) { return }
    $text = ($lines | Select-Object -First 60) -join "`n"
    if ($text.Length -gt 8000) { $text = $text.Substring(0, 8000) }
    $escaped = $text.Replace('%', '%25').Replace("`r", '').Replace("`n", '%0A')
    Write-Output "::error title=$title::$escaped"
}
$log = 'artifacts/build.log'
if ($Summary) {
    $lines = @()
    if (Test-Path $log) { $lines += Get-Content $log | Select-String -Pattern '^PASS:' | ForEach-Object { $_.Line.Trim() } }
    foreach ($json in Get-ChildItem artifacts/smoke-results -Filter *.json -ErrorAction SilentlyContinue) {
        try { $data = Get-Content $json.FullName -Raw | ConvertFrom-Json; if ($null -ne $data.passed) { $lines += "$($json.Name): $($data.passed) passed" } } catch {}
    }
    $text = ($lines -join "`n").Replace('%', '%25').Replace("`n", '%0A')
    Write-Output "::notice title=Test summary::$text"
    exit 0
}
if (Test-Path $log) {
    $all = Get-Content $log
    Emit 'Compiler errors' ($all | Select-String -Pattern ': error [A-Z]+[0-9]+' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    Emit 'Test failures' ($all | Select-String -Pattern 'Unhandled exception|Exception:|check failed|FAIL' | ForEach-Object { $_.Line.Trim() } | Select-Object -First 20)
    Emit 'Build log tail' ($all | Select-Object -Last 25)
}
foreach ($file in @('artifacts/smoke-results/result.txt', 'smoke-results/result.txt')) {
    if (Test-Path $file) { Emit "Self-test result ($file)" (Get-Content $file | Select-Object -First 40) }
}
exit 0
