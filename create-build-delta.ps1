[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepositoryRoot,
    [Parameter(Mandatory)]
    [string]$PublishDirectory,
    [Parameter(Mandatory)]
    [string]$SourceCommit,
    [Parameter(Mandatory)]
    [string]$TargetCommit,
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'build-inputs.ps1')

function New-DeltaManifest {
    param(
        [bool]$FullRequired,
        [object[]]$Files,
        [string[]]$DeletedFiles,
        [string[]]$Reasons
    )

    [ordered]@{
        format       = 1
        sourceCommit = $SourceCommit
        targetCommit = $TargetCommit
        fullRequired = $FullRequired
        files        = @($Files)
        deletedFiles  = @($DeletedFiles)
        reasons      = @($Reasons)
    }
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$fullRequired = $false
$reasons = [System.Collections.Generic.List[string]]::new()
$outputFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$deletedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

if ([string]::IsNullOrWhiteSpace($SourceCommit) -or $SourceCommit -match '^0+$') {
    $fullRequired = $true
    $reasons.Add('No source commit was supplied.')
}

$sourceRef = "$SourceCommit^{commit}"
$targetRef = "$TargetCommit^{commit}"
if (-not $fullRequired) {
    & git -C $RepositoryRoot rev-parse --verify $sourceRef 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $fullRequired = $true
        $reasons.Add("Source commit '$SourceCommit' is not available in the checkout.")
    }
}

& git -C $RepositoryRoot rev-parse --verify $targetRef 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    $fullRequired = $true
    $reasons.Add("Target commit '$TargetCommit' is not available in the checkout.")
}

if (-not $fullRequired) {
    $lastBuiltCommit = Get-GfdStudioLastBuiltCommit -RepositoryRoot $RepositoryRoot -StartingCommit $SourceCommit
    if ([string]::IsNullOrWhiteSpace($lastBuiltCommit)) {
        $fullRequired = $true
        $reasons.Add("No previous built commit could be found before '$TargetCommit'.")
    }
    else {
        $SourceCommit = $lastBuiltCommit
    }
}

$changedRecords = @()
if (-not $fullRequired) {
    $changedRecords = @(& git -C $RepositoryRoot diff --name-status --diff-filter=ACDMRT --find-renames $SourceCommit $TargetCommit)
    if ($LASTEXITCODE -ne 0) {
        $fullRequired = $true
        $reasons.Add('The source-to-target file list could not be computed.')
    }
}

foreach ($record in $changedRecords) {
    if ([string]::IsNullOrWhiteSpace($record)) {
        continue
    }

    $parts = $record -split "`t"
    $status = $parts[0]
    $paths = @(
        if ($status.StartsWith('R') -or $status.StartsWith('C')) {
            $parts | Select-Object -Skip 1
        }
        else {
            $parts[1]
        }
    )

    for ($pathIndex = 0; $pathIndex -lt $paths.Count; $pathIndex++) {
        $changedPath = $paths[$pathIndex]
        if ([string]::IsNullOrWhiteSpace($changedPath)) {
            continue
        }

        $impact = Get-GfdStudioPathImpact -Path $changedPath
        if ($impact.Full) {
            $fullRequired = $true
            $reasons.Add("Build configuration or an unclassified build input changed: $changedPath")
            continue
        }

        $pathIsDeleted = $status -eq 'D' -or ($status.StartsWith('R') -and $pathIndex -eq 0)
        foreach ($file in $impact.Files) {
            if ($pathIsDeleted -and $impact.Asset) {
                [void]$deletedFiles.Add($file)
                continue
            }

            [void]$outputFiles.Add($file)
        }
    }
}

$fileRecords = [System.Collections.Generic.List[object]]::new()
foreach ($relativePath in ($outputFiles | Sort-Object)) {
    $sourcePath = Join-Path $PublishDirectory ($relativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        $fullRequired = $true
        $reasons.Add("The changed output '$relativePath' was not present in the publish directory.")
        continue
    }

    $fileInfo = Get-Item -LiteralPath $sourcePath
    $fileRecords.Add([ordered]@{
            path   = $relativePath
            bytes  = $fileInfo.Length
            sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        })

    $targetRelativePath = Join-Path $OutputDirectory ($relativePath -replace '/', '\')
    $targetParent = Split-Path -Parent $targetRelativePath
    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $targetRelativePath -Force
    [void]$deletedFiles.Remove($relativePath)
}

$manifest = New-DeltaManifest `
    -FullRequired $fullRequired `
    -Files $fileRecords.ToArray() `
    -DeletedFiles @($deletedFiles | Sort-Object) `
    -Reasons @($reasons)

$manifestPath = Join-Path $OutputDirectory 'gfdstudio-delta.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if ($fullRequired) {
    Write-Host 'Delta is unavailable; the full archive is required.'
    foreach ($reason in $reasons) {
        Write-Host "  $reason"
    }
}
else {
    Write-Host "Created delta from $SourceCommit to $TargetCommit with $($fileRecords.Count) file(s)."
}
