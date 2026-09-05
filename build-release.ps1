[CmdletBinding()]
param(
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = (Resolve-Path $PSScriptRoot).Path
$binaryDirectory = Join-Path $workspace 'GFDStudio-binary'

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
New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null

$runningProcesses = @(Get-Process -Name GFDStudio -ErrorAction SilentlyContinue)
$restartAfterBuild = $runningProcesses.Count -gt 0
if ($restartAfterBuild) {
    $processIds = $runningProcesses.Id -join ', '
    Write-Host "[release] Stopping GFD Studio (PID $processIds) before replacing the build..."
    $runningProcesses | Stop-Process -Force
    Start-Sleep -Milliseconds 250
}

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

foreach ($outputFile in @('GFDStudio.dll', 'GFDStudio.exe')) {
    $outputPath = Join-Path $binaryDirectory $outputFile
    if (Test-Path -LiteralPath $outputPath) {
        try {
            $stream = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Dispose()
        }
        catch {
            throw "Cannot update $outputPath because it is in use. Close GFD Studio and run the build again."
        }
    }
}

$mainProject = Join-Path $workspace 'GFDStudio\GFDStudio.csproj'
$fbxProject = Join-Path $workspace 'GFDLibrary.Conversion.FbxSdk\GFDLibrary.Conversion.FbxSdk.vcxproj'
$publishDirectory = $binaryDirectory.TrimEnd('\') + '\'
$application = Join-Path $binaryDirectory 'GFDStudio.exe'
$buildSucceeded = $false

try {
    Write-Host '[release] Restoring the normal GFD Studio project graph...'
    Invoke-MSBuild @(
        $mainProject,
        '/t:Restore',
        '/p:RuntimeIdentifier=win-x64',
        '/p:SelfContained=true',
        '/p:Platform=x64',
        "/p:FBXSDKRoot=$fbxSdkRoot",
        '/verbosity:minimal'
    )

    Write-Host '[release] Restoring the native FBX project...'
    Invoke-MSBuild @(
        $fbxProject,
        '/t:Restore',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '/p:RuntimeIdentifier=win-x64',
        '/p:RestoreRuntimeIdentifier=win-x64',
        "/p:FBXSDKRoot=$fbxSdkRoot",
        '/verbosity:minimal'
    )

    Write-Host '[release] Building the normal incremental Release graph into GFDStudio-binary...'
    Invoke-MSBuild @(
        $mainProject,
        '/t:Publish',
        '/p:Configuration=Release',
        '/p:TargetFramework=net8.0-windows',
        '/p:RuntimeIdentifier=win-x64',
        '/p:SelfContained=true',
        "/p:PublishDir=$publishDirectory",
        '/p:Platform=x64',
        "/p:FBXSDKRoot=$fbxSdkRoot",
        '/p:PublishSingleFile=false',
        '/p:DebugType=None',
        '/p:DebugSymbols=false',
        '/verbosity:minimal'
    )

    $assembly = Join-Path $binaryDirectory 'GFDStudio.dll'
    if (-not (Test-Path -LiteralPath $application) -or -not (Test-Path -LiteralPath $assembly)) {
        throw 'The Release build completed without producing GFDStudio.exe and GFDStudio.dll.'
    }

    Write-Host "[release] Built $application"
    $buildSucceeded = $true
}
finally {
    if (($restartAfterBuild -or ($Run -and $buildSucceeded)) -and (Test-Path -LiteralPath $application)) {
        $process = Start-Process -FilePath $application -WorkingDirectory $binaryDirectory -PassThru
        Write-Host "[release] Started GFD Studio (PID $($process.Id))."
    }
}
