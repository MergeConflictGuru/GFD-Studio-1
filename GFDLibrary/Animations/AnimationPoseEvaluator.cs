using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    /// <summary>Samples absolute node PRS layers into model-space transforms without a graphics context.</summary>
    public static class AnimationPoseEvaluator
    {
        public static Dictionary<Node, Matrix4x4> Evaluate(Model model, Animation animation, float time)
        {
            var controllers = animation?.Controllers.Where(c => c.TargetKind == TargetKind.Node)
                .ToLookup(c => c.TargetName);
            var result = new Dictionary<Node, Matrix4x4>();
            foreach (var node in model.Nodes)
            {
                var position = node.Translation;
                var rotation = node.Rotation;
                var scale = node.Scale;
                if (controllers != null)
                    foreach (var controller in controllers[node.Name])
                        foreach (var layer in controller.Layers)
                            Sample(layer, time, ref position, ref rotation, ref scale);
                var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateScale(scale);
                local.Translation = position;
                result[node] = node.Parent == null ? local : local * result[node.Parent];
            }
            return result;
        }

        private static void Sample(AnimationLayer layer, float time, ref Vector3 position, ref Quaternion rotation, ref Vector3 scale)
        {
            if (!layer.HasPRSKeyFrames || layer.Keys.Count == 0)
                return;
            // Clamp at the endpoints when baking: an end key must not sample the
            // first frame again. Respect independent timings of split channels.
            var low = 0;
            var high = layer.Keys.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (layer.Keys[middle].Time <= time) low = middle + 1;
                else high = middle;
            }
            var previous = (PRSKey)layer.Keys[Math.Max(0, low - 1)];
            var next = (PRSKey)layer.Keys[Math.Min(low, layer.Keys.Count - 1)];
            var amount = next.Time > previous.Time ? Math.Clamp((time - previous.Time) / (next.Time - previous.Time), 0, 1) : 0;
            if (previous.HasPosition)
                position = Vector3.Lerp(previous.Position, next.Position, amount) * layer.PositionScale;
            if (previous.HasRotation)
                rotation = Quaternion.Slerp(Quaternion.Normalize(previous.Rotation), Quaternion.Normalize(next.Rotation), amount);
            if (previous.HasScale)
                scale = Vector3.Lerp(previous.Scale, next.Scale, amount) * layer.ScaleScale;
        }
    }
}
