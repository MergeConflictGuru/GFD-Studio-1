using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    /// <summary>
    /// Runs the normal humanoid retarget and the P5D-specific corrective pass
    /// as one operation. The optional native base animation calibrates P5D
    /// Dance knee helper bones; same-game destination GAPs can also supply
    /// authored tracks for target-only nodes.
    /// </summary>
    public static class P5dAnimationRetargeter
    {
        private static readonly IReadOnlyDictionary<string, string>
            NativeReferenceStems =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["201"] = "pc201_003_p",
                    ["202"] = "pc202_002_p",
                    ["203"] = "pc203_001_p",
                    ["204"] = "pc204_018_p",
                    ["205"] = "pc205_001_p",
                    ["206"] = "pc206_001_p",
                    ["207"] = "pc207_002_p",
                    ["208"] = "pc208_001_p",
                    ["209"] = "pc209_029",
                    ["210"] = "pc210_002_p",
                    ["211"] = "pc211_002_p",
                    ["212"] = "pc212_030"
                };

        private sealed class NativeReference
        {
            public Animation Animation { get; init; }
            public string Path { get; init; }
        }

        private sealed class DestinationReferencePack
        {
            public AnimationPack Pack { get; init; }
            public string Path { get; init; }
        }

        private static readonly Dictionary<string, NativeReference> NativeReferenceCache =
            new Dictionary<string, NativeReference>(StringComparer.OrdinalIgnoreCase);
        private static readonly object NativeReferenceCacheLock = new object();
        private static readonly Dictionary<string, IReadOnlyList<DestinationReferencePack>>
            DestinationReferenceCache =
                new Dictionary<string, IReadOnlyList<DestinationReferencePack>>(
                    StringComparer.OrdinalIgnoreCase);
        private static readonly object DestinationReferenceCacheLock = new object();

        /// <summary>
        /// Retargets a pack and applies the P5D corrective tracks when the
        /// target skeleton contains the Dance helper hierarchy.
        /// </summary>
        public static P5dRetargetResult Retarget(
            AnimationPack pack,
            Model originalModel,
            Model targetModel,
            Animation nativeBase = null,
            bool useLocalBindSpace = false)
        {
            return Retarget(
                pack, originalModel, targetModel, nativeBase, false,
                useLocalBindSpace);
        }

        public static P5dRetargetResult Retarget(
            AnimationPack pack,
            Model originalModel,
            Model targetModel,
            Animation nativeBase,
            bool fixArms,
            bool useLocalBindSpace = false)
        {
            if (pack == null)
                throw new ArgumentNullException(nameof(pack));
            if (originalModel == null)
                throw new ArgumentNullException(nameof(originalModel));
            if (targetModel == null)
                throw new ArgumentNullException(nameof(targetModel));

            pack.Retarget(originalModel, targetModel, fixArms, useLocalBindSpace);

            var isP5dTarget = DancingKneeCorrection.SupportsTarget(targetModel);
            if (!isP5dTarget || nativeBase == null)
            {
                if (isP5dTarget && nativeBase == null)
                    Logger.Debug("P5D retarget: native Dance base is unavailable; knee helpers were not generated.");
                return new P5dRetargetResult(
                    isP5dTarget, false, null, 0, Array.Empty<string>());
            }

            try
            {
                DancingKneeCorrection.Apply(pack, targetModel, nativeBase);
                return new P5dRetargetResult(
                    isP5dTarget, true, null, 0, Array.Empty<string>());
            }
            catch (Exception exception)
            {
                // Corrective calibration is optional for previewing. A bad or
                // missing reference must not turn into a UI error dialog.
                Logger.Debug($"P5D retarget: knee correction unavailable: {exception}");
                return new P5dRetargetResult(
                    isP5dTarget, false, null, 0, Array.Empty<string>());
            }
        }

        /// <summary>
        /// Retargets one animation and resolves its native P5D calibration clip
        /// from the target resource path when the target is a P5D Dance model.
        /// Path and file-layout knowledge belongs here instead of in a UI form.
        /// </summary>
        public static P5dRetargetResult Retarget(
            Animation animation,
            Model originalModel,
            Model targetModel,
            string targetModelPath,
            string referenceRoot,
            bool useLocalBindSpace = false,
            bool fixArms = false)
        {
            return Retarget(
                animation, originalModel, targetModel, null, 0,
                targetModelPath, new[] { targetModelPath }, referenceRoot,
                useLocalBindSpace, fixArms);
        }

        /// <summary>
        /// Retargets one source animation and, for same-game character changes,
        /// fills target-only node tracks from the matching destination action.
        /// The source pack path and clip index are required for that optional
        /// lookup; the old overload remains useful when no source file exists.
        /// </summary>
        public static P5dRetargetResult Retarget(
            Animation animation,
            Model originalModel,
            Model targetModel,
            string sourceAnimationPath,
            int sourceAnimationIndex,
            string targetModelPath,
            string referenceRoot,
            bool useLocalBindSpace = false,
            bool fixArms = false)
        {
            return Retarget(
                animation, originalModel, targetModel, sourceAnimationPath,
                sourceAnimationIndex, targetModelPath, new[] { targetModelPath },
                referenceRoot, useLocalBindSpace, fixArms);
        }

        /// <summary>
        /// Retargets one source animation with the selected destination body,
        /// face and hair paths available for exact split-GAP lookup.
        /// </summary>
        public static P5dRetargetResult Retarget(
            Animation animation,
            Model originalModel,
            Model targetModel,
            string sourceAnimationPath,
            int sourceAnimationIndex,
            string targetModelPath,
            IReadOnlyList<string> targetModelPaths,
            string referenceRoot,
            bool useLocalBindSpace = false,
            bool fixArms = false)
        {
            if (animation == null)
                throw new ArgumentNullException(nameof(animation));

            var nativeBase = TryLoadNativeBase(
                targetModel, targetModelPath, referenceRoot, out var referencePath);
            var pack = new AnimationPack(animation.Version)
            {
                Animations = new List<Animation> { animation }
            };
            var result = Retarget(
                pack, originalModel, targetModel, nativeBase, fixArms,
                useLocalBindSpace);
            result = result.WithReferencePath(
                result.KneeCorrectionApplied ? referencePath : null);
            return ApplyDestinationReferences(
                result, pack, originalModel, targetModel, sourceAnimationPath,
                targetModelPath, targetModelPaths, referenceRoot, sourceAnimationIndex);
        }

        /// <summary>
        /// Retargets a complete pack using the browser-selected root to resolve
        /// the native P5D calibration clip.
        /// </summary>
        public static P5dRetargetResult Retarget(
            AnimationPack pack,
            Model originalModel,
            Model targetModel,
            string targetModelPath,
            string referenceRoot,
            bool useLocalBindSpace = false,
            bool fixArms = false)
        {
            return Retarget(
                pack, originalModel, targetModel, null,
                targetModelPath, new[] { targetModelPath }, referenceRoot,
                useLocalBindSpace, fixArms);
        }

        /// <summary>
        /// Retargets a complete source pack and supplements target-only node
        /// tracks from matching same-game destination GAPs when available.
        /// </summary>
        public static P5dRetargetResult Retarget(
            AnimationPack pack,
            Model originalModel,
            Model targetModel,
            string sourceAnimationPath,
            string targetModelPath,
            string referenceRoot,
            bool useLocalBindSpace = false,
            bool fixArms = false)
        {
            return Retarget(
                pack, originalModel, targetModel, sourceAnimationPath,
                targetModelPath, new[] { targetModelPath }, referenceRoot,
                useLocalBindSpace, fixArms);
        }

        /// <summary>
        /// Retargets a complete source pack with the selected destination body,
        /// face and hair paths available for exact split-GAP lookup.
        /// </summary>
        public static P5dRetargetResult Retarget(
            AnimationPack pack,
            Model originalModel,
            Model targetModel,
            string sourceAnimationPath,
            string targetModelPath,
            IReadOnlyList<string> targetModelPaths,
            string referenceRoot,
            bool useLocalBindSpace = false,
            bool fixArms = false)
        {
            var nativeBase = TryLoadNativeBase(
                targetModel, targetModelPath, referenceRoot, out var referencePath);
            var result = Retarget(
                pack, originalModel, targetModel, nativeBase, fixArms,
                useLocalBindSpace);
            result = result.WithReferencePath(
                result.KneeCorrectionApplied ? referencePath : null);
            return ApplyDestinationReferences(
                result, pack, originalModel, targetModel, sourceAnimationPath,
                targetModelPath, targetModelPaths, referenceRoot, null);
        }

        private static P5dRetargetResult ApplyDestinationReferences(
            P5dRetargetResult result,
            AnimationPack pack,
            Model originalModel,
            Model targetModel,
            string sourceAnimationPath,
            string targetModelPath,
            IReadOnlyList<string> targetModelPaths,
            string referenceRoot,
            int? sourceAnimationIndex)
        {
            if (string.IsNullOrWhiteSpace(sourceAnimationPath) ||
                string.IsNullOrWhiteSpace(targetModelPath) ||
                string.IsNullOrWhiteSpace(referenceRoot) ||
                AnimationRetargetMap.Create(originalModel, targetModel).UsesDifferentHumanoidHierarchy)
                return result;

            try
            {
                var references = TryLoadDestinationReferences(
                    sourceAnimationPath, targetModelPath, referenceRoot,
                    targetModelPaths, sourceAnimationIndex, pack.Animations.Count,
                    out var referencePaths);
                var added = AnimationDestinationTrackSupplement.Apply(
                    pack, originalModel, targetModel, references);
                return result.WithDestinationTracks(added, referencePaths);
            }
            catch (Exception exception)
            {
                // Destination supplementation is an optional enhancement. A
                // malformed or partially copied reference GAP must not turn a
                // normal preview into an error dialog.
                Logger.Debug($"Same-game retarget: destination tracks unavailable: {exception}");
                return result;
            }
        }

        private static IReadOnlyDictionary<int, IReadOnlyList<Animation>>
            TryLoadDestinationReferences(
                string sourceAnimationPath,
                string targetModelPath,
                string referenceRoot,
                IReadOnlyList<string> targetModelPaths,
                int? sourceAnimationIndex,
                int targetAnimationCount,
                out IReadOnlyList<string> referencePaths)
        {
            referencePaths = Array.Empty<string>();
            var empty = new Dictionary<int, IReadOnlyList<Animation>>();
            if (string.IsNullOrWhiteSpace(sourceAnimationPath) ||
                string.IsNullOrWhiteSpace(targetModelPath) ||
                string.IsNullOrWhiteSpace(referenceRoot) ||
                targetAnimationCount <= 0)
                return empty;

            var sourceFileName = Path.GetFileNameWithoutExtension(sourceAnimationPath) ?? string.Empty;
            var sourceMatch = Regex.Match(
                sourceFileName, @"(?<!\d)(?<id>\d{3,4})(?=_)", RegexOptions.IgnoreCase);
            var targetId = ExtractCharacterToken(targetModelPath);
            if (!sourceMatch.Success ||
                string.IsNullOrWhiteSpace(targetId) ||
                sourceMatch.Groups["id"].Value.Length != targetId.Length ||
                sourceMatch.Groups["id"].Value.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                return empty;

            var targetFileStem = sourceFileName.Substring(0, sourceMatch.Index) + targetId +
                                 sourceFileName.Substring(sourceMatch.Index + sourceMatch.Length);
            var isSplitDanceAnimation = Regex.IsMatch(
                sourceFileName,
                @"^pc\d{3}_\d+_p(?:_(?:\d+|f|h\d+))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var destinationBaseStem = isSplitDanceAnimation
                ? Regex.Replace(targetFileStem, @"_(?:\d+|f|h\d+)$", string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                : targetFileStem;
            var destinationStems = isSplitDanceAnimation
                ? GetDestinationReferenceStems(destinationBaseStem, targetModelPaths)
                : new[] { destinationBaseStem };
            var destinationFileNames = destinationStems
                .Select(stem => stem + ".GAP")
                .ToArray();

            var cacheKey = MakeDestinationReferenceCacheKey(
                referenceRoot, string.Join("|", destinationFileNames));
            IReadOnlyList<DestinationReferencePack> candidatePacks;
            lock (DestinationReferenceCacheLock)
            {
                if (!DestinationReferenceCache.TryGetValue(cacheKey, out candidatePacks))
                {
                    candidatePacks = LoadDestinationReferencePacks(
                        referenceRoot, destinationFileNames, destinationBaseStem);
                    DestinationReferenceCache[cacheKey] = candidatePacks;
                }
            }

            var paths = candidatePacks
                .Select(candidate => candidate.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            referencePaths = paths;
            if (candidatePacks.Count == 0)
            {
                Logger.Debug($"Same-game retarget: no destination action found for {sourceAnimationPath} " +
                             $"({string.Join(", ", destinationFileNames)}).");
                return empty;
            }

            var result = new Dictionary<int, IReadOnlyList<Animation>>();
            if (sourceAnimationIndex.HasValue)
            {
                var references = candidatePacks
                    .Where(candidate => sourceAnimationIndex.Value >= 0 &&
                                        sourceAnimationIndex.Value < candidate.Pack.Animations.Count)
                    .Select(candidate => candidate.Pack.Animations[sourceAnimationIndex.Value])
                    .Where(animation => animation != null && animation.Duration > 0)
                    .ToArray();
                if (references.Length > 0)
                    result[0] = references;
                return result;
            }

            for (var animationIndex = 0; animationIndex < targetAnimationCount; animationIndex++)
            {
                var references = candidatePacks
                    .Where(candidate => animationIndex >= 0 &&
                                        animationIndex < candidate.Pack.Animations.Count)
                    .Select(candidate => candidate.Pack.Animations[animationIndex])
                    .Where(animation => animation != null && animation.Duration > 0)
                    .ToArray();
                if (references.Length > 0)
                    result[animationIndex] = references;
            }

            return result;
        }

        private static IReadOnlyList<DestinationReferencePack> LoadDestinationReferencePacks(
            string referenceRoot,
            IReadOnlyList<string> fileNames,
            string destinationBaseStem)
        {
            if (!Directory.Exists(referenceRoot))
                return Array.Empty<DestinationReferencePack>();

            IEnumerable<string> paths;
            try
            {
                paths = fileNames
                    .SelectMany(fileName => Directory.EnumerateFiles(
                        referenceRoot, fileName, SearchOption.AllDirectories))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => Path.GetFileNameWithoutExtension(path).Equals(
                        destinationBaseStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception)
            {
                Logger.Debug($"Same-game retarget: failed to scan destination actions in {referenceRoot}: {exception}");
                return Array.Empty<DestinationReferencePack>();
            }

            var result = new List<DestinationReferencePack>();
            foreach (var path in paths)
            {
                try
                {
                    var pack = Resource.Load<AnimationPack>(path);
                    if (pack?.Animations?.Any(animation => animation.Duration > 0) == true)
                        result.Add(new DestinationReferencePack { Pack = pack, Path = path });
                }
                catch (Exception exception)
                {
                    Logger.Debug($"Same-game retarget: failed to load destination action {path}: {exception}");
                }
            }

            return result;
        }

        private static IReadOnlyList<string> GetDestinationReferenceStems(
            string destinationBaseStem,
            IReadOnlyList<string> targetModelPaths)
        {
            var stems = new List<string>();
            var hasBodyVariant = false;
            foreach (var path in targetModelPaths ?? Array.Empty<string>())
            {
                var stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                var bodyMatch = Regex.Match(
                    stem, @"^pc\d+_(?<variant>\d+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (bodyMatch.Success)
                {
                    hasBodyVariant = true;
                    stems.Add(destinationBaseStem + "_" + bodyMatch.Groups["variant"].Value);
                    continue;
                }

                var faceMatch = Regex.Match(
                    stem, @"^pc\d+_f\d+$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (faceMatch.Success)
                {
                    stems.Add(destinationBaseStem + "_f");
                    continue;
                }

                var hairMatch = Regex.Match(
                    stem, @"^pc\d+_(?<hair>h\d+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (hairMatch.Success)
                    stems.Add(destinationBaseStem + "_" + hairMatch.Groups["hair"].Value);
            }

            // If the selected body has no outfit suffix, the unsuffixed body
            // GAP is the only body candidate. Numeric outfit GAPs belong to
            // other models and must never be mixed into this preview.
            if (!hasBodyVariant)
                stems.Add(destinationBaseStem);

            return stems
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string MakeDestinationReferenceCacheKey(
            string referenceRoot, string fileNames)
        {
            try
            {
                return Path.GetFullPath(referenceRoot) + "|" + fileNames;
            }
            catch
            {
                return referenceRoot + "|" + fileNames;
            }
        }

        private static Animation TryLoadNativeBase(
            Model targetModel,
            string targetModelPath,
            string referenceRoot,
            out string referencePath)
        {
            referencePath = null;
            if (!DancingKneeCorrection.SupportsTarget(targetModel) ||
                string.IsNullOrWhiteSpace(targetModelPath) ||
                string.IsNullOrWhiteSpace(referenceRoot))
                return null;

            var danceId = ExtractDanceId(targetModelPath);
            if (string.IsNullOrWhiteSpace(danceId))
                return null;

            string cacheKey;
            try
            {
                cacheKey = Path.GetFullPath(referenceRoot) + "|" + danceId;
            }
            catch
            {
                cacheKey = referenceRoot + "|" + danceId;
            }

            lock (NativeReferenceCacheLock)
            {
                if (NativeReferenceCache.TryGetValue(cacheKey, out var cached))
                {
                    referencePath = cached.Path;
                    return cached.Animation;
                }
            }

            foreach (var path in GetNativeReferencePaths(referenceRoot, danceId))
            {
                try
                {
                    var pack = Resource.Load<AnimationPack>(path);
                    var reference = pack?.Animations?.FirstOrDefault(
                        candidate => candidate.Duration > 0 && HasReferenceTracks(candidate));
                    if (reference != null)
                    {
                        referencePath = path;
                        lock (NativeReferenceCacheLock)
                        {
                            NativeReferenceCache[cacheKey] = new NativeReference {
                                Animation = reference,
                                Path = path
                            };
                        }
                        return reference;
                    }
                }
                catch (Exception exception)
                {
                    Logger.Debug($"P5D retarget: failed to load native reference {path}: {exception}");
                }
            }

            Logger.Debug($"P5D retarget: no native calibration clip found for character {danceId}.");
            return null;
        }

        private static bool HasReferenceTracks(Animation animation)
        {
            var names = animation.Controllers
                .Where(controller => controller.TargetKind == TargetKind.Node)
                .Select(controller => controller.TargetName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new[] { "LeftLeg", "L_Knee_Roll_01", "L_Knee_Roll_02", "L_ExKnee" }
                .All(names.Contains) ||
                new[] { "RightLeg", "R_Knee_Roll_01", "R_Knee_Roll_02", "R_ExKnee" }
                .All(names.Contains);
        }

        private static IEnumerable<string> GetNativeReferencePaths(string referenceRoot, string danceId)
        {
            if (!Directory.Exists(referenceRoot))
                yield break;

            var pattern = NativeReferenceStems.TryGetValue(danceId, out var knownStem)
                ? knownStem + "*.GAP"
                // This keeps future P5D character ids usable without allowing
                // a face, hair or costume overlay to pass track validation.
                : "pc" + danceId + "_*_p*.GAP";
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(
                    referenceRoot, pattern, SearchOption.AllDirectories)
                    .OrderBy(path => Path.GetFileName(path).Equals(
                        (knownStem ?? string.Empty) + ".GAP",
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception)
            {
                Logger.Debug($"P5D retarget: failed to scan browser root {referenceRoot}: {exception}");
                yield break;
            }

            foreach (var path in paths)
                yield return path;
        }

        private static string ExtractDanceId(string modelPath)
        {
            var stem = Path.GetFileNameWithoutExtension(modelPath) ?? string.Empty;
            return Regex.Match(stem, @"^pc(?<id>\d{3})_", RegexOptions.IgnoreCase)
                .Groups["id"].Value;
        }

        private static string ExtractCharacterToken(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            return Regex.Match(stem, @"(?<!\d)(?<id>\d{3,4})(?=_)", RegexOptions.IgnoreCase)
                .Groups["id"].Value;
        }

    }

    public sealed class P5dRetargetResult
    {
        internal P5dRetargetResult(
            bool isP5dTarget,
            bool kneeCorrectionApplied,
            string referencePath,
            int destinationTracksApplied,
            IReadOnlyList<string> destinationReferencePaths)
        {
            IsP5dTarget = isP5dTarget;
            KneeCorrectionApplied = kneeCorrectionApplied;
            ReferencePath = referencePath;
            DestinationTracksApplied = destinationTracksApplied;
            DestinationReferencePaths = destinationReferencePaths ?? Array.Empty<string>();
        }

        public bool IsP5dTarget { get; }
        public bool KneeCorrectionApplied { get; }
        public string ReferencePath { get; }
        public int DestinationTracksApplied { get; }
        public IReadOnlyList<string> DestinationReferencePaths { get; }

        internal P5dRetargetResult WithReferencePath(string referencePath)
        {
            return new P5dRetargetResult(
                IsP5dTarget, KneeCorrectionApplied, referencePath,
                DestinationTracksApplied, DestinationReferencePaths);
        }

        internal P5dRetargetResult WithDestinationTracks(
            int destinationTracksApplied,
            IReadOnlyList<string> destinationReferencePaths)
        {
            return new P5dRetargetResult(
                IsP5dTarget, KneeCorrectionApplied, ReferencePath,
                destinationTracksApplied, destinationReferencePaths);
        }
    }
}
