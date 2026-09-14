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
    /// as one operation. The optional native base animation is used only to
    /// calibrate P5D Dance knee helper bones.
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

        private static readonly Dictionary<string, NativeReference> NativeReferenceCache =
            new Dictionary<string, NativeReference>(StringComparer.OrdinalIgnoreCase);
        private static readonly object NativeReferenceCacheLock = new object();

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
            if (pack == null)
                throw new ArgumentNullException(nameof(pack));
            if (originalModel == null)
                throw new ArgumentNullException(nameof(originalModel));
            if (targetModel == null)
                throw new ArgumentNullException(nameof(targetModel));

            pack.Retarget(originalModel, targetModel, false, useLocalBindSpace);

            var isP5dTarget = DancingKneeCorrection.SupportsTarget(targetModel);
            if (!isP5dTarget || nativeBase == null)
            {
                if (isP5dTarget && nativeBase == null)
                    Logger.Debug("P5D retarget: native Dance base is unavailable; knee helpers were not generated.");
                return new P5dRetargetResult(isP5dTarget, false, null);
            }

            try
            {
                DancingKneeCorrection.Apply(pack, targetModel, nativeBase);
                return new P5dRetargetResult(isP5dTarget, true, null);
            }
            catch (Exception exception)
            {
                // Corrective calibration is optional for previewing. A bad or
                // missing reference must not turn into a UI error dialog.
                Logger.Debug($"P5D retarget: knee correction unavailable: {exception}");
                return new P5dRetargetResult(isP5dTarget, false, null);
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
            bool useLocalBindSpace = false)
        {
            if (animation == null)
                throw new ArgumentNullException(nameof(animation));

            var nativeBase = TryLoadNativeBase(
                targetModel, targetModelPath, referenceRoot, out var referencePath);
            var pack = new AnimationPack(animation.Version)
            {
                Animations = new List<Animation> { animation }
            };
            var result = Retarget(pack, originalModel, targetModel, nativeBase, useLocalBindSpace);
            return result.WithReferencePath(result.KneeCorrectionApplied ? referencePath : null);
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
            bool useLocalBindSpace = false)
        {
            var nativeBase = TryLoadNativeBase(
                targetModel, targetModelPath, referenceRoot, out var referencePath);
            var result = Retarget(pack, originalModel, targetModel, nativeBase, useLocalBindSpace);
            return result.WithReferencePath(result.KneeCorrectionApplied ? referencePath : null);
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

    }

    public sealed class P5dRetargetResult
    {
        internal P5dRetargetResult(bool isP5dTarget, bool kneeCorrectionApplied, string referencePath)
        {
            IsP5dTarget = isP5dTarget;
            KneeCorrectionApplied = kneeCorrectionApplied;
            ReferencePath = referencePath;
        }

        public bool IsP5dTarget { get; }
        public bool KneeCorrectionApplied { get; }
        public string ReferencePath { get; }

        internal P5dRetargetResult WithReferencePath(string referencePath)
        {
            return new P5dRetargetResult(IsP5dTarget, KneeCorrectionApplied, referencePath);
        }
    }
}
