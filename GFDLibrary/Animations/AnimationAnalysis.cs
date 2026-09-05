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
            return animation?.Controllers?
                .Where(controller => controller?.TargetKind == TargetKind.Node &&
                                     !string.IsNullOrWhiteSpace(controller.TargetName) &&
                                     controller.Layers?.Any(layer => HasChangingTransform(layer, animation.Duration)) == true)
                .Select(controller => controller.TargetName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
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
