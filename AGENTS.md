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

The build output is written to `GFDStudio\bin\x64\Release\net8.0-windows\win-x64`. When the final executable is not in use, the script copies that output to `GFDStudio-binary`. To build and launch it, run:

```powershell
.\build-release.ps1 -Run
```

The script uses Visual Studio MSBuild and the normal `GFDStudio\GFDStudio.csproj` dependency graph. It automatically uses an available Unity-bundled .NET SDK and FBX SDK when available. MSBuild decides which projects and source files need rebuilding; there is no timestamp-based partial-build logic.

The normal graph includes the managed libraries, OpenGL renderer, Assimp conversion library, and native FBX C++/CLI project. The self-contained `win-x64` Release build is copied to `GFDStudio-binary` only when its executable and assembly are available for replacement. If either file is in use, leave the new build in the normal build output and do not update the final binary directory.

Never terminate, kill, or force-stop a process just because its executable is found or in use. Do not replace files in a directory while its executable is running.

NuGet and .NET CLI state uses the normal user profile cache. Do not set `DOTNET_CLI_HOME` to a directory in the repository.


# Retargeting

You must check this visually with multiple different characters and animations if there is a chance of breaking something. Its not enough to just code it and say, yes, fixed, moving on without RIGOROUS exhaustive testing on different data.
