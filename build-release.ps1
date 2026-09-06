[CmdletBinding()]
param(
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = (Resolve-Path $PSScriptRoot).Path
$binaryDirectory = Join-Path $workspace 'GFDStudio-binary'
$buildDirectory = Join-Path $workspace 'GFDStudio\bin\x64\Release\net8.0-windows\win-x64'

$dotnetRootCandidates = @(
    'Q:\_coding\tools\unity\editor\6000.5.7f1\Editor\Data\DotNetSdk',
    'C:\Program Files\dotnet'
)
$dotnetRoot = $dotnetRootCandidates |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'dotnet.exe') } |
    Select-Object -First 1

$msbuildCandidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
)
$msbuildPath = $msbuildCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

$fbxSdkCandidates = @(
    'Q:\_coding\tools\fbxsdk',
    'C:\Program Files\Autodesk\FBX\FBX SDK\2020.3.7'
)
$fbxSdkRoot = $fbxSdkCandidates |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_ 'include\fbxsdk.h')) -and
        (Test-Path -LiteralPath (Join-Path $_ 'lib\x64\release\libfbxsdk-md.lib'))
    } |
    Select-Object -First 1

if (-not $dotnetRoot) {
    throw 'A .NET SDK could not be found. Install .NET 8 or update the SDK path in build-release.ps1.'
}
if (-not $msbuildPath) {
    throw 'Visual Studio MSBuild could not be found. Install the Visual Studio desktop C++ workload.'
}
if (-not $fbxSdkRoot) {
    throw 'The FBX SDK 2020.3.7 could not be found. Install it or update the FBX SDK path in build-release.ps1.'
}

$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$sdkDirectories = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot 'sdk') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '8.*' } |
    Sort-Object Name -Descending
if ($sdkDirectories) {
    $env:MSBuildSDKsPath = Join-Path $sdkDirectories[0].FullName 'Sdks'
}
else {
    Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue
}
$env:FBXSDKRoot = $fbxSdkRoot

function Invoke-MSBuild {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $msbuildPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-FileAvailable {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Update-BinaryDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory,
        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    $lockedFiles = @(
        @('GFDStudio.exe', 'GFDStudio.dll') |
            ForEach-Object {
                $destinationPath = Join-Path $DestinationDirectory $_
                if (-not (Test-FileAvailable -Path $destinationPath)) {
                    $destinationPath
                }
            }
    )

    if ($lockedFiles.Count -gt 0) {
        $lockedNames = $lockedFiles |
            ForEach-Object { Split-Path -Leaf $_ } |
            Sort-Object -Unique
        Write-Host "[release] $($lockedNames -join ', ') is in use; leaving the new build in $SourceDirectory and not updating $DestinationDirectory."
        return $false
    }

    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $DestinationDirectory -Recurse -Force
    return $true
}

$application = Join-Path $binaryDirectory 'GFDStudio.exe'
$assembly = Join-Path $binaryDirectory 'GFDStudio.dll'
$buildApplication = Join-Path $buildDirectory 'GFDStudio.exe'
$buildAssembly = Join-Path $buildDirectory 'GFDStudio.dll'
$binaryDirectoryUpdated = $false

$buildSucceeded = $false

try {
    Write-Host '[release] Restoring the normal GFD Studio project graph...'
    Invoke-MSBuild @(
        (Join-Path $workspace 'GFDStudio\GFDStudio.csproj'),
        '/t:Restore',
        '/p:RuntimeIdentifier=win-x64',
        '/p:SelfContained=true',
        '/p:Platform=x64',
        "/p:FBXSDKRoot=$fbxSdkRoot",
        '/verbosity:minimal'
    )

    Write-Host "[release] Building the normal incremental Release graph into $buildDirectory..."
    Invoke-MSBuild @(
        (Join-Path $workspace 'GFDStudio\GFDStudio.csproj'),
        '/t:Build',
        '/p:Configuration=Release',
        '/p:TargetFramework=net8.0-windows',
        '/p:RuntimeIdentifier=win-x64',
        '/p:SelfContained=true',
        '/p:Platform=x64',
        # The checked-in Scarlet projects run NetRevisionTool in pre/post-build
        # events. Those events mutate source files and defeat incremental builds.
        '/p:PreBuildEvent=',
        '/p:PostBuildEvent=',
        "/p:FBXSDKRoot=$fbxSdkRoot",
        '/p:DebugType=None',
        '/p:DebugSymbols=false',
        '/verbosity:minimal'
    )

    if (-not (Test-Path -LiteralPath $buildApplication) -or -not (Test-Path -LiteralPath $buildAssembly)) {
        throw 'The Release build completed without producing GFDStudio.exe and GFDStudio.dll in the normal build output.'
    }

    $binaryDirectoryUpdated = Update-BinaryDirectory -SourceDirectory $buildDirectory -DestinationDirectory $binaryDirectory
    if ($binaryDirectoryUpdated) {
        Write-Host "[release] Updated $application"
    }
    else {
        Write-Host "[release] Built $buildApplication; the final binary directory was left unchanged."
    }

    $buildSucceeded = $true
}
finally {
    if ($Run -and $buildSucceeded -and $binaryDirectoryUpdated -and (Test-Path -LiteralPath $application)) {
        $process = Start-Process -FilePath $application -WorkingDirectory $binaryDirectory -PassThru
        Write-Host "[release] Started GFD Studio (PID $($process.Id))."
    }
}
