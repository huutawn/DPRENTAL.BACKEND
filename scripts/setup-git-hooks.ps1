$ErrorActionPreference = "Stop"

$repoRoot = git rev-parse --show-toplevel
if (-not $repoRoot) {
	throw "This is not a Git repository. Run it inside the repo root."
}

$hooksDir = Join-Path $repoRoot ".git/hooks"
if (-not (Test-Path $hooksDir)) {
	throw "The .git/hooks directory does not exist."
}

$solutionPath = Join-Path $repoRoot "AuthService\AuthService.sln"
$preCommitPath = Join-Path $hooksDir "pre-commit"

$hookScript = @"
#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
`$ErrorActionPreference = 'Stop'

Write-Host "Running pre-commit build..."
& dotnet build "$solutionPath"
if (`$LASTEXITCODE -ne 0) {
	Write-Error "Build failed. Commit aborted."
	exit 1
}
"@

Set-Content -Path $preCommitPath -Value $hookScript -Encoding UTF8

if ($PSVersionTable.PSVersion.Major -ge 6) {
	& chmod +x $preCommitPath
}

Write-Host "Git hook pre-commit created at: $preCommitPath"
Write-Host "Build check: dotnet build \"$solutionPath\""
