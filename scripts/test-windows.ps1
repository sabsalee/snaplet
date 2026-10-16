param([string]$AppPath = '', [switch]$Ocr)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppPath) { $AppPath = Join-Path $projectRoot 'artifacts/snaplet-win-x64/Snaplet.exe' }
$AppPath = (Resolve-Path $AppPath).Path
$results = Join-Path $projectRoot 'artifacts/smoke-results'
$arguments = @('--self-test', "`"$results`"")
if ($Ocr) { $arguments += '--ocr-test' }
$process = Start-Process -FilePath $AppPath -ArgumentList $arguments -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Windows 검증 실패: $results/result.txt" }
Get-Content (Join-Path $results 'result.txt')
Write-Host '화면 촬영·단축키·팝업 포커스·혼합 DPI·속도는 docs/runtime-validation.md의 실기기 검증이 추가로 필요합니다.'
