# AniMatch skeleton audit

Generated: 2026-09-07T01:00:27.9700702Z

## Executive summary

The scan discovered **2,502** .GMD files, loaded **2,468** model skeletons, and had **34** load failures. **1,989** loaded models meet the report's humanoid threshold. The data forms **31** semantic topology families.

This scanner uses the existing AnimationRetargetMap resolver, so Bip01/P5-style names, P5 Hips names, and P5D naming aliases are analyzed through the same semantic knowledge used by runtime retargeting. Cosmetic and helper nodes do not enter the family signature.

## Datasets

| Dataset | Root | Discovered | Loaded | Failures | Humanoid |
|---|---|---:|---:|---:|---:|
| P5 | M:\_P_backup\p5 modding\dataR\model\character | 2,204 | 2,170 | 34 | 1,822 |
| P5D | M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5 | 298 | 298 | 0 | 167 |

## Canonical mapping and coverage

The proposed canonical representation keeps one motion root, pelvis, two torso slots, an optional neck, head, and the 12 major limb joints. LeftElbow/RightElbow intentionally map to the existing semantic forearm roles. A single-spine rig may resolve the same source node for both torso slots and should be marked as a lower-fidelity family rather than rejected.

| Canonical joint | Existing retarget roles | Required | Loaded-model coverage |
|---|---|:---:|---:|
| Root | motionroot, root, rootnode | yes | 2,415/2,468 (97.9%) |
| Pelvis | hips | yes | 2,074/2,468 (84.0%) |
| LowerSpine | spine, spine1, spine2 | yes | 1,990/2,468 (80.6%) |
| UpperSpine | spine2, spine1, spine | yes | 1,990/2,468 (80.6%) |
| Neck | neck | optional | 2,110/2,468 (85.5%) |
| Head | head | yes | 2,210/2,468 (89.5%) |
| LeftShoulder | leftshoulder | yes | 2,070/2,468 (83.9%) |
| LeftElbow | leftforearm | yes | 2,070/2,468 (83.9%) |
| LeftHand | lefthand | yes | 2,072/2,468 (84.0%) |
| RightShoulder | rightshoulder | yes | 2,070/2,468 (83.9%) |
| RightElbow | rightforearm | yes | 2,070/2,468 (83.9%) |
| RightHand | righthand | yes | 2,072/2,468 (84.0%) |
| LeftHip | leftupleg | yes | 2,074/2,468 (84.0%) |
| LeftKnee | leftleg | yes | 2,074/2,468 (84.0%) |
| LeftFoot | leftfoot | yes | 2,072/2,468 (84.0%) |
| RightHip | rightupleg | yes | 2,072/2,468 (84.0%) |
| RightKnee | rightleg | yes | 2,072/2,468 (84.0%) |
| RightFoot | rightfoot | yes | 2,072/2,468 (84.0%) |

## Major semantic topology families

Families are grouped by canonical-joint presence and nearest canonical ancestry, not by exact complete node lists. Hair, cloth, tails, weapons, and other extra leaves therefore do not split otherwise equivalent body rigs.

