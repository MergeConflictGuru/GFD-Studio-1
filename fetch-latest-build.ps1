[CmdletBinding()]
param(
    [string]$Repository,
    [string]$Workflow = 'build-gfdstudio.yml',
    [string]$Artifact = 'gfdstudio-windows-x64',
    [string]$Branch,
    [string]$OutputDirectory,
    [string]$Commit,
    [string]$SourceCommit,
    [switch]$Watch,
    [switch]$Launch,
    [ValidateRange(10, 86400)]
    [int]$IntervalSeconds = 30
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'build-inputs.ps1')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'GFDStudio-binary'
}

function Get-RunningGfdStudioProcesses {
    param(
        [string]$ExecutablePath
    )

    $fullExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    return @(Get-Process -Name 'GFDStudio' -ErrorAction SilentlyContinue | Where-Object {
        try {
            [IO.Path]::GetFullPath($_.MainModule.FileName) -ieq $fullExecutablePath
        }
        catch {
            $false
        }
    })
}

function Stop-GfdStudioProcesses {
    param(
        [System.Diagnostics.Process[]]$Processes
    )

    foreach ($process in $Processes) {
        if ($process.HasExited) {
            continue
        }

        Write-Host "Closing running GFD Studio process $($process.Id) before replacing the binary."
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}

function Start-GfdStudio {
    param(
        [string]$ExecutablePath
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Cannot launch GFD Studio because '$ExecutablePath' was not found."
    }

    Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) | Out-Null
    Write-Host "Started GFD Studio: $ExecutablePath"
}

function Get-LocalBuildCommit {
    param(
        [string]$BuildDirectory
    )

    $metadataPath = Join-Path $BuildDirectory 'gfdstudio-build.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        return $null
    }

    try {
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        if ($metadata.format -eq 1 -and $metadata.commit -match '^[0-9a-fA-F]{40}$') {
            return $metadata.commit.ToLowerInvariant()
        }
    }
    catch {
        Write-Warning "Could not read local GFD Studio build metadata: $($_.Exception.Message)"
    }

    return $null
}

function Get-SafeRelativePath {
    param(
        [string]$Path
    )

    $normalizedPath = $Path.Trim() -replace '\\', '/'
    if ([IO.Path]::IsPathRooted($normalizedPath) -or $normalizedPath -match '(^|/)\.\.(?:/|$)') {
        throw "The delta contains an unsafe path: '$Path'."
    }

    return $normalizedPath
}

function Get-GitRemoteUrl {
    param(
        [string]$RemoteName
    )

    # Capture the native command's exit code before running any PowerShell
    # pipeline commands. This keeps repository discovery reliable when the
    # script is launched by VS Code or a Git hook.
    $remoteUrls = @(& git -C $PSScriptRoot remote get-url $RemoteName 2>$null)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0) {
        return $null
    }

    return $remoteUrls |
        ForEach-Object { $_.ToString().Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Repository)) {
    $remoteName = 'origin'
    $remoteUrl = Get-GitRemoteUrl -RemoteName $remoteName
    if ([string]::IsNullOrWhiteSpace($remoteUrl)) {
        $remoteName = 'upstream'
        $remoteUrl = Get-GitRemoteUrl -RemoteName $remoteName
    }
    if ([string]::IsNullOrWhiteSpace($remoteUrl)) {
        throw 'Repository was not specified and neither the local origin nor upstream remote could be read.'
    }

    $remoteUrl = $remoteUrl.Trim()
    if ($remoteUrl -match 'github\.com[/:](?<repository>[^/]+/[^/]+?)(?:\.git)?$') {
        $Repository = $Matches.repository
    }
    else {
        throw "The $remoteName remote is not a GitHub repository: $remoteUrl"
    }
}

Write-Host "Using GitHub repository: $Repository"

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = (& git -C $PSScriptRoot branch --show-current 2>$null | Select-Object -First 1)
    if ($null -ne $Branch) {
        $Branch = $Branch.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($Branch)) {
        $Branch = 'master'
    }
}

Write-Host "Using branch: $Branch"

$headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'GFD-Studio-build-fetcher'
}

$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
if ($null -ne $ghCommand) {
    $githubToken = (& gh auth token --hostname github.com 2>$null | Select-Object -First 1)
    if ($null -ne $githubToken) {
        $githubToken = $githubToken.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($githubToken)) {
        $headers.Authorization = "Bearer $githubToken"
    }
}

function Get-LatestSuccessfulRun {
    param(
        [hashtable]$RequestHeaders,
        [string]$Repo,
        [string]$WorkflowFile,
        [string]$Ref
    )

    $encodedRef = [Uri]::EscapeDataString($Ref)
    $runListUri = "https://api.github.com/repos/$Repo/actions/workflows/$WorkflowFile/runs?branch=$encodedRef&status=success&per_page=20"
    $runs = Invoke-RestMethod -Uri $runListUri -Headers $RequestHeaders

    return $runs.workflow_runs |
        Where-Object { $_.event -in @('push', 'workflow_dispatch') } |
        Sort-Object run_number -Descending |
        Select-Object -First 1
}

function Get-WorkflowRunForCommit {
    param(
        [hashtable]$RequestHeaders,
        [string]$Repo,
        [string]$WorkflowFile,
        [string]$Ref,
        [string]$CommitSha
    )

    $encodedRef = [Uri]::EscapeDataString($Ref)
    $runListUri = "https://api.github.com/repos/$Repo/actions/workflows/$WorkflowFile/runs?branch=$encodedRef&per_page=100"
    $runs = Invoke-RestMethod -Uri $runListUri -Headers $RequestHeaders

    return $runs.workflow_runs |
        Where-Object {
            $_.event -in @('push', 'workflow_dispatch') -and
            $_.head_sha -eq $CommitSha
        } |
        Sort-Object run_number -Descending |
        Select-Object -First 1
}

function Get-RunArtifactInfo {
    param(
        [hashtable]$RequestHeaders,
        [string]$Repo,
        [object]$Run,
        [string]$ArtifactName
    )

    $artifactsUri = "https://api.github.com/repos/$Repo/actions/runs/$($Run.id)/artifacts?per_page=100"
    $artifacts = Invoke-RestMethod -Uri $artifactsUri -Headers $RequestHeaders
    return $artifacts.artifacts |
        Where-Object { $_.name -eq $ArtifactName -and -not $_.expired } |
        Select-Object -First 1
}

