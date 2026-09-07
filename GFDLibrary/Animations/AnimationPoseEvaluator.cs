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

        internal static void Sample(AnimationLayer layer, float time, ref Vector3 position, ref Quaternion rotation, ref Vector3 scale)
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

    /// <summary>
    /// Reusable evaluator for a single animation. It resolves only the requested nodes and their
    /// ancestors, which is important for the global matcher because cosmetic and helper branches
    /// do not contribute to the canonical pose descriptor.
    /// </summary>
    public sealed class AnimationPoseSampler
    {
        private readonly Dictionary<string, AnimationController[]> _controllers;

        public AnimationPoseSampler(Model model, Animation animation)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            Animation = animation ?? throw new ArgumentNullException(nameof(animation));
            _controllers = animation.Controllers
                .Where(controller => controller.TargetKind == TargetKind.Node)
                .GroupBy(controller => controller.TargetName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        public Model Model { get; }
        public Animation Animation { get; }

        public Dictionary<Node, Matrix4x4> Evaluate(IReadOnlyList<Node> requestedNodes, float time)
        {
            if (requestedNodes == null)
                throw new ArgumentNullException(nameof(requestedNodes));

            var result = new Dictionary<Node, Matrix4x4>(requestedNodes.Count * 2);
            foreach (var node in requestedNodes)
                if (node != null)
                    EvaluateNode(node, time, result);
            return result;
        }

        private Matrix4x4 EvaluateNode(Node node, float time, Dictionary<Node, Matrix4x4> result)
        {
            if (result.TryGetValue(node, out var existing))
                return existing;

            var position = node.Translation;
            var rotation = node.Rotation;
            var scale = node.Scale;
            if (_controllers.TryGetValue(node.Name ?? string.Empty, out var controllers))
            {
                foreach (var controller in controllers)
                    foreach (var layer in controller.Layers)
                        AnimationPoseEvaluator.Sample(layer, time, ref position, ref rotation, ref scale);
            }

            var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateScale(scale);
            local.Translation = position;
            var world = node.Parent == null
                ? local
                : local * EvaluateNode(node.Parent, time, result);
            result[node] = world;
            return world;
        }
    }
}
