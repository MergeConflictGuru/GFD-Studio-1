function Get-GfdStudioPathImpact {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $normalizedPath = $Path.Trim() -replace '\\', '/'

    $nonBuildPath = $normalizedPath -match '^(?:README\.md|AGENTS\.md|\.editorconfig|\.gitignore|\.githooks/|\.github/(?!workflows/)|\.vscode/|fetch-latest-build\.ps1|create-build-delta\.ps1|build-inputs\.ps1|build-release\.ps1|install-git-hooks\.ps1|run-latest-build\.bat|appveyor\.yml|Resources/|GFDLibrary\.Tests/|GFDLibrary\.Conversion\.Core/|GFDLibrary\.Conversion\.FbxSdk\.Tests/|Dependencies/Scarlet/(?:Scarlet\.IO\.CompressionFormats|Scarlet\.IO\.ContainerFormats|ScarletTestApp|ScarletUnitTests|ScarletWinTest)/)'
    if ($nonBuildPath) {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $true
            Asset    = $false
            Files    = @()
        }
    }

    $fullBuildPath = $normalizedPath -match '^(?:\.github/workflows/|\.gitmodules$|GFDStudio\.sln$|Directory\.Build\.props$|global\.json$|NuGet\.config$|Directory\.Packages\.props$|.*\.(?:csproj|vcxproj|props|targets|sln|packages\.config)$)'
    if ($fullBuildPath) {
        return [pscustomobject]@{
            Full     = $true
            NonBuild = $false
            Asset    = $false
            Files    = @()
        }
    }

    if ($normalizedPath -match '^GFDStudio/Properties/PublishProfiles/') {
        return [pscustomobject]@{
            Full     = $true
            NonBuild = $false
            Asset    = $false
            Files    = @()
        }
    }

    if ($normalizedPath -match '^GFDStudio/Properties/launchSettings\.json$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $true
            Asset    = $false
            Files    = @()
        }
    }

    if ($normalizedPath -match '^GFDStudio/(?:app_data|Presets)/(?<asset>.+)$') {
        $assetRoot = $normalizedPath.Substring('GFDStudio/'.Length).Split('/')[0]
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $true
            Files    = @("$assetRoot/$($Matches.asset)")
        }
    }

    if ($normalizedPath -match '^GFDStudio/.*\.(?:cs|resx)$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @('GFDStudio.dll')
        }
    }

    if ($normalizedPath -match '^GFDStudio/(?:App\.config|app\.manifest|Properties/.*\.(?:pubxml|json))$') {
        return [pscustomobject]@{
            Full     = $true
            NonBuild = $false
            Asset    = $false
            Files    = @()
        }
    }

    if ($normalizedPath -match '^GFDLibrary/.*\.cs$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @(
                'GFDLibrary.dll',
                'GFDStudio.dll',
                'GFDLibrary.Rendering.OpenGL.dll',
                'GFDLibrary.Conversion.AssimpNet.dll',
                'GFDLibrary.Conversion.FbxSdk.dll',
                'GFDLibrary.Conversion.FbxSdk.pdb'
            )
        }
    }

    if ($normalizedPath -match '^GFDLibrary\.Rendering\.OpenGL/.*\.cs$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @('GFDLibrary.Rendering.OpenGL.dll', 'GFDStudio.dll')
        }
    }

    if ($normalizedPath -match '^GFDLibrary\.Conversion\.AssimpNet/.*\.cs$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @('GFDLibrary.Conversion.AssimpNet.dll', 'GFDStudio.dll')
        }
    }

    if ($normalizedPath -match '^GFDLibrary\.Conversion\.FbxSdk/.*\.(?:cpp|h|rc|ico)$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @('GFDLibrary.Conversion.FbxSdk.dll', 'GFDLibrary.Conversion.FbxSdk.pdb', 'GFDStudio.dll')
        }
    }

    if ($normalizedPath -match '^Dependencies/Scarlet/Scarlet/.*\.cs$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @(
                'Scarlet.dll',
                'GFDLibrary.dll',
                'GFDStudio.dll',
                'GFDLibrary.Rendering.OpenGL.dll',
                'GFDLibrary.Conversion.AssimpNet.dll',
                'GFDLibrary.Conversion.FbxSdk.dll',
                'GFDLibrary.Conversion.FbxSdk.pdb'
            )
        }
    }

    if ($normalizedPath -match '^Dependencies/Scarlet/Scarlet\.IO\.ImageFormats/.*\.cs$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @(
                'Scarlet.IO.ImageFormats.dll',
                'GFDLibrary.dll',
                'GFDStudio.dll',
                'GFDLibrary.Rendering.OpenGL.dll',
                'GFDLibrary.Conversion.AssimpNet.dll',
                'GFDLibrary.Conversion.FbxSdk.dll',
                'GFDLibrary.Conversion.FbxSdk.pdb'
            )
        }
    }

    if ($normalizedPath -match '^Dependencies/tga-decoder-cs/.*\.cs$') {
        return [pscustomobject]@{
            Full     = $false
            NonBuild = $false
            Asset    = $false
            Files    = @(
                'TgaDecoderTest.dll',
                'GFDLibrary.dll',
                'GFDStudio.dll',
                'GFDLibrary.Rendering.OpenGL.dll',
                'GFDLibrary.Conversion.AssimpNet.dll',
                'GFDLibrary.Conversion.FbxSdk.dll',
                'GFDLibrary.Conversion.FbxSdk.pdb'
            )
        }
    }

    return [pscustomobject]@{
        Full     = $true
        NonBuild = $false
        Asset    = $false
        Files    = @()
    }
}

function Test-GfdStudioBuildRequired {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$SourceCommit,
        [Parameter(Mandatory)]
        [string]$TargetCommit
    )

    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or $SourceCommit -match '^0+$') {
        return $true
    }

    $sourceRef = "$SourceCommit^{commit}"
    $targetRef = "$TargetCommit^{commit}"
    & git -C $RepositoryRoot rev-parse --verify $sourceRef 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return $true
    }

    & git -C $RepositoryRoot rev-parse --verify $targetRef 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return $true
    }

    $changedPaths = @(& git -C $RepositoryRoot diff --name-only --diff-filter=ACDMRT $SourceCommit $TargetCommit)
    if ($LASTEXITCODE -ne 0) {
        return $true
    }

    foreach ($changedPath in $changedPaths) {
        $impact = Get-GfdStudioPathImpact -Path $changedPath
        if ($impact.Full -or (-not $impact.NonBuild)) {
            return $true
        }
    }

    return $false
}

function Get-GfdStudioLastBuiltCommit {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$StartingCommit
    )

    $candidate = $StartingCommit
    while (-not [string]::IsNullOrWhiteSpace($candidate) -and $candidate -notmatch '^0+$') {
        $parent = (& git -C $RepositoryRoot rev-parse "$candidate^" 2>$null | Select-Object -First 1)
        if ($null -eq $parent) {
            return $null
        }

        $parent = $parent.Trim()
        if (Test-GfdStudioBuildRequired -RepositoryRoot $RepositoryRoot `
                -SourceCommit $parent -TargetCommit $candidate) {
            return $candidate
        }

        $candidate = $parent
    }

    return $null
}
