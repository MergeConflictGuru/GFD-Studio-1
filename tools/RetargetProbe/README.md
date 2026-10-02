# AniMatch rig bias audit

Build from the repository root using the normal project graph:

```powershell
.\build-release.ps1
.\tools\RetargetProbe\bin\x64\Release\net8.0-windows\win-x64\RetargetProbe.exe --animatch-audit --audit-output=artifacts/animatch-audit/current
```

The audit loads the actual application assembly from the normal Release output. Override it with `--studio=path/to/GFDStudio.dll`. Override the default `M:\_P_backup\all models` corpus root with `--data-root=...` (quote the argument if it contains spaces).

Synthetic checks preserve physical motion while independently changing motion-root bone axes, joint axes, model orientation, body scale, and cosmetic extent. They also check that different poses and joint rotations remain distinguishable, and that an equivalent motion on a different rig wins over a different motion in both the in-memory index and its mapped cache.

Real-data checks use P5D/P5R Joker, Ann, Makoto and Haru models, two clips from both games per character, and three frames per clip. P5D clips use the application's split-animation composition. The audit writes channel distances, skinned frame comparisons, and front/side views of the normalized matching skeletons. Retargeted frame pairs are diagnostic comparisons; their remaining differences include the existing retargeter's behavior and different body proportions.

To check cache invalidation, pass `--prior-index=path/to/older/fixture-cache.bin` from an audit using the previous descriptor schema. The audit must reject it. Exit code zero means all regression checks passed; real-data renders still require visual review.