function Download-BuildArtifact {
    param(
        [hashtable]$RequestHeaders,
        [string]$Repo,
        [string]$ArtifactName,
        [string]$DestinationDirectory,
        [object]$Run
    )

    $artifactInfo = Get-RunArtifactInfo -RequestHeaders $RequestHeaders -Repo $Repo `
        -Run $Run -ArtifactName $ArtifactName

    if (-not $artifactInfo) {
        throw "Artifact '$ArtifactName' was not found on successful run $($Run.id)."
    }

    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "gfdstudio-artifact-$([Guid]::NewGuid().ToString('N'))"
    $archivePath = Join-Path $temporaryDirectory 'artifact.zip'
    $expandedDirectory = Join-Path $temporaryDirectory 'expanded'
    $targetExecutablePath = Join-Path $DestinationDirectory 'GFDStudio.exe'

    try {
        New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
        Invoke-WebRequest -Uri $artifactInfo.archive_download_url -Headers $RequestHeaders -OutFile $archivePath
        Expand-Archive -LiteralPath $archivePath -DestinationPath $expandedDirectory -Force

        $executable = Get-ChildItem -LiteralPath $expandedDirectory -Filter 'GFDStudio.exe' -File -Recurse |
            Select-Object -First 1
        if (-not $executable) {
            $nestedArchives = Get-ChildItem -LiteralPath $expandedDirectory -Filter '*.zip' -File -Recurse
            foreach ($nestedArchive in $nestedArchives) {
                $nestedDirectory = Join-Path $expandedDirectory ([IO.Path]::GetFileNameWithoutExtension($nestedArchive.Name))
                Expand-Archive -LiteralPath $nestedArchive.FullName -DestinationPath $nestedDirectory -Force
            }

            $executable = Get-ChildItem -LiteralPath $expandedDirectory -Filter 'GFDStudio.exe' -File -Recurse |
                Select-Object -First 1
        }

        if (-not $executable) {
            throw 'Downloaded artifact does not contain GFDStudio.exe.'
        }

        $runningProcesses = @(Get-RunningGfdStudioProcesses -ExecutablePath $targetExecutablePath)
        if ($runningProcesses.Count -gt 0) {
            Stop-GfdStudioProcesses -Processes $runningProcesses
        }

        if (Test-Path -LiteralPath $DestinationDirectory) {
            Remove-Item -LiteralPath $DestinationDirectory -Recurse -Force
        }
        New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $executable.Directory.FullName '*') -Destination $DestinationDirectory -Recurse -Force

        $buildMetadata = [ordered]@{
            format = 1
            commit = $Run.head_sha
        }
        $buildMetadata | ConvertTo-Json | Set-Content `
            -LiteralPath (Join-Path $DestinationDirectory 'gfdstudio-build.json') -Encoding UTF8

        $shortSha = if ($Run.head_sha.Length -ge 7) { $Run.head_sha.Substring(0, 7) } else { $Run.head_sha }
        Write-Host "Downloaded GFD Studio run $($Run.run_number) ($shortSha)."
        Write-Host "Binary: $DestinationDirectory\GFDStudio.exe"

        if ($runningProcesses.Count -gt 0 -or $Launch) {
            Start-GfdStudio -ExecutablePath $targetExecutablePath
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Download-BuildDeltaArtifact {
    param(
        [hashtable]$RequestHeaders,
        [string]$Repo,
        [string]$ArtifactName,
        [string]$DestinationDirectory,
        [object]$Run,
        [string]$SourceCommit
    )

    $artifactInfo = Get-RunArtifactInfo -RequestHeaders $RequestHeaders -Repo $Repo `
        -Run $Run -ArtifactName $ArtifactName
    if (-not $artifactInfo) {
        return $false
    }

    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "gfdstudio-delta-$([Guid]::NewGuid().ToString('N'))"
    $archivePath = Join-Path $temporaryDirectory 'artifact.zip'
    $expandedDirectory = Join-Path $temporaryDirectory 'expanded'
    $targetExecutablePath = Join-Path $DestinationDirectory 'GFDStudio.exe'

    try {
        New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
        Invoke-WebRequest -Uri $artifactInfo.archive_download_url -Headers $RequestHeaders -OutFile $archivePath
        Expand-Archive -LiteralPath $archivePath -DestinationPath $expandedDirectory -Force

        $manifestFile = Get-ChildItem -LiteralPath $expandedDirectory -Filter 'gfdstudio-delta.json' -File -Recurse |
            Select-Object -First 1
        if (-not $manifestFile) {
            return $false
        }

        $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
        if ($manifest.format -ne 1 -or $manifest.fullRequired -or
            $manifest.sourceCommit -ine $SourceCommit -or $manifest.targetCommit -ine $Run.head_sha) {
            return $false
        }

        $copyOperations = @()
        foreach ($file in @($manifest.files)) {
            $relativePath = Get-SafeRelativePath -Path ([string]$file.path)
            $sourcePath = Join-Path $manifestFile.Directory.FullName ($relativePath -replace '/', '\')
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "The delta is missing '$relativePath'."
            }

            if ($file.sha256) {
                $actualHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
                if ($actualHash -ine ([string]$file.sha256)) {
                    throw "The delta hash did not match for '$relativePath'."
                }
            }

            $copyOperations += [pscustomobject]@{
                RelativePath = $relativePath
                SourcePath   = $sourcePath
            }
        }

        $deleteOperations = @()
        foreach ($file in @($manifest.deletedFiles)) {
            $deleteOperations += Get-SafeRelativePath -Path ([string]$file)
        }

        if (-not (Test-Path -LiteralPath $DestinationDirectory -PathType Container) -or
            -not (Test-Path -LiteralPath $targetExecutablePath -PathType Leaf)) {
            return $false
        }

        $runningProcesses = @(Get-RunningGfdStudioProcesses -ExecutablePath $targetExecutablePath)
        if ($runningProcesses.Count -gt 0) {
            Stop-GfdStudioProcesses -Processes $runningProcesses
        }

        foreach ($relativePath in $deleteOperations) {
            $destinationPath = Join-Path $DestinationDirectory ($relativePath -replace '/', '\')
            if (Test-Path -LiteralPath $destinationPath) {
                Remove-Item -LiteralPath $destinationPath -Force
            }
        }

        foreach ($operation in $copyOperations) {
            $destinationPath = Join-Path $DestinationDirectory ($operation.RelativePath -replace '/', '\')
            $destinationParent = Split-Path -Parent $destinationPath
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
            Copy-Item -LiteralPath $operation.SourcePath -Destination $destinationPath -Force
        }

        $buildMetadata = [ordered]@{
            format = 1
            commit = $Run.head_sha
        }
        $buildMetadata | ConvertTo-Json | Set-Content `
            -LiteralPath (Join-Path $DestinationDirectory 'gfdstudio-build.json') -Encoding UTF8

        $shortSha = if ($Run.head_sha.Length -ge 7) { $Run.head_sha.Substring(0, 7) } else { $Run.head_sha }
        Write-Host "Applied GFD Studio delta to run $($Run.run_number) ($shortSha)."
        Write-Host "Binary: $DestinationDirectory\GFDStudio.exe"

        if ($runningProcesses.Count -gt 0 -or $Launch) {
            Start-GfdStudio -ExecutablePath $targetExecutablePath
        }

        return $true
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Download-PreferredBuild {
    param(
        [hashtable]$RequestHeaders,
        [string]$Repo,
        [string]$FullArtifactName,
        [string]$DestinationDirectory,
        [object]$Run
    )

    $targetExecutablePath = Join-Path $DestinationDirectory 'GFDStudio.exe'
    $localCommit = Get-LocalBuildCommit -BuildDirectory $DestinationDirectory
    if ($localCommit -ieq $Run.head_sha -and
        (Test-Path -LiteralPath $targetExecutablePath -PathType Leaf)) {
        Write-Host "GFD Studio run $($Run.run_number) ($($Run.head_sha.Substring(0, 7))) is already installed."
        if ($Launch -and @(Get-RunningGfdStudioProcesses -ExecutablePath $targetExecutablePath).Count -eq 0) {
            Start-GfdStudio -ExecutablePath $targetExecutablePath
        }
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($localCommit)) {
        $deltaArtifactName = "gfdstudio-delta-$localCommit-$($Run.head_sha)"
        if (Download-BuildDeltaArtifact -RequestHeaders $RequestHeaders -Repo $Repo `
                -ArtifactName $deltaArtifactName -DestinationDirectory $DestinationDirectory `
                -Run $Run -SourceCommit $localCommit) {
            return
        }

        Write-Host "No usable delta from $localCommit to $($Run.head_sha); downloading the full archive."
    }

    Download-BuildArtifact -RequestHeaders $RequestHeaders -Repo $Repo -ArtifactName $FullArtifactName `
        -DestinationDirectory $DestinationDirectory -Run $Run
}

if (-not [string]::IsNullOrWhiteSpace($Commit) -and $Watch) {
    throw 'The -Commit and -Watch options cannot be used together.'
}

if (-not [string]::IsNullOrWhiteSpace($Commit)) {
    $shortCommit = if ($Commit.Length -ge 7) { $Commit.Substring(0, 7) } else { $Commit }
    Write-Host "[wait] Waiting for $Repository/$Workflow run for commit $shortCommit on branch '$Branch'."

    $lastStatus = $null
    while ($true) {
        try {
            $run = Get-WorkflowRunForCommit -RequestHeaders $headers -Repo $Repository `
                -WorkflowFile $Workflow -Ref $Branch -CommitSha $Commit
        }
        catch {
            Write-Warning "[wait] GitHub check failed: $($_.Exception.Message). Will retry."
            Start-Sleep -Seconds $IntervalSeconds
            continue
        }

        if (-not $run) {
            $comparisonSourceCommit = $SourceCommit
            if ([string]::IsNullOrWhiteSpace($comparisonSourceCommit)) {
                $comparisonSourceCommit = (& git -C $PSScriptRoot rev-parse "$Commit^" 2>$null | Select-Object -First 1)
                if ($null -ne $comparisonSourceCommit) {
                    $comparisonSourceCommit = $comparisonSourceCommit.Trim()
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($comparisonSourceCommit) -and
                -not (Test-GfdStudioBuildRequired -RepositoryRoot $PSScriptRoot `
                    -SourceCommit $comparisonSourceCommit -TargetCommit $Commit)) {
                Write-Host "[skip] Commit $shortCommit does not change the built application; keeping the local binary."
                exit 0
            }

            if ($lastStatus -ne 'not-found') {
                Write-Host '[wait] Pipeline run has not appeared yet. Waiting...'
                $lastStatus = 'not-found'
            }

            Start-Sleep -Seconds $IntervalSeconds
            continue
        }

        $runStatus = "$($run.id):$($run.status):$($run.conclusion)"
        if ($lastStatus -ne $runStatus) {
            $conclusion = if ([string]::IsNullOrWhiteSpace($run.conclusion)) { '' } else { ", conclusion $($run.conclusion)" }
            Write-Host "[wait] Run #$($run.run_number): $($run.status)$conclusion"
            $lastStatus = $runStatus
        }

        if ($run.status -eq 'completed') {
            if ($run.conclusion -ne 'success') {
                throw "GitHub Actions run #$($run.run_number) finished with conclusion '$($run.conclusion)'."
            }

            Download-PreferredBuild -RequestHeaders $headers -Repo $Repository -FullArtifactName $Artifact `
                -DestinationDirectory $OutputDirectory -Run $run
            exit 0
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}

if (-not $Watch) {
    $run = Get-LatestSuccessfulRun -RequestHeaders $headers -Repo $Repository -WorkflowFile $Workflow -Ref $Branch
    if (-not $run) {
        throw "No successful $Workflow run was found for $Repository on branch $Branch."
    }

    Download-PreferredBuild -RequestHeaders $headers -Repo $Repository -FullArtifactName $Artifact `
        -DestinationDirectory $OutputDirectory -Run $run
    exit 0
}

Write-Host "[watch] Monitoring $Repository/$Workflow on branch '$Branch' every $IntervalSeconds seconds."
Write-Host '[watch] Press Ctrl+C to stop.'
Write-Host '[watch] Ready.'

$lastObservedRunId = $null
$lastDownloadedRunId = $null
while ($true) {
    try {
        $run = Get-LatestSuccessfulRun -RequestHeaders $headers -Repo $Repository -WorkflowFile $Workflow -Ref $Branch

        if (-not $run) {
            if ($lastObservedRunId -ne 'none') {
                Write-Host "[watch] No successful run found yet for branch '$Branch'."
                $lastObservedRunId = 'none'
            }
        }
        else {
            if ($lastObservedRunId -ne $run.id) {
                Write-Host "[watch] Latest successful run: #$($run.run_number) ($($run.head_sha.Substring(0, 7)))."
                $lastObservedRunId = $run.id
            }

            if ($lastDownloadedRunId -ne $run.id) {
                try {
                    Write-Host "[watch] Fetching run #$($run.run_number)..."
                    Download-PreferredBuild -RequestHeaders $headers -Repo $Repository -FullArtifactName $Artifact `
                        -DestinationDirectory $OutputDirectory -Run $run
                    $lastDownloadedRunId = $run.id
                }
                catch {
                    Write-Warning "[watch] Fetch failed: $($_.Exception.Message). Will retry."
                }
            }
        }
    }
    catch {
        Write-Warning "[watch] GitHub check failed: $($_.Exception.Message). Will retry."
    }

    Start-Sleep -Seconds $IntervalSeconds
}
