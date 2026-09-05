# Commit guidance

AI should make commits for features and keep each commit focused on one coarse feature.

Every feature commit must be numbered sequentially. Commit messages must always contain exactly two lines:

1. The first line is the commit number.
2. The second line is a super-short feature name.

Push the commit.

## Local Release build

Run the normal incremental Release build from the repository root:

```powershell
.\build-release.ps1
```

The output is written to `GFDStudio-binary`. To build and launch it, run:

```powershell
.\build-release.ps1 -Run
```

The script uses Visual Studio MSBuild and the normal `GFDStudio\GFDStudio.csproj` dependency graph. It automatically uses the Unity-bundled .NET SDK under `Q:\_coding\tools\unity\editor\6000.5.7f1\Editor\Data\DotNetSdk` and the FBX SDK under `Q:\_coding\tools\fbxsdk` when available. MSBuild decides which projects and source files need rebuilding; there is no timestamp-based partial-build logic.

The normal graph includes the managed libraries, OpenGL renderer, Assimp conversion library, and native FBX C++/CLI project. The self-contained `win-x64` Release publish is written to `GFDStudio-binary`, so the published executable carries the .NET runtime with it. Close any running GFD Studio process before replacing the binaries.

NuGet and .NET CLI state uses the normal user profile cache. Do not set `DOTNET_CLI_HOME` to a directory in the repository.
