[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$hooksPath = Join-Path $PSScriptRoot '.githooks'
if (-not (Test-Path -LiteralPath (Join-Path $hooksPath 'post-push'))) {
    throw "Git hooks directory is missing: $hooksPath"
}

& git -C $PSScriptRoot config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw 'Could not configure Git core.hooksPath.'
}

$configuredHooksPath = & git -C $PSScriptRoot config --local --get core.hooksPath
if ($LASTEXITCODE -ne 0 -or $configuredHooksPath.Trim() -ne '.githooks') {
    throw "Git did not configure the expected hooks path. Current value: '$configuredHooksPath'"
}

Write-Host "Enabled GFD Studio post-push hook: $hooksPath\post-push"
Write-Host 'Future successful pushes will wait for the matching GitHub Actions build and download it to GFDStudio-binary.'
