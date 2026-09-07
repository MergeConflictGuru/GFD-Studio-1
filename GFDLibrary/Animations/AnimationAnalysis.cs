using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GFDLibrary.Animations
{
    public static class AnimationAnalysis
    {
        private const float TransformEpsilon = 0.0001f;

        /// <summary>
        /// Returns whether an animation contains changing node transforms.
        ///
        /// Node tracks are the body animation path. A node track with no changing
        /// transform leaves the model in one pose, which is not useful in an
        /// animation browser even when it contains serialized key frames.
        /// </summary>
        public static bool HasBodyMotion(Animation animation)
        {
            return HasBodyMotion(animation, null);
        }

        /// <summary>
        /// Returns whether an animation has usable body motion for the supplied model nodes.
        /// </summary>
        public static bool HasBodyMotion(Animation animation, ISet<string> targetNodeNames)
        {
            if (animation == null || animation.Duration <= TransformEpsilon)
                return false;

            return animation?.Controllers?.Any(controller =>
                controller?.TargetKind == TargetKind.Node &&
                (targetNodeNames == null || targetNodeNames.Contains(controller.TargetName)) &&
                controller.Layers?.Any(layer => HasChangingTransform(layer, animation.Duration)) == true) == true;
        }

        public static IReadOnlyCollection<string> GetBodyTargetNames(Animation animation)
        {
            var targetNames = animation?.Controllers?
                .Where(controller => controller?.TargetKind == TargetKind.Node &&
                                     !string.IsNullOrWhiteSpace(controller.TargetName) &&
                                     controller.Layers?.Any(layer => HasChangingTransform(layer, animation.Duration)) == true)
                .Select(controller => controller.TargetName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            // The Character Browser uses these names as a cheap compatibility filter.
            // Include equivalent P5/R <-> Dancing humanoid node names so a retargetable
            // animation is not hidden merely because the two games name the same bone
            // differently (for example Bip01 Pelvis vs Hips).
            if (targetNames.Length == 0)
                return targetNames;

            var compatibleNames = new HashSet<string>(targetNames, StringComparer.OrdinalIgnoreCase);
            foreach (var targetName in targetNames)
                AddCrossGameTargetAliases(compatibleNames, targetName);

            return compatibleNames.ToArray();
        }

        private static void AddCrossGameTargetAliases(ISet<string> names, string targetName)
        {
            var role = AnimationRetargetMap.GetSkeletonRoleForMatching(targetName);
            if (string.IsNullOrWhiteSpace(role))
                return;

            switch (role.ToLowerInvariant())
            {
                case "hips":
                    Add(names, "Hips", "Bip01 Pelvis");
                    break;
                case "spine":
                    Add(names, "Spine", "Bip01 Spine");
                    break;
                case "spine1":
                    Add(names, "Spine1", "Bip01 Spine1");
                    break;
                case "spine2":
                    Add(names, "Spine2", "Bip01 Spine2");
                    break;
                case "neck":
                    Add(names, "Neck", "Bip01 Neck");
                    break;
                case "head":
                    Add(names, "Head", "Bip01 Head");
                    break;
                case "leftshoulder":
                    Add(names, "LeftShoulder", "Bip01 L Clavicle");
                    break;
                case "rightshoulder":
                    Add(names, "RightShoulder", "Bip01 R Clavicle");
                    break;
                case "leftarm":
                    Add(names, "LeftArm", "Bip01 L UpperArm");
                    break;
                case "rightarm":
                    Add(names, "RightArm", "Bip01 R UpperArm");
                    break;
                case "leftforearm":
                    Add(names, "LeftForeArm", "LeftForearm", "Bip01 L Forearm");
                    break;
                case "rightforearm":
                    Add(names, "RightForeArm", "RightForearm", "Bip01 R Forearm");
                    break;
                case "lefthand":
                    Add(names, "LeftHand", "Bip01 L Hand");
                    break;
                case "righthand":
                    Add(names, "RightHand", "Bip01 R Hand");
                    break;
                case "leftupleg":
                    Add(names, "LeftUpLeg", "Bip01 L Thigh");
                    break;
                case "rightupleg":
                    Add(names, "RightUpLeg", "Bip01 R Thigh");
                    break;
                case "leftleg":
                    Add(names, "LeftLeg", "Bip01 L Calf");
                    break;
                case "rightleg":
                    Add(names, "RightLeg", "Bip01 R Calf");
                    break;
                case "leftfoot":
                    Add(names, "LeftFoot", "Bip01 L Foot");
                    break;
                case "rightfoot":
                    Add(names, "RightFoot", "Bip01 R Foot");
                    break;
                case "lefttoe":
                    Add(names, "LeftToe0", "LeftToe2", "Bip01 L Toe0", "Bip01 L Toe2");
                    break;
                case "righttoe":
                    Add(names, "RightToe0", "RightToe2", "Bip01 R Toe0", "Bip01 R Toe2");
                    break;
                default:
                    AddFingerAliases(names, role);
                    break;
            }
        }

        private static void AddFingerAliases(ISet<string> names, string role)
        {
            var lower = role.ToLowerInvariant();
            var side = lower.StartsWith("left", StringComparison.Ordinal) ? "Left" :
                       lower.StartsWith("right", StringComparison.Ordinal) ? "Right" : null;
            if (side == null)
                return;

            var remainder = lower.Substring(side.Length);
            var fingerNames = new[] { "thumb", "index", "middle", "ring", "pinky" };
            var fingerIndex = Array.FindIndex(fingerNames,
                finger => remainder.StartsWith(finger, StringComparison.Ordinal));
            if (fingerIndex < 0)
                return;

            var segmentText = remainder.Substring(fingerNames[fingerIndex].Length);
            if (!int.TryParse(segmentText, out var segment) || segment < 1 || segment > 3)
                return;

            var p5dFingerName = char.ToUpperInvariant(fingerNames[fingerIndex][0]) +
                                fingerNames[fingerIndex].Substring(1);
            var bipSide = side == "Left" ? "L" : "R";
            var bipFinger = fingerIndex;
            var bipSegment = segment - 1;

            Add(names,
                $"{side}Hand{p5dFingerName}{segment}",
                $"{side}Finger{bipFinger}{bipSegment}",
                $"Bip01 {bipSide} Finger{bipFinger}{bipSegment}");
        }

        private static void Add(ISet<string> names, params string[] aliases)
        {
            foreach (var alias in aliases)
                names.Add(alias);
        }

        private static bool HasChangingTransform(AnimationLayer layer, float duration)
        {
            if (layer == null || !layer.HasPRSKeyFrames || layer.Keys == null || layer.Keys.Count < 2)
                return false;

            // GLModel loops animation time from zero up to (but not including) Duration.
            // Keys outside that interval can never drive the preview.
            var keys = layer.Keys.OfType<PRSKey>()
                .Where(key => key.Time >= 0 && key.Time < duration)
                .OrderBy(key => key.Time)
                .GroupBy(key => key.Time)
                .Select(group => group.Last())
                .ToList();
            if (keys.Count < 2)
                return false;

            var first = keys[0];
            foreach (var key in keys.Skip(1))
            {
                if (first.HasPosition != key.HasPosition ||
                    first.HasRotation != key.HasRotation ||
                    first.HasScale != key.HasScale)
                    return true;

                if (first.HasPosition && !NearlyEqual(
                        first.Position * layer.PositionScale,
                        key.Position * layer.PositionScale))
                    return true;

                if (first.HasRotation && !SameRotation(first.Rotation, key.Rotation))
                    return true;

                if (first.HasScale && !NearlyEqual(
                        first.Scale * layer.ScaleScale,
                        key.Scale * layer.ScaleScale))
                    return true;
            }

            return false;
        }

        private static bool NearlyEqual(Vector3 first, Vector3 second)
        {
            return Vector3.DistanceSquared(first, second) <=
                   TransformEpsilon * TransformEpsilon;
        }

        private static bool SameRotation(Quaternion first, Quaternion second)
        {
            if (first.LengthSquared() <= TransformEpsilon || second.LengthSquared() <= TransformEpsilon)
                return NearlyEqual(new Vector3(first.X, first.Y, first.Z),
                                   new Vector3(second.X, second.Y, second.Z)) &&
                       Math.Abs(first.W - second.W) <= TransformEpsilon;

            var dot = Math.Abs(Quaternion.Dot(Quaternion.Normalize(first), Quaternion.Normalize(second)));
            return Math.Abs(1f - dot) <= TransformEpsilon;
        }
    }
}
