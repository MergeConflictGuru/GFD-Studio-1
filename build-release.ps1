[CmdletBinding()]
param(
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = (Resolve-Path $PSScriptRoot).Path
$binaryDirectory = Join-Path $workspace 'GFDStudio-binary'
$temporaryBuildDirectory = Join-Path $env:TEMP 'GFDStudio-release-build'
$dotnetCandidates = @(
    'Q:\_coding\tools\unity\editor\6000.5.7f1\Editor\Data\DotNetSdk\dotnet.exe',
    'C:\Program Files\dotnet\dotnet.exe'
)

$dotnetPath = $dotnetCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if (-not $dotnetPath) {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($dotnetCommand) {
        $dotnetPath = $dotnetCommand.Source
    }
}

if (-not $dotnetPath) {
    throw 'A .NET SDK could not be found. Install .NET 8 or update the dotnet path in build-release.ps1.'
}

$dotnetRoot = Split-Path -Parent $dotnetPath
$unitySdkRoot = Join-Path $dotnetRoot 'sdk\8.0.318'
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"

if (Test-Path -LiteralPath $unitySdkRoot) {
    $env:MSBuildSDKsPath = Join-Path $unitySdkRoot 'Sdks'
}
else {
    Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue
}

$env:DOTNET_CLI_HOME = Join-Path $temporaryBuildDirectory 'dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $dotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$requiredBinaryInputs = @(
    'AssimpNet.dll',
    'BCnEncoder.dll',
    'BCnEncoder.NET.ImageSharp.dll',
    'CSharpImageLibrary.dll',
    'GFDLibrary.Conversion.AssimpNet.dll',
    'GFDLibrary.Conversion.FbxSdk.dll',
    'GFDLibrary.Rendering.OpenGL.dll',
    'MetroSet UI.dll',
    'Newtonsoft.Json.dll',
    'Ookii.Dialogs.Wpf.dll',
    'OpenTK.dll',
    'OpenTK.GLControl.dll',
    'Scarlet.dll',
    'Scarlet.IO.ImageFormats.dll',
    'SixLabors.ImageSharp.dll',
    'System.Drawing.Common.dll',
    'TgaDecoderTest.dll',
    'UsefulThings.dll',
    'YamlDotNet.dll'
)

$missingInputs = $requiredBinaryInputs |
    Where-Object { -not (Test-Path -LiteralPath (Join-Path $binaryDirectory $_)) }
if ($missingInputs) {
    throw "GFDStudio-binary is missing required prebuilt libraries: $($missingInputs -join ', ')"
}

foreach ($outputFile in @('GFDLibrary.dll', 'GFDStudio.dll', 'GFDStudio.exe')) {
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

$libraryProject = Join-Path $workspace 'GFDLibrary.MainOnly.csproj'
$libraryBinary = Join-Path $binaryDirectory 'GFDLibrary.dll'
$librarySources = Get-ChildItem -LiteralPath (Join-Path $workspace 'GFDLibrary') -Recurse -File -Filter '*.cs'
$libraryTimestamp = if (Test-Path -LiteralPath $libraryBinary) { (Get-Item $libraryBinary).LastWriteTimeUtc } else { [DateTime]::MinValue }
$librarySourceChanges = @($librarySources | Where-Object { $_.LastWriteTimeUtc -gt $libraryTimestamp })
$libraryNeedsBuild =
    (-not (Test-Path -LiteralPath $libraryBinary)) -or
    ($librarySourceChanges.Count -gt 0)

if ($libraryNeedsBuild) {
    Write-Host '[release] Building the changed managed GFDLibrary dependency...'
    $libraryStagingDirectory = Join-Path $temporaryBuildDirectory 'GFDLibrary'
    New-Item -ItemType Directory -Force -Path $libraryStagingDirectory | Out-Null
    $libraryOutput = $libraryStagingDirectory.TrimEnd('\') + '\'

    Invoke-DotNet @('restore', $libraryProject, '--ignore-failed-sources', '--nologo', '-v:minimal')
    Invoke-DotNet @('build', $libraryProject, '--no-restore', '--nologo', '-c', 'Release', "-p:OutDir=$libraryOutput")

    foreach ($fileName in @('GFDLibrary.dll', 'GFDLibrary.pdb', 'GFDLibrary.deps.json')) {
        $builtFile = Join-Path $libraryStagingDirectory $fileName
        if (Test-Path -LiteralPath $builtFile) {
            Copy-Item -LiteralPath $builtFile -Destination (Join-Path $binaryDirectory $fileName) -Force
        }
    }
}
else {
    Write-Host '[release] GFDLibrary is up to date; keeping the prebuilt library.'
}

$mainProject = Join-Path $workspace 'GFDStudio.MainOnly.csproj'
$mainOutput = $binaryDirectory.TrimEnd('\') + '\'
Write-Host '[release] Building GFDStudio into GFDStudio-binary...'
Invoke-DotNet @('restore', $mainProject, '--ignore-failed-sources', '--nologo', '-v:minimal')
Invoke-DotNet @('build', $mainProject, '--no-restore', '--nologo', '-c', 'Release', '-p:SelfContained=false', "-p:OutDir=$mainOutput")

$application = Join-Path $binaryDirectory 'GFDStudio.exe'
$assembly = Join-Path $binaryDirectory 'GFDStudio.dll'
if (-not (Test-Path -LiteralPath $application) -or -not (Test-Path -LiteralPath $assembly)) {
    throw 'The Release build completed without producing GFDStudio.exe and GFDStudio.dll.'
}

Write-Host "[release] Built $application"

if ($Run) {
    $process = Start-Process -FilePath $dotnetPath -ArgumentList @($assembly) -WorkingDirectory $binaryDirectory -PassThru
    Write-Host "[release] Started GFD Studio (PID $($process.Id))."
}