| Family | Models | Humanoid | Dataset split | Representative files |
|---|---:|---:|---|---|
| F001 | 805 | 805 | P5: 805 | 0001\c0001_099_00.GMD; 0002\c0002_099_00.GMD; 0003\c0003_000_00.GMD |
| F002 | 531 | 531 | P5: 531 | 0003\c0003_048_00.GMD; 0003\c0003_051_00.GMD; 0003\c0003_061_00.GMD |
| F003 | 372 | 372 | P5: 372 | 0001\c0001_001_00.GMD; 0001\c0001_002_00.GMD; 0001\c0001_003_00.GMD |
| F004 | 167 | 167 | P5D: 167 | pc201_01.GMD; pc201_02.GMD; pc201_03.GMD |
| F005 | 119 | 0 | P5: 119 | 0100\c0100_051_01.GMD; 1001\c1001_072_00.GMD; 1011\c1011_201_01.GMD |
| F006 | 115 | 0 | P5D: 115 | hair\pc201_h00.GMD; hair\pc201_h09.GMD; hair\pc201_h10.GMD |
| F007 | 80 | 0 | P5: 79, P5D: 1 | 3501\c3501_000_00.GMD; 4694\c4694_000_00.GMD; 4920\c4920_000_00.GMD |
| F008 | 53 | 0 | P5: 53 | 3501\c3501_001_00.GMD; 4856\c4856_001_00.GMD; 4856\c4856_002_00.GMD |
| F009 | 43 | 0 | P5: 43 | 4696\c4696_000_00.GMD; 4697\c4697_000_00.GMD; 4698\c4698_000_00.GMD |
| F010 | 30 | 30 | P5: 30 | 2004\c2004_000_00.GMD; 2101\c2101_000_00.GMD; 2101\c2101_051_00.GMD |
| F011 | 27 | 0 | P5: 27 | enemy\0082\em0082.GMD; enemy\0091\em0091.GMD; enemy\0099\em0099.GMD |
| F012 | 21 | 21 | P5: 21 | persona\0183\ps0183.GMD; persona\0183\psz0183.GMD; persona\0184\ps0184.GMD |
| F013 | 21 | 0 | P5: 6, P5D: 15 | 0003\c0003_201_01.GMD; 0003\c0003_201_02.GMD; 0003\c0003_201_03.GMD |
| F014 | 16 | 16 | P5: 16 | enemy\0128\em0128.GMD; enemy\0133\em0133.GMD; enemy\0437\em0437.GMD |
| F015 | 10 | 10 | P5: 10 | 5658\c5658_001_00.GMD; 5907\c5907_000_00.GMD; 5907\c5907_001_00.GMD |
| F016 | 9 | 9 | P5: 9 | 5501\c5501_000_00.GMD; 5501\c5501_101_00.GMD; enemy\0007\em0007.GMD |
| F017 | 7 | 7 | P5: 7 | 5918\c5918_000_00.GMD; 5918\c5918_001_00.GMD; persona\0210\ps0210.GMD |
| F018 | 6 | 6 | P5: 6 | 5936\c5936_000_00.GMD; enemy\0014\em0014.GMD; persona\0014\ps0014.GMD |
| F019 | 6 | 6 | P5: 6 | enemy\0078\em0078.GMD; enemy\0089\em0089.GMD; enemy\0090\em0090.GMD |
| F020 | 5 | 0 | P5: 5 | persona\0185\ps0185.GMD; persona\0185\psz0185.GMD; persona\0195\ps0195.GMD |
| F021 | 4 | 0 | P5: 4 | enemy\0003\em0003.GMD; enemy\0430\em0430.GMD; persona\0003\ps0003.GMD |
| F022 | 3 | 0 | P5: 3 | enemy\0131\em0131.GMD; persona\0131\ps0131.GMD; persona\0312\ps0312.GMD |
| F023 | 3 | 3 | P5: 3 | enemy\0087\em0087.GMD; persona\0087\ps0087.GMD; persona\0460\ps0460.GMD |
| F024 | 2 | 0 | P5: 2 | persona\0299\ps0299.GMD; persona\0299\psz0299.GMD |
| F025 | 2 | 2 | P5: 2 | enemy\0127\em0127.GMD; persona\0127\ps0127.GMD |

Only the 25 largest families are shown here; all 31 families are in JSON.

## P5 vs P5D observations

### P5

- 2,170 models loaded; 1,822 pass the humanoid threshold.
- Most-covered joints: Root (2,117), Head (1,913), Pelvis (1,907), LeftHip (1,907), LeftKnee (1,907).
- Lowest-covered joints: LowerSpine (1,823), UpperSpine (1,823), Neck (1,828), LeftShoulder (1,903), LeftElbow (1,903).
- Semantic families: 29.

### P5D

- 298 models loaded; 167 pass the humanoid threshold.
- Most-covered joints: Root (298), Head (297), Neck (282), Pelvis (167), LowerSpine (167).
- Lowest-covered joints: Pelvis (167), LowerSpine (167), UpperSpine (167), LeftShoulder (167), LeftElbow (167).
- Semantic families: 4.

