# GFD Studio
**GFD Studio** is a tool for viewing, editing and converting models in **GMD**/**GFS** format.  
## Latest builds

Every pushed branch is built on GitHub Actions as a self-contained Windows x64 release. The workflow uploads a downloadable `gfdstudio-windows-x64` artifact.

To fetch the latest successful build without compiling locally, run:

```bat
run-latest-build.bat
```

To fetch it without launching a new application instance, run `fetch-latest-build.ps1`. The script reads the GitHub owner/repository and current branch from the local checkout, and places the downloaded files in `GFDStudio-binary`. If that binary is already running, the script closes it before replacing the files and restarts it after a successful fetch. Use `-Launch` to launch the fetched binary when it was not already running, or use `-Repository owner/repository` and `-Branch branch-name` to override discovery.

Fetched builds record their commit in `GFDStudio-binary\gfdstudio-build.json`. When a matching source-to-target delta artifact is available, only changed build outputs are downloaded and overlaid; otherwise the complete archive is used.

The same fetch operation is available in VS Code through `Terminal > Run Task > GFD Studio: Fetch latest binary`; it is also the default build task (`Ctrl+Shift+B`). Use `GFD Studio: Fetch and run latest binary` to launch the downloaded build.

To have every successful build fetched automatically after `git push`, enable the repository's post-push hook once:

```powershell
.\install-git-hooks.ps1
```

After that, a successful push waits for the matching GitHub Actions pipeline to finish and downloads its artifact. Commits that do not affect the built application are skipped. Failed pipelines are reported by the push command and are not downloaded. The same setup is available in VS Code through `Terminal > Run Task > GFD Studio: Enable post-push auto-fetch`.

For a continuously running monitor instead, use `GFD Studio: Watch and auto-fetch latest binary`. It checks every 30 seconds and can be started or stopped from `Terminal > Run Task`. If the downloaded binary is currently running, it is closed before replacement and restarted after the fetch completes.

## Features
- View a rendered preview of the opened model
- Showroom previews automatically retarget normal GAP animations to the selected model in memory when the source character can be identified from the GAP/model names; source files are not modified
- View, export, replace and add **Textures** (automatic conversion to and from PNG/DDS)
- Export, replace and edit **Materials** and their maps & properties
- Export and import models using assimp (automatic conversion to and from DAE/FBX)
## Requirements
- .NET 8 SDK and the FBX SDK 2020.3.7 to build locally
- A videocard that supports at least OpenGL 3.3 to use the model viewer.
(This is required for compiling shaders)
## Building
- Install FBX SDK 2020.3.7 to the standard path (C:\Program Files\Autodesk\FBX\FBX SDK\2020.3.7)
- Clone with `git clone https://github.com/tge-was-taken/GFD-Studio`
- Navigate to the repo, and clone submodules with `git submodule update --init --recursive`
- Open the solution in Visual Studio. You may get pop-ups prompting you to update the submodules' target frameworks. Click update.
## Usage
### Model Conversion
For best results, use the [GMD Maxscript](https://github.com/tge-was-taken/GFD-Studio/blob/master/Resources/GfdImporter/GfdImporter.ms) to import models directly into 3ds Max.
Alternatively, you can use GFD studio to export as DAE, which you can import into your program of choice.
1. Skin your new model to the existing bones and export as an **ASCII 2011 FBX**.
2. In GFD Studio, navigate to **New > Model** and select your FBX.
3. Choose a material preset and change the version if needed. (Hover over the options for more info)
### Replacing Materials and Textures
By default, after importing a new model from FBX, all materials will have the same properties.
You can edit these properties manually, or export them from another model and reuse them.
1. Right click a material and choose Replace.
2. Select a gmt file to replace it with.
3. **Be sure to change the material's name back** to what it was before replacing. It has to match the material's name from the FBX.
4. Also be sure to update the bitmap names for the newly replaced material. **They need to match a texture that's part of the model.**
5. You can right click the Textures or Materials to export or replace them all at once as one file, or add individual textures or materials that are missing.
5. Click the filename at the top of the list to refresh the preview. If a material name is wrong or references a texture that can't be found, parts of the model will be shaded black.

### P5R Animation backport

GFD Studio can now convert P5R animations to P5. There are two ways to do it:

On a single file
1. Load a P5R .GAP file in GFD Studio.
2. Right-click on the animation pack then select **Tools -> Convert to P5**.
3. Export.

On a folder
1. In GFD Studio navigate to **Tools -> Convert P5R animations to P5 in directory**.
2. Select a directory that contains P5R animations.
3. Beware, all the files will be overwritten so make backups.
