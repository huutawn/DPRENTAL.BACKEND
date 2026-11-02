$ErrorActionPreference = 'Stop'

$repositoryRoot = (git rev-parse --show-toplevel).Trim()
if (-not $repositoryRoot) {
    throw 'Thiết lập Git hook: phải chạy bên trong Git worktree.'
}

Set-Location $repositoryRoot
if (-not (Test-Path '.githooks/pre-push')) {
    throw 'Thiết lập Git hook: thiếu .githooks/pre-push.'
}

if (-not (Test-Path 'scripts/pre-push-check.sh')) {
    throw 'Thiết lập Git hook: thiếu scripts/pre-push-check.sh.'
}

git config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw 'Thiết lập Git hook: không thể cấu hình core.hooksPath.'
}

$hooksPath = (git config --local --get core.hooksPath).Trim()
if ($hooksPath -ne '.githooks') {
    throw "Thiết lập Git hook: cần .githooks, nhưng nhận được $hooksPath."
}

Write-Host "Đã bật Git hook cho clone này: $hooksPath"
