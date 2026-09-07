# AnimationSkeletonReport

Scans `.GMD` files and emits the AniMatch skeleton audit in JSON and Markdown.
The scanner resolves semantic roles through the existing `GFDLibrary` retarget-role resolver, records node hierarchy and transforms, and groups models by canonical topology.

Build from the repository root:

```powershell
dotnet build tools/AnimationSkeletonReport/AnimationSkeletonReport.csproj -c Release
```

Run with one or more dataset/root pairs:

```powershell
dotnet tools/AnimationSkeletonReport/bin/Release/net10.0/AnimationSkeletonReport.dll `
  --dataset P5 "M:\_P_backup\p5 modding\dataR\model\character" `
  --dataset P5D "M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5" `
  --json animatch-skeleton-report.json `
  --markdown animatch-skeleton-report.md
```

The report records absolute source paths because it is an audit of the configured local corpus. No AniMatch index is built by this tool.
