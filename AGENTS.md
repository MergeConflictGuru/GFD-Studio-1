# Commit guidance

AI should make commits for features and keep each commit focused on one coarse feature.

Every feature commit must be numbered sequentially. Commit messages must always contain exactly two lines:

1. The first line is the commit number.
2. The second line is a super-short feature name.

Push the commit.

## Local Release build

Run the supported partial Release build from the repository root:

```powershell
.\build-release.ps1
```

The output is written to `GFDStudio-binary`. To build and launch it, run:

```powershell
.\build-release.ps1 -Run
```

The script uses the Unity-bundled .NET SDK at `Q:\_coding\tools\unity\editor\6000.5.7f1\Editor\Data\DotNetSdk\dotnet.exe` when available. Its temporary MSBuild/NuGet state is under `%TEMP%\GFDStudio-release-build`; do not set `DOTNET_CLI_HOME` to a directory in the repository.

This build compiles `GFDLibrary.MainOnly.csproj` only when `GFDLibrary` sources changed, then compiles `GFDStudio.MainOnly.csproj` against the prebuilt DLLs in `GFDStudio-binary`. It does not rebuild the native FBX library or other prebuilt libraries. Close any running GFD Studio process before replacing the binaries.

Changes under `GFDLibrary.Rendering.OpenGL` are not included by the main-only build. That project is consumed from the prebuilt `GFDStudio-binary\GFDLibrary.Rendering.OpenGL.dll`. If a change adds or removes API such as `ShaderRegistry.mGuideArrowShader`, rebuild and replace that library first, or keep the change on the GFDStudio side.

The full Visual Studio build is separate: it requires the .NET 8 SDK, the FBX SDK 2020.3.7, initialized submodules, and the native FBX project. Use the solution for that full build rather than the `MainOnly` projects.
