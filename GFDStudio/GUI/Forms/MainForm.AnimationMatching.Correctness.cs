using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;

namespace GFDStudio.GUI.Forms
{
    /// <summary>
    /// Correctness layer for AniMatch corpus construction. Kept separate from the Character Browser
    /// UI so matching can be stricter than the browser's preview compatibility rules.
    /// </summary>
    public partial class MainForm : IAnimationMatchingCorpusHost
    {
        private sealed class AnimationMatchIdentityClip : IAnimationClip
        {
            private readonly IAnimationClip mInner;

            public AnimationMatchIdentityClip(IAnimationClip inner, string id)
            {
                mInner = inner ?? throw new ArgumentNullException(nameof(inner));
                Id = id ?? throw new ArgumentNullException(nameof(id));
            }

            public string Id { get; }
            public string DisplayName => mInner.DisplayName;
            public SkeletonDefinition Skeleton => mInner.Skeleton;
            public int FrameCount => mInner.FrameCount;
            public float FramesPerSecond => mInner.FramesPerSecond;
            public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination) =>
                mInner.SampleGlobalPose(frameIndex, destination);
        }

        IAnimationClip IAnimationMatchingCorpusHost.CurrentAnimationForMatching
        {
            get
            {
                var source = mAnimationMatchCurrentSource;
                if (source == null)
                    return null;

                // Character Browser animations have a stable pack/kind/index identity. Preserve it
                // for the source as well as the corpus so AnimationMatcher can actually suppress
                // the trivial self match around the selected transition frame.
                var selected = mCharacterAnimationListBox?.SelectedItem as CharacterAnimationEntry;
                if (selected != null &&
                    string.Equals(source.DisplayName, selected.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return new AnimationMatchIdentityClip(source, GetCorrectedAnimationMatchClipId(selected));
                }

                // An animation opened outside the showroom has no corpus identity, so keep the
                // source-only GUID assigned by the existing capture path rather than guessing.
                return source;
            }
        }

        IReadOnlyList<IAnimationClip> IAnimationMatchingCorpusHost.SearchableAnimationsForMatching =>
            BuildCorrectedAnimationMatchingCorpus();

        string IAnimationMatchingCorpusHost.AnimationMatchingContextSignature =>
            GetCorrectedAnimationMatchingContextKey();

        private IReadOnlyList<IAnimationClip> BuildCorrectedAnimationMatchingCorpus()
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                return Array.Empty<IAnimationClip>();

            var targetModel = targetPack.Model;
            var targetModelPath = mCharacterBrowserCurrentModelPath;
            var skeleton = GfdAnimationClip.CreateSkeleton(targetModel);
            var root = mCharacterBrowserRoot;

            var bodyModels = mCharacterModels
                .Where(model => model.Part == CharacterModelPart.Body && !string.IsNullOrWhiteSpace(model.Path))
                .ToArray();
            var exactModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var characterModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in bodyModels)
            {
                var directory = GetAnimationMatchCharacterDirectory(root, model.Path);
                var key = ExtractCharacterModelKey(model.Path);
                var characterId = ExtractCharacterId(model.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(key))
                    exactModels.TryAdd(directory + "|" + key, model.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(characterId))
                    characterModels.TryAdd(directory + "|" + characterId, model.Path);
            }

            var selectedKey = ExtractCharacterModelKey(targetModelPath);

            // Do NOT use IsCharacterBrowserAnimationForSelectedBody here. That legacy filter checks
            // raw target node names and can reject P5/P5D clips before AnimationRetargetMap gets the
            // chance to map their different humanoid naming/hierarchy conventions.
            var entries = mCharacterAnimations
                .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                .ToArray();
            var clips = new List<IAnimationClip>(entries.Length);
            var skippedWithoutSourceModel = 0;

            foreach (var entry in entries)
            {
                var packPath = entry.PackPath;
                var kind = entry.Kind;
                var index = entry.Index;
                var displayName = entry.DisplayName;
                var directory = GetAnimationMatchCharacterDirectory(root, packPath);
                var animationKey = ExtractCharacterModelKey(packPath);
                var characterId = ExtractCharacterId(packPath);

                string sourceModelPath = null;
                if (!string.IsNullOrWhiteSpace(targetModelPath) &&
                    !string.IsNullOrWhiteSpace(selectedKey) &&
                    string.Equals(selectedKey, animationKey, StringComparison.OrdinalIgnoreCase))
                {
                    sourceModelPath = targetModelPath;
                }
                else if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(animationKey))
                {
                    exactModels.TryGetValue(directory + "|" + animationKey, out sourceModelPath);
                }

                if (string.IsNullOrWhiteSpace(sourceModelPath) &&
                    !string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(characterId))
                {
                    characterModels.TryGetValue(directory + "|" + characterId, out sourceModelPath);
                }

                // An unknown source skeleton is not a valid retarget. The old path silently called
                // FixTargetIds on the raw animation, which could delete incompatible tracks and then
                // index the mutilated pose as if it were a legitimate match candidate.
                if (string.IsNullOrWhiteSpace(sourceModelPath) || !File.Exists(sourceModelPath))
                {
                    skippedWithoutSourceModel++;
                    Logger.Debug($"AnimationMatch: skipping {packPath} [{kind} {index}] because its source model could not be resolved.");
                    continue;
                }

