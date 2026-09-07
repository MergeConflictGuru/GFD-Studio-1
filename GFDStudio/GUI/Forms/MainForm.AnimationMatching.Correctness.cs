using System;
using System.Collections.Concurrent;
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
    /// Supplies source-model clips to the global AniMatch index. The selected showroom character is
    /// intentionally absent from this layer; it is used only by the preview/export callbacks.
    /// </summary>
    public partial class MainForm : IAnimationMatchingCorpusHost
    {
        private static readonly ConcurrentDictionary<string, WeakReference<Model>> sAnimationMatchingSourceModels =
            new ConcurrentDictionary<string, WeakReference<Model>>(StringComparer.OrdinalIgnoreCase);

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

                // Character Browser entries have a stable source pack/kind/index identity. Keep it
                // on the query clip so the matcher can suppress the trivial self-match.
                var selected = mCharacterAnimationListBox?.SelectedItem as CharacterAnimationEntry;
                if (selected != null &&
                    string.Equals(source.DisplayName, selected.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return new AnimationMatchIdentityClip(source, GetCorrectedAnimationMatchClipId(selected));
                }

                return source;
            }
        }

        IReadOnlyList<IAnimationClip> IAnimationMatchingCorpusHost.SearchableAnimationsForMatching =>
            BuildCorrectedAnimationMatchingCorpus();

        string IAnimationMatchingCorpusHost.AnimationMatchingContextSignature =>
            GetCorrectedAnimationMatchingContextKey();

        private IReadOnlyList<IAnimationClip> BuildCorrectedAnimationMatchingCorpus()
        {
            var root = mCharacterBrowserRoot;
            var lookups = BuildAnimationMatchingSourceModelLookups(root);
            var entries = mCharacterAnimations
                .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                .ToArray();
            var clips = new List<IAnimationClip>(entries.Length);
            var validSourceModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalidSourceModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedWithoutSourceModel = 0;

            foreach (var entry in entries)
            {
                var sourceModelPath = ResolveAnimationMatchingSourceModelPath(
                    entry, root, lookups.exactModels, lookups.characterModels);
                if (string.IsNullOrWhiteSpace(sourceModelPath) || !File.Exists(sourceModelPath))
                {
                    skippedWithoutSourceModel++;
                    Logger.Debug($"AnimationMatch: skipping {entry.PackPath} [{entry.Kind} {entry.Index}] because its source model could not be resolved.");
                    continue;
                }

                if (!validSourceModels.Contains(sourceModelPath) && !invalidSourceModels.Contains(sourceModelPath))
                {
                    try
                    {
                        // Validate each unique source model once, then drop the strong reference.
                        // Individual clips load their source path only while being indexed.
                        GfdAnimationClip.CreateSkeleton(LoadAnimationMatchingSourceModel(sourceModelPath));
                        validSourceModels.Add(sourceModelPath);
                    }
                    catch (Exception ex)
                    {
                        invalidSourceModels.Add(sourceModelPath);
                        Logger.Debug($"AnimationMatch: skipping {entry.PackPath} because its source skeleton is not canonical: {ex.Message}");
                    }
                }

                if (invalidSourceModels.Contains(sourceModelPath))
                {
                    skippedWithoutSourceModel++;
                    continue;
                }

                var capturedSourceModelPath = sourceModelPath;
                var capturedPackPath = entry.PackPath;
                var capturedKind = entry.Kind;
                var capturedIndex = entry.Index;
                clips.Add(new GfdAnimationClip(
                    GetCorrectedAnimationMatchClipId(entry),
                    entry.DisplayName,
                    () => LoadAnimationMatchingSourceModel(capturedSourceModelPath),
                    () => LoadAnimationMatchingSourceAnimation(
                        capturedPackPath, capturedKind, capturedIndex),
                    AnimationMatchingFramesPerSecond));
            }

            if (skippedWithoutSourceModel > 0)
            {
                Logger.Debug($"AnimationMatch: excluded {skippedWithoutSourceModel} animations with no resolvable source model; {clips.Count} remain searchable.");
            }

            return clips;
        }

        private (Dictionary<string, string> exactModels, Dictionary<string, string> characterModels)
            BuildAnimationMatchingSourceModelLookups(string root)
        {
            var exactModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var characterModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in mCharacterModels.Where(model =>
                         model.Part == CharacterModelPart.Body && !string.IsNullOrWhiteSpace(model.Path)))
            {
                var directory = GetAnimationMatchCharacterDirectory(root, model.Path);
                var key = ExtractCharacterModelKey(model.Path);
                var characterId = ExtractCharacterId(model.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(key))
                    exactModels.TryAdd(directory + "|" + key, model.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(characterId))
                    characterModels.TryAdd(directory + "|" + characterId, model.Path);
            }

            return (exactModels, characterModels);
        }

        private static string ResolveAnimationMatchingSourceModelPath(
            CharacterAnimationEntry entry,
            string root,
            IReadOnlyDictionary<string, string> exactModels,
            IReadOnlyDictionary<string, string> characterModels)
        {
            var directory = GetAnimationMatchCharacterDirectory(root, entry.PackPath);
            var animationKey = ExtractCharacterModelKey(entry.PackPath);
            var characterId = ExtractCharacterId(entry.PackPath);
            string sourceModelPath = null;

            if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(animationKey))
                exactModels.TryGetValue(directory + "|" + animationKey, out sourceModelPath);
            if (string.IsNullOrWhiteSpace(sourceModelPath) &&
                !string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(characterId))
                characterModels.TryGetValue(directory + "|" + characterId, out sourceModelPath);

            return sourceModelPath;
        }

        private static Model LoadAnimationMatchingSourceModel(string sourceModelPath)
        {
            if (sAnimationMatchingSourceModels.TryGetValue(sourceModelPath, out var weakReference) &&
                weakReference.TryGetTarget(out var cached))
                return cached;

            var pack = Resource.Load<ModelPack>(sourceModelPath);
            var model = pack?.Model ?? throw new InvalidDataException(
                "Animation source model has no model data: " + sourceModelPath);
            sAnimationMatchingSourceModels[sourceModelPath] = new WeakReference<Model>(model);
            return model;
        }

        private static Animation LoadAnimationMatchingSourceAnimation(
            string packPath,
            CharacterAnimationListKind kind,
            int index)
        {
            var pack = Resource.Load<AnimationPack>(packPath);
            var animation = kind switch
            {
                CharacterAnimationListKind.Animation =>
                    index >= 0 && index < (pack.Animations?.Count ?? 0) ? pack.Animations[index] : null,
                CharacterAnimationListKind.ExtraAnimation =>
                    index >= 0 && index < (pack.METAPHOR_AnimArray3?.Count ?? 0) ? pack.METAPHOR_AnimArray3[index] : null,
                CharacterAnimationListKind.BlendAnimation =>
                    index >= 0 && index < (pack.BlendAnimations?.Count ?? 0) ? pack.BlendAnimations[index] : null,
                _ => null
            };
            return animation ?? throw new InvalidDataException(
                "Animation no longer exists in pack: " + packPath);
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
            return string.Join("|",
                "animatch-global-v3",
                NormalizeAnimationMatchPath(mCharacterBrowserRoot),
                GetAnimationMatchingCorpusListSignature(),
                mCharacterBrowserScanGeneration);
        }

        private string GetAnimationMatchingCorpusListSignature()
        {
            var root = mCharacterBrowserRoot;
            var lookups = BuildAnimationMatchingSourceModelLookups(root);
            var builder = new StringBuilder();
            foreach (var entry in mCharacterAnimations
                         .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                         .OrderBy(entry => entry.PackPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(entry => entry.Kind)
                         .ThenBy(entry => entry.Index))
            {
                var sourceModelPath = ResolveAnimationMatchingSourceModelPath(
                    entry, root, lookups.exactModels, lookups.characterModels);
                builder.Append(GetCorrectedAnimationMatchClipId(entry))
                    .Append('|')
                    .Append(NormalizeAnimationMatchPath(sourceModelPath))
                    .Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
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
