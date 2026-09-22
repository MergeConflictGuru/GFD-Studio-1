using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        bool IAnimationMatchingCorpusHost.AnimationMatchingCorpusReady =>
            mCharacterBrowserScanComplete;

        private static readonly ConcurrentDictionary<string, WeakReference<Model>> sAnimationMatchingSourceModels =
            new ConcurrentDictionary<string, WeakReference<Model>>(StringComparer.OrdinalIgnoreCase);

        private sealed class AnimationMatchIdentityClip : IAnimationClipWrapper
        {
            private readonly IAnimationClip mInner;

            public AnimationMatchIdentityClip(IAnimationClip inner, string id)
            {
                mInner = inner ?? throw new ArgumentNullException(nameof(inner));
                Id = id ?? throw new ArgumentNullException(nameof(id));
            }

            public string Id { get; }
            public IAnimationClip InnerClip => mInner;
            public string DisplayName => mInner.DisplayName;
            public SkeletonDefinition Skeleton => mInner.Skeleton;
            public int FrameCount => mInner.FrameCount;
            public float FramesPerSecond => mInner.FramesPerSecond;
            public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination) =>
                mInner.SampleGlobalPose(frameIndex, destination);
        }

        private sealed class AnimationMatchingComposition
        {
            public string BasePackPath { get; init; }
            public string BodyModelPath { get; init; }
            public string FaceSelectionPath { get; init; }
            public string HairSelectionPath { get; init; }
            public string FaceModelPath { get; init; }
            public string HairModelPath { get; init; }
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
            BuildCorrectedAnimationMatchingCorpus(validateSourceModels: false);

        IReadOnlyList<IAnimationClip> IAnimationMatchingCorpusHost.SearchableAnimationsForIndexBuild =>
            BuildCorrectedAnimationMatchingCorpus(validateSourceModels: true);

        string IAnimationMatchingCorpusHost.AnimationMatchingContextSignature =>
            GetCorrectedAnimationMatchingContextKey();

        private IReadOnlyList<IAnimationClip> BuildCorrectedAnimationMatchingCorpus(bool validateSourceModels = true)
        {
            var root = mCharacterBrowserRoot;
            var lookups = BuildAnimationMatchingSourceModelLookups(root);
            var modelEntries = mCharacterModels.ToArray();
            var entries = mCharacterAnimations
                .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                .OrderBy(entry => GetCorrectedAnimationMatchClipId(entry), StringComparer.Ordinal)
                .ToArray();
            var animationGroups = BuildAnimationMatchingAnimationGroups(entries);
            var compositionCache = new Dictionary<string, AnimationMatchingComposition>(
                StringComparer.OrdinalIgnoreCase);
            var clips = new List<IAnimationClip>(entries.Length);
            var validSourceModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalidSourceModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedWithoutSourceModel = 0;

            foreach (var entry in entries)
            {
                // Dance component GAPs are not independent motions. Index only the base
                // animation after composing the body/face/hair additions onto it; indexing a
                // component by itself produces the static/T-pose candidates seen in results.
                if (entry.Kind == CharacterAnimationListKind.Animation &&
                    IsCharacterBrowserAnimationComponent(entry.PackPath))
                    continue;

                var sourceModelPath = ResolveAnimationMatchingSourceModelPath(
                    entry, root, lookups.exactModels, lookups.characterModels);
                if (string.IsNullOrWhiteSpace(sourceModelPath) || !File.Exists(sourceModelPath))
                {
                    skippedWithoutSourceModel++;
                    Logger.Debug($"AnimationMatch: skipping {entry.PackPath} [{entry.Kind} {entry.Index}] because its source model could not be resolved.");
                    continue;
                }

                if (validateSourceModels &&
                    !validSourceModels.Contains(sourceModelPath) &&
                    !invalidSourceModels.Contains(sourceModelPath))
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

                if (validateSourceModels && invalidSourceModels.Contains(sourceModelPath))
                {
                    skippedWithoutSourceModel++;
                    continue;
                }

                var capturedSourceModelPath = sourceModelPath;
                var capturedPackPath = entry.PackPath;
                var capturedKind = entry.Kind;
                var capturedIndex = entry.Index;
                var composition = entry.Kind == CharacterAnimationListKind.Animation
                    ? GetAnimationMatchingComposition(
                        entry, sourceModelPath, animationGroups, compositionCache, modelEntries)
                    : null;
                Func<Animation> animationLoader = composition == null
                    ? () => LoadAnimationMatchingSourceAnimation(
                        capturedPackPath, capturedKind, capturedIndex)
                    : () => LoadComposedAnimationMatchingSourceAnimation(
                        composition, capturedIndex);
                Func<Model> modelLoader = composition == null
                    ? () => LoadAnimationMatchingSourceModel(capturedSourceModelPath)
                    : () => LoadAnimationMatchingSourceModel(composition);
                clips.Add(new GfdAnimationClip(
                    GetCorrectedAnimationMatchClipId(entry),
                    entry.DisplayName,
                    modelLoader,
                    animationLoader,
                    AnimationMatchingFramesPerSecond));
            }

            if (skippedWithoutSourceModel > 0)
            {
                Logger.Debug($"AnimationMatch: excluded {skippedWithoutSourceModel} animations with no resolvable source model; {clips.Count} remain searchable.");
            }

            return clips;
        }

        private static Dictionary<string, IReadOnlyList<CharacterAnimationEntry>>
            BuildAnimationMatchingAnimationGroups(IReadOnlyList<CharacterAnimationEntry> entries)
        {
            var groups = new Dictionary<string, List<CharacterAnimationEntry>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Kind != CharacterAnimationListKind.Animation)
                    continue;

                var basePath = GetCharacterBrowserAnimationBasePath(entry.PackPath);
                var key = NormalizeAnimationMatchPath(basePath);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<CharacterAnimationEntry>();
                    groups.Add(key, group);
                }
                group.Add(entry);
            }

            return groups.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<CharacterAnimationEntry>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static AnimationMatchingComposition GetAnimationMatchingComposition(
            CharacterAnimationEntry entry,
            string sourceModelPath,
            IReadOnlyDictionary<string, IReadOnlyList<CharacterAnimationEntry>> animationGroups,
            IDictionary<string, AnimationMatchingComposition> compositionCache,
            IReadOnlyList<CharacterModelEntry> modelEntries)
        {
            var basePath = GetCharacterBrowserAnimationBasePath(entry.PackPath);
            var cacheKey = string.Join("|",
                NormalizeAnimationMatchPath(basePath),
                NormalizeAnimationMatchPath(sourceModelPath));
            if (compositionCache.TryGetValue(cacheKey, out var cached))
                return cached;

            animationGroups.TryGetValue(
                NormalizeAnimationMatchPath(basePath), out var related);
            var composition = ResolveAnimationMatchingComposition(
                entry, sourceModelPath, related ?? Array.Empty<CharacterAnimationEntry>(), modelEntries);
            compositionCache[cacheKey] = composition;
            return composition;
        }

        private static bool IsCharacterBrowserAnimationComponent(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (!Regex.IsMatch(
                    stem,
                    @"_(?:f|h\d+|\d+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;

            var basePath = GetCharacterBrowserAnimationBasePath(path);
            return !string.IsNullOrWhiteSpace(basePath) &&
                   !AreSamePath(basePath, path) &&
                   File.Exists(basePath);
        }

        private static AnimationMatchingComposition ResolveAnimationMatchingComposition(
            CharacterAnimationEntry entry,
            string sourceModelPath,
            IReadOnlyList<CharacterAnimationEntry> entries,
            IReadOnlyList<CharacterModelEntry> modelEntries)
        {
            var basePath = GetCharacterBrowserAnimationBasePath(entry.PackPath);
            if (string.IsNullOrWhiteSpace(basePath) || !File.Exists(basePath))
                return null;

            var related = entries
                .Where(candidate => candidate.Kind == CharacterAnimationListKind.Animation)
                .Where(candidate => AreSamePath(
                    GetCharacterBrowserAnimationBasePath(candidate.PackPath), basePath))
                .ToArray();

            var directory = Path.GetDirectoryName(basePath) ?? string.Empty;
            var baseStem = Path.GetFileNameWithoutExtension(basePath) ?? string.Empty;
            var facePath = related
                .Select(candidate => candidate.PackPath)
                .Where(path => string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    baseStem + "_f",
                    StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(path => File.Exists(path));
            facePath ??= Path.Combine(directory, baseStem + "_f.GAP");
            if (!File.Exists(facePath))
                facePath = null;

            var hairComponentPath = related
                .Select(candidate => candidate.PackPath)
                .Where(path => Regex.IsMatch(
                    Path.GetFileNameWithoutExtension(path) ?? string.Empty,
                    @"_h\d+$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(path => File.Exists(path));
            var hairStem = GetCharacterBrowserAnimationHairStem(hairComponentPath);
            var hairSelectionPath = string.IsNullOrWhiteSpace(hairStem)
                ? null
                : Path.Combine(directory, hairStem + ".GMD");

            var characterId = ExtractCharacterId(sourceModelPath);
            var faceModelPath = string.IsNullOrWhiteSpace(facePath) ||
                                string.IsNullOrWhiteSpace(characterId)
                ? null
                : FindCharacterBrowserAnimationSplitPart(
                    modelEntries, sourceModelPath, CharacterModelPart.Face, characterId, null)?.Path;
            var hairModelPath = string.IsNullOrWhiteSpace(hairSelectionPath) ||
                                string.IsNullOrWhiteSpace(characterId)
                ? null
                : FindCharacterBrowserAnimationSplitPart(
                    modelEntries, sourceModelPath, CharacterModelPart.Hair, characterId, hairStem)?.Path;

            var bodyComponentPath = GetCharacterBrowserBodyAnimationPath(basePath, sourceModelPath);
            var hasBodyComponent = !string.IsNullOrWhiteSpace(bodyComponentPath) &&
                                   File.Exists(bodyComponentPath);
            if (!hasBodyComponent && facePath == null && hairSelectionPath == null)
                return null;

            return new AnimationMatchingComposition
            {
                BasePackPath = basePath,
                BodyModelPath = sourceModelPath,
                FaceSelectionPath = facePath,
                HairSelectionPath = hairSelectionPath,
                FaceModelPath = faceModelPath,
                HairModelPath = hairModelPath
            };
        }

        private static Model LoadAnimationMatchingSourceModel(
            AnimationMatchingComposition composition)
        {
            var cacheKey = string.Join("|",
                NormalizeAnimationMatchPath(composition.BodyModelPath),
                NormalizeAnimationMatchPath(composition.FaceModelPath),
                NormalizeAnimationMatchPath(composition.HairModelPath));
            if (sAnimationMatchingSourceModels.TryGetValue(cacheKey, out var weakReference) &&
                weakReference.TryGetTarget(out var cached))
                return cached;

            var parts = new List<CharacterModelEntry>
            {
                new CharacterModelEntry
                {
                    Path = composition.BodyModelPath,
                    Part = CharacterModelPart.Body,
                    DisplayName = composition.BodyModelPath
                }
            };
            if (!string.IsNullOrWhiteSpace(composition.FaceModelPath))
            {
                parts.Add(new CharacterModelEntry
                {
                    Path = composition.FaceModelPath,
                    Part = CharacterModelPart.Face,
                    DisplayName = composition.FaceModelPath
                });
            }
            if (!string.IsNullOrWhiteSpace(composition.HairModelPath))
            {
                parts.Add(new CharacterModelEntry
                {
                    Path = composition.HairModelPath,
                    Part = CharacterModelPart.Hair,
                    DisplayName = composition.HairModelPath
                });
            }

            var model = ComposeCharacterModelPack(parts)?.Model ?? throw new InvalidDataException(
                "Animation source model has no model data: " + composition.BodyModelPath);
            sAnimationMatchingSourceModels[cacheKey] = new WeakReference<Model>(model);
            return model;
        }

        private static Animation LoadComposedAnimationMatchingSourceAnimation(
            AnimationMatchingComposition composition,
            int index)
        {
            var baseAnimation = LoadAnimationMatchingSourceAnimation(
                composition.BasePackPath,
                CharacterAnimationListKind.Animation,
                index);
            if (baseAnimation == null)
                return null;

            var baseEntry = new CharacterAnimationEntry
            {
                PackPath = composition.BasePackPath,
                Kind = CharacterAnimationListKind.Animation,
                Index = index,
                DisplayName = composition.BasePackPath
            };

            return ComposeCharacterBrowserAnimation(
                baseEntry,
                baseAnimation,
                composition.BodyModelPath,
                composition.FaceSelectionPath,
                composition.HairSelectionPath,
                out _,
                out _,
                CancellationToken.None);
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
            !string.IsNullOrWhiteSpace(entry.DefinitionHash)
                ? "definition:" + entry.DefinitionHash
                : GetCorrectedAnimationMatchClipId(entry.PackPath, entry.Kind, entry.Index);

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
                "animatch-global-v5-split-composition",
                NormalizeAnimationMatchPath(mCharacterBrowserRoot),
                GetAnimationMatchingCorpusListSignature());
        }

        private string GetAnimationMatchingCorpusListSignature()
        {
            var root = mCharacterBrowserRoot;
            var lookups = BuildAnimationMatchingSourceModelLookups(root);
            var builder = new StringBuilder();
            foreach (var entry in mCharacterAnimations
                         .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                         .OrderBy(entry => GetCorrectedAnimationMatchClipId(entry), StringComparer.Ordinal))
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