                var capturedSourceModelPath = sourceModelPath;
                clips.Add(new GfdAnimationClip(
                    GetCorrectedAnimationMatchClipId(entry),
                    displayName,
                    targetModel,
                    skeleton,
                    () => LoadAnimationMatchingCandidateStrict(
                        packPath, kind, index, capturedSourceModelPath, targetModelPath, targetModel),
                    AnimationMatchingFramesPerSecond));
            }

            if (skippedWithoutSourceModel > 0)
            {
                Logger.Debug($"AnimationMatch: excluded {skippedWithoutSourceModel} animations with no resolvable source model; {clips.Count} remain searchable.");
            }

            return clips;
        }

        private static Animation LoadAnimationMatchingCandidateStrict(
            string packPath,
            CharacterAnimationListKind kind,
            int index,
            string sourceModelPath,
            string targetModelPath,
            Model targetModel)
        {
            if (string.IsNullOrWhiteSpace(sourceModelPath))
                throw new InvalidDataException("Animation matching requires a resolvable source model: " + packPath);

            var pack = Resource.Load<AnimationPack>(packPath);
            Animation animation = kind switch
            {
                CharacterAnimationListKind.Animation =>
                    index >= 0 && index < (pack.Animations?.Count ?? 0) ? pack.Animations[index] : null,
                CharacterAnimationListKind.ExtraAnimation =>
                    index >= 0 && index < (pack.METAPHOR_AnimArray3?.Count ?? 0) ? pack.METAPHOR_AnimArray3[index] : null,
                CharacterAnimationListKind.BlendAnimation =>
                    index >= 0 && index < (pack.BlendAnimations?.Count ?? 0) ? pack.BlendAnimations[index] : null,
                _ => null
            };
            if (animation == null)
                throw new InvalidDataException("Animation no longer exists in pack: " + packPath);

            if (!AreSamePath(sourceModelPath, targetModelPath))
            {
                var sourcePack = Resource.Load<ModelPack>(sourceModelPath);
                if (sourcePack?.Model == null)
                    throw new InvalidDataException("Animation source model has no model data: " + sourceModelPath);

                // This invokes the semantic humanoid baker when P5 and P5D use different
                // hierarchies, so descriptors are generated only after conversion to target space.
                animation.Retarget(sourcePack.Model, targetModel, false);
            }

            animation.FixTargetIds(targetModel);
            return animation;
        }

        private static string GetCorrectedAnimationMatchClipId(CharacterAnimationEntry entry) =>
            GetCorrectedAnimationMatchClipId(entry.PackPath, entry.Kind, entry.Index);

        private static string GetCorrectedAnimationMatchClipId(
            string packPath,
            CharacterAnimationListKind kind,
            int index)
        {
            var normalizedPath = packPath ?? string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(normalizedPath))
                    normalizedPath = Path.GetFullPath(normalizedPath);
            }
            catch
            {
                // Preserve the original path if it cannot be normalized.
            }

            return normalizedPath + "|" + kind + "|" + index;
        }

        private string GetCorrectedAnimationMatchingContextKey()
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            var facePath = GetSelectedCharacterBrowserFacePath();
            var hairPath = GetSelectedCharacterBrowserHairPath();

            return string.Join("|",
                "animatch-context-v2",
                NormalizeAnimationMatchPath(mCharacterBrowserRoot),
                NormalizeAnimationMatchPath(mCharacterBrowserCurrentModelPath),
                NormalizeAnimationMatchPath(facePath),
                NormalizeAnimationMatchPath(hairPath),
                GetAnimationMatchingTargetSkeletonSignature(targetPack?.Model),
                GetAnimationMatchingCorpusListSignature(),
                mCharacterBrowserScanGeneration,
                mCharacterAnimations.Count);
        }

        private string GetAnimationMatchingCorpusListSignature()
        {
            // Clip membership is cheap to hash and avoids treating two scans with the same count as
            // the same corpus. AnimationIndexCache separately verifies every clip ID on load.
            var builder = new StringBuilder();
            foreach (var entry in mCharacterAnimations
                         .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                         .OrderBy(entry => entry.PackPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(entry => entry.Kind)
                         .ThenBy(entry => entry.Index))
            {
                builder.Append(GetCorrectedAnimationMatchClipId(entry)).Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static string GetAnimationMatchingTargetSkeletonSignature(Model model)
        {
            if (model == null)
                return "no-target-model";

            // Pose matching depends on the composed target skeleton, not its textures. Hash node
            // topology and bind transforms so changing body/face/hair composition invalidates both
            // the disk cache and the live in-memory index even when file paths/counts stay the same.
            var builder = new StringBuilder();
            foreach (var node in model.Nodes)
            {
                builder.Append(node.Name).Append('|')
                    .Append(node.Parent?.Name ?? string.Empty).Append('|');
                AppendAnimationMatchVector(builder, node.Translation);
                AppendAnimationMatchQuaternion(builder, node.Rotation);
                AppendAnimationMatchVector(builder, node.Scale);
                builder.Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static void AppendAnimationMatchVector(StringBuilder builder, System.Numerics.Vector3 value)
        {
            builder.Append(BitConverter.SingleToInt32Bits(value.X)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(value.Y)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(value.Z)).Append('|');
        }

        private static void AppendAnimationMatchQuaternion(StringBuilder builder, System.Numerics.Quaternion value)
        {
            builder.Append(BitConverter.SingleToInt32Bits(value.X)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(value.Y)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(value.Z)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(value.W)).Append('|');
        }

        private static string NormalizeAnimationMatchPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
