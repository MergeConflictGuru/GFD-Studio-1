using System;
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
            return animation?.Controllers?.Any(controller =>
                controller?.TargetKind == TargetKind.Node &&
                controller.Layers?.Any(HasChangingTransform) == true) == true;
        }

        private static bool HasChangingTransform(AnimationLayer layer)
        {
            if (layer == null || !layer.HasPRSKeyFrames || layer.Keys == null || layer.Keys.Count < 2)
                return false;

            var keys = layer.Keys.OfType<PRSKey>().ToList();
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
