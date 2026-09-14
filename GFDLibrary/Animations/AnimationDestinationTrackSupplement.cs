using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    /// <summary>
    /// Adds authored destination-only node tracks after a normal retarget.
    /// A destination reference is only allowed to fill nodes which do not
    /// exist in the source model; source-retargeted common joints remain
    /// authoritative.
    /// </summary>
    internal static class AnimationDestinationTrackSupplement
    {
        internal static int Apply(
            AnimationPack pack,
            Model sourceModel,
            Model targetModel,
            IReadOnlyDictionary<int, IReadOnlyList<Animation>> references)
        {
            if (pack == null || sourceModel == null || targetModel == null || references == null)
                return 0;

            var sourceNames = sourceModel.Nodes
                .Select(node => node.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetNodes = targetModel.Nodes.ToArray();
            var targetNodesByName = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            var targetNodeIds = new Dictionary<Node, int>();
            for (var i = 0; i < targetNodes.Length; i++)
            {
                targetNodeIds.TryAdd(targetNodes[i], i);
                targetNodesByName.TryAdd(targetNodes[i].Name, targetNodes[i]);
            }

            var targetOnlyNames = targetNodes
                .Where(node => !sourceNames.Contains(node.Name))
                .Select(node => node.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (targetOnlyNames.Count == 0)
                return 0;

            var addedCount = 0;
            for (var animationIndex = 0; animationIndex < pack.Animations.Count; animationIndex++)
            {
                if (!references.TryGetValue(animationIndex, out var referenceAnimations))
                    continue;

                var animation = pack.Animations[animationIndex];
                if (animation?.Controllers == null || animation.Duration <= 0)
                    continue;

                var existingNames = animation.Controllers
                    .Where(controller => controller.TargetKind == TargetKind.Node)
                    .Select(controller => controller.TargetName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var additions = new List<AnimationController>();

                foreach (var reference in referenceAnimations ?? Array.Empty<Animation>())
                {
                    if (reference == null)
                        continue;

                    foreach (var controller in reference.Controllers ?? Enumerable.Empty<AnimationController>())
                    {
                        if (controller == null)
                            continue;

                        var layers = controller.Layers ?? new List<AnimationLayer>();
                        if (controller.TargetKind != TargetKind.Node ||
                            string.IsNullOrWhiteSpace(controller.TargetName) ||
                            !targetOnlyNames.Contains(controller.TargetName) ||
                            existingNames.Contains(controller.TargetName) ||
                            !targetNodesByName.TryGetValue(controller.TargetName, out var targetNode) ||
                            !layers.Any(layer => layer != null && layer.HasPRSKeyFrames &&
                                                 layer.Keys?.Count > 0))
                            continue;

                        try
                        {
                            var copy = CloneController(
                                controller,
                                animation.Version,
                                targetNode,
                                targetNodeIds[targetNode],
                                reference.Duration,
                                animation.Duration);
                            additions.Add(copy);
                            existingNames.Add(controller.TargetName);
                            addedCount++;
                        }
                        catch (Exception exception)
                        {
                            Logger.Debug($"Same-game retarget: could not copy destination controller " +
                                         $"{controller.TargetName}: {exception}");
                        }
                    }
                }

                animation.Controllers.AddRange(additions);
            }

            return addedCount;
        }

        private static AnimationController CloneController(
            AnimationController source,
            uint targetVersion,
            Node targetNode,
            int targetId,
            float referenceDuration,
            float targetDuration)
        {
            // Resource serialization is the library's complete-copy path. It
            // preserves split Dance layers (Type31/RHalf/SHalf) and any future
            // key data without reimplementing every key subtype here.
            using var stream = new MemoryStream();
            source.Save(stream, true);
            stream.Position = 0;
            var copy = Resource.Load<AnimationController>(stream, true);
            copy.Version = targetVersion;
            copy.TargetName = targetNode.Name;
            copy.TargetId = targetId;

            foreach (var layer in copy.Layers ?? new List<AnimationLayer>())
            {
                if (layer == null)
                    continue;

                layer.Version = targetVersion;
                if (referenceDuration <= 0 || targetDuration <= 0)
                    continue;

                var timeScale = targetDuration / referenceDuration;
                foreach (var key in layer.Keys ?? Enumerable.Empty<Key>())
                    key.Time = Math.Clamp(key.Time * timeScale, 0, targetDuration);
            }

            return copy;
        }
    }
}
