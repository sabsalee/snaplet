# Force-pushes the latest self-test PNG/JSON outputs to the orphan 'visual-review' branch.
# The branch holds only review images for the most recent pushed commit; it is not part of the product.
$ErrorActionPreference = 'Stop'
$out = Join-Path $env:RUNNER_TEMP 'visual-review'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item artifacts/smoke-results/*.png $out
Copy-Item artifacts/smoke-results/*.json $out -ErrorAction SilentlyContinue
"Visual review for $env:GITHUB_REF_NAME @ $env:GITHUB_SHA`n" | Out-File -Encoding utf8 (Join-Path $out 'README.md')
Push-Location $out
try {
    git init -q
    git checkout -q -b visual-review
    git config user.name 'snaplet-ci'
    git config user.email 'snaplet-ci@users.noreply.github.com'
    git add -A
    git commit -q -m "Visual review for $env:GITHUB_SHA"
    git push -q -f "https://x-access-token:$env:GH_TOKEN@github.com/$env:GITHUB_REPOSITORY.git" visual-review
    if ($LASTEXITCODE -ne 0) { throw 'visual-review push failed' }
} finally { Pop-Location }
