using System;
using System.Buffers;
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
        private readonly Node[] _evaluationNodes;
        private readonly int[] _parentIndices;
        private readonly int[] _requestedNodeIndices;

        public AnimationPoseSampler(
            Model model,
            Animation animation,
            IReadOnlyList<Node> requestedNodes)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            Animation = animation ?? throw new ArgumentNullException(nameof(animation));
            if (requestedNodes == null)
                throw new ArgumentNullException(nameof(requestedNodes));

            _controllers = animation.Controllers
                .Where(controller => controller.TargetKind == TargetKind.Node)
                .GroupBy(controller => controller.TargetName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            var evaluationNodes = new List<Node>(requestedNodes.Count * 2);
            var nodeIndices = new Dictionary<Node, int>();
            var visiting = new HashSet<Node>();

            void AddNode(Node node)
            {
                if (nodeIndices.ContainsKey(node))
                    return;
                if (!visiting.Add(node))
                    throw new InvalidOperationException("Animation node hierarchy contains a cycle.");

                if (node.Parent != null)
                    AddNode(node.Parent);

                visiting.Remove(node);
                nodeIndices.Add(node, evaluationNodes.Count);
                evaluationNodes.Add(node);
            }

            for (var i = 0; i < requestedNodes.Count; i++)
            {
                var node = requestedNodes[i] ??
                    throw new ArgumentException("Requested animation nodes cannot contain null entries.", nameof(requestedNodes));
                AddNode(node);
            }

            _evaluationNodes = evaluationNodes.ToArray();
            _parentIndices = new int[_evaluationNodes.Length];
            for (var i = 0; i < _evaluationNodes.Length; i++)
            {
                _parentIndices[i] = _evaluationNodes[i].Parent is { } parent &&
                    nodeIndices.TryGetValue(parent, out var parentIndex)
                    ? parentIndex
                    : -1;
            }

            _requestedNodeIndices = new int[requestedNodes.Count];
            for (var i = 0; i < requestedNodes.Count; i++)
                _requestedNodeIndices[i] = nodeIndices[requestedNodes[i]];
        }

        public Model Model { get; }
        public Animation Animation { get; }

        public void Evaluate(Span<Matrix4x4> requestedTransforms, float time)
        {
            if (requestedTransforms.Length < _requestedNodeIndices.Length)
                throw new ArgumentException("Requested transform buffer is too small.", nameof(requestedTransforms));

            var values = ArrayPool<Matrix4x4>.Shared.Rent(_evaluationNodes.Length);
            try
            {
                for (var i = 0; i < _evaluationNodes.Length; i++)
                {
                    var node = _evaluationNodes[i];
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
                    var parentIndex = _parentIndices[i];
                    values[i] = parentIndex < 0 ? local : local * values[parentIndex];
                }

                for (var i = 0; i < _requestedNodeIndices.Length; i++)
                    requestedTransforms[i] = values[_requestedNodeIndices[i]];
            }
            finally
            {
                ArrayPool<Matrix4x4>.Shared.Return(values, clearArray: false);
            }
        }
    }
}