P5/P5D are not compared by raw names: P5 commonly has a coordinate/helper root followed by Bip01 as the motion root, while P5D commonly exposes root as the motion root under RootNode. The shared semantic roles and the explicit motion-root distinction are precisely the compatibility boundary the canonical index needs.

## Outliers and safely ignored extras

There are **521** load failures or non-standard/low-coverage models. The full list, reasons, and node data are in JSON. Representative outliers:

- P5/0003\c0003_072_00.GMD — missing required canonical joints: Pelvis, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/0003\c0003_201_01.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/0003\c0003_201_02.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/0003\c0003_201_03.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/0003\c0003_201_04.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/0100\c0100_051_01.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/1001\c1001_072_00.GMD — no mesh attachments; missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/1011\c1011_201_01.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/1011\c1011_201_02.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/2011\c2011_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/2113\c2113_071_00.GMD — vehicle-like node naming detected
- P5/3501\c3501_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; prop or weapon rig without humanoid pelvis; fewer than two semantic feet
- P5/3501\c3501_001_00.GMD — missing required canonical joints: Root, Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; prop or weapon rig without humanoid pelvis; fewer than two semantic feet
- P5/4691\c4691_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4692\c4692_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4693\c4693_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4694\c4694_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4695\c4695_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4696\c4696_000_00.GMD — missing required canonical joints: LowerSpine, UpperSpine
- P5/4697\c4697_000_00.GMD — missing required canonical joints: LowerSpine, UpperSpine
- P5/4698\c4698_000_00.GMD — missing required canonical joints: LowerSpine, UpperSpine
- P5/4720\c4720_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4850\c4850_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4851\c4851_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4856\c4856_001_00.GMD — missing required canonical joints: Root, Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4856\c4856_002_00.GMD — missing required canonical joints: Root, Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4857\c4857_001_00.GMD — missing required canonical joints: Root, Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4857\c4857_002_00.GMD — missing required canonical joints: Root, Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4901\c4901_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet
- P5/4902\c4902_000_00.GMD — missing required canonical joints: Pelvis, LowerSpine, UpperSpine, Head, LeftShoulder, LeftElbow, LeftHand, RightShoulder, RightElbow, RightHand, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot; fewer than two semantic feet

Examples of node classes that can safely be excluded from body-continuation features: rot (1,986 models), b b hair02 (435 models), b l hair00 (385 models), b r hair00 (385 models), b r s_coat01 (350 models), b r f_coat01 (350 models), b l s_coat01 (350 models), b b hair01 (342 models), b l f_coat01 (336 models), b r b_coat01 (331 models), b l b_coat01 (331 models), b l b_coat00 (323 models), b l s_coat00 (323 models), b r b_coat00 (323 models), b r s_coat00 (323 models), b r f_coat00 (323 models), b l hair01 (319 models), b f hair01 (318 models), b r hair01 (318 models), b l f_coat00 (309 models), mesh_grp (244 models), b r s_coat02 (233 models), b r f_coat02 (233 models), b l s_coat02 (233 models), b l f_coat02 (233 models), b b hair00 (228 models), b p hair01 (221 models), b f hair00 (220 models), b r s_coat03 (218 models), b l s_coat03 (218 models).

## Recommendation

A global semantic pose index is recommended for the **1,989/2,468 (80.6%)** models passing this audit. Build features from the original animation plus its source skeleton mapped into the canonical slots above; do not include the selected showroom body/face/hair model in the expensive cache identity. Keep low-coverage, non-humanoid, and failed models out of the global body index, but retain them as preview/export assets when the target rig is genuinely needed.

The implementation should version the canonical mapping and feature schema in the persistent cache fingerprint, keep per-animation/shard source-skeleton features reusable, sample coarsely for ANN, locally refine shortlisted frames at full resolution, and retarget only after candidate selection for preview/export.

## Machine-readable detail

animatch-skeleton-report.json contains every discovered path, load result, node name/parent hierarchy, local bind PRS, world position, skin-bone inverse bind matrix, existing semantic roles, canonical resolution, topology signature, and outlier reasons.
