using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GFDLibrary.Materials;
using GFDLibrary.Models;
using GFDLibrary.Textures;
using GFDLibrary.Animations.Keys;

namespace GFDLibrary.Animations
{
    public enum SplitCharacterPart
    {
        Generic,
        Body,
        Face,
        Hair
    }

    public static class SplitCharacterRetargeter
    {
        /// <summary>
        /// Creates an independent combined preview, keeping all three parts on
        /// one skeleton before retargeting. Input resources are never mutated.
        /// Only skeletal motion is transferred; source morph/material tracks
        /// and unmatched hair/cloth dynamics are not interchangeable across rigs.
        /// </summary>
        public static ModelPack CreatePreview(Model source, AnimationPack animations,
            ModelPack body, ModelPack face, ModelPack hair, Animation nativeBase)
        {
            var combined = Copy(body);
            combined.Textures ??= new TextureDictionary(combined.Version);
            combined.Materials ??= new MaterialDictionary(combined.Version);
            foreach (var original in new[] { face, hair })
            {
                var part = Copy(original);
                if (part.Version != combined.Version)
                    throw new ArgumentException("Body, face and hair must belong to the same target format.");
                if (part.Textures != null)
                    foreach (var texture in part.Textures)
                        if (!combined.Textures.ContainsKey(texture.Key)) combined.Textures.Add(texture.Key, texture.Value);
                if (part.Materials != null)
                    foreach (var material in part.Materials)
                        if (!combined.Materials.ContainsKey(material.Key)) combined.Materials.Add(material.Key, material.Value);
                combined.Model.MergeWith(part.Model);
            }
            combined.AnimationPack = Copy(animations);
            combined.AnimationPack.Retarget(source, combined.Model, false);
            DancingKneeCorrection.Apply(combined.AnimationPack, combined.Model, nativeBase);
            return combined;
        }

        /// <summary>
        /// Bakes a combined character's world motion into a standalone part's
        /// hierarchy and IDs. Face head keys use the body head's local transform
        /// when the face hierarchy differs, matching native Dance _f packs.
        /// These standalone packs must not be layered back onto the combined model.
        /// </summary>
        public static AnimationPack ForStandalonePart(ModelPack combined, Model part,
            SplitCharacterPart partKind = SplitCharacterPart.Generic)
        {
            var result = new AnimationPack(part.Version) {
                // Native Dance component GAPs carry the same pack flags as the
                // body pack. In particular, Bit3 is present on the _f/_h
                // files even though they do not contain blend animations.
                Flags = combined.AnimationPack?.Flags ?? AnimationPackFlags.Bit3
            };
            var combinedNodes = combined.Model.Nodes.ToDictionary(n => n.Name);
            var nodes = part.Nodes.ToArray();
            var outputNodes = partKind == SplitCharacterPart.Hair
                ? HairAnimationNodes(part, nodes)
                : nodes;
            var outputNodeSet = outputNodes.ToHashSet();
            var bind = AnimationPoseEvaluator.Evaluate(combined.Model, null, 0);
            var partBind = AnimationPoseEvaluator.Evaluate(part, null, 0);
            foreach (var source in combined.AnimationPack.Animations)
            {
                var animation = new Animation(part.Version) { Duration = source.Duration, Speed = source.Speed };
                var output = outputNodes.Select(n => {
                    var controller = new AnimationController(part.Version) {
                        TargetKind = TargetKind.Node, TargetName = n.Name,
                        TargetId = AnimationTargetId(nodes, outputNodes, n, partKind)
                    };
                    if (partKind == SplitCharacterPart.Hair && n != part.RootNode)
                    {
                        // This is the native Dance hair layout: position,
                        // rotation and scale are separate component layers.
                        controller.Layers.Add(new AnimationLayer(part.Version) { KeyType = KeyType.Type31 });
                        controller.Layers.Add(new AnimationLayer(part.Version) { KeyType = KeyType.NodeRHalf });
                        controller.Layers.Add(new AnimationLayer(part.Version) { KeyType = KeyType.NodeSHalf });
                    }
                    else
                    {
                        controller.Layers.Add(new AnimationLayer(part.Version) { KeyType = KeyType.NodePRS });
                    }
                    return controller;
                }).ToArray();
                if (partKind == SplitCharacterPart.Hair)
                {
                    var rootController = output[Array.IndexOf(outputNodes, part.RootNode)];
                    rootController.Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS) {
                        Time = 0, Position = part.RootNode.Translation,
                        Rotation = Quaternion.Normalize(part.RootNode.Rotation), Scale = part.RootNode.Scale
                    });
                    foreach (var controller in output.Where(controller => controller.TargetName != part.RootNode.Name))
                    {
                        var node = outputNodes.First(n => n.Name == controller.TargetName);
                        var scaleLayer = controller.Layers[2];
                        scaleLayer.Keys.Add(new PRSKey(KeyType.NodeSHalf) {
                            Time = 0, Scale = node.Scale
                        });
                        if (source.Duration > 0)
                            scaleLayer.Keys.Add(new PRSKey(KeyType.NodeSHalf) {
                                Time = source.Duration, Scale = node.Scale
                            });
                    }
                }
                var times = source.Controllers.SelectMany(c => c.Layers).Where(l => l.HasPRSKeyFrames)
                    .SelectMany(l => l.Keys).Select(k => k.Time).Distinct().OrderBy(t => t);
                foreach (var time in times)
                {
                    var pose = AnimationPoseEvaluator.Evaluate(combined.Model, source, time);
                    var partPose = new Dictionary<Node, Matrix4x4>();
                    for (var i = 0; i < nodes.Length; i++)
                    {
                        var node = nodes[i];
                        var parent = node.Parent == null ? Matrix4x4.Identity : partPose[node.Parent];
                        var local = node.LocalTransform;
                        var outputLocal = local;
                        if (combinedNodes.TryGetValue(node.Name, out var shared))
                        {
                            Matrix4x4.Invert(bind[shared], out var inverseBind);
                            Matrix4x4.Invert(parent, out var inverseParent);
                            local = partBind[node] * inverseBind * pose[shared] * inverseParent;

                            // The face GMD has head directly below RootNode,
                            // while the combined Dance skeleton has head below
                            // neck. Dance _f animations use the latter's local
                            // head transform (relative to the body), not the
                            // face file's absolute world transform.
                            outputLocal = local;
                            if (node.Name.Equals("head", StringComparison.OrdinalIgnoreCase) &&
                                node.Parent != null && shared.Parent != null &&
                                !node.Parent.Name.Equals(shared.Parent.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                Matrix4x4.Invert(pose[shared.Parent], out var inverseSharedParent);
                                outputLocal = pose[shared] * inverseSharedParent;
                            }
                        }
                        partPose[node] = local * parent;
                        if (!outputNodeSet.Contains(node))
                            continue;
                        if (!Matrix4x4.Decompose(outputLocal, out var scale, out var rotation, out var position))
                            throw new InvalidOperationException("Cannot decompose standalone part pose.");
                        if (partKind == SplitCharacterPart.Hair && node == part.RootNode)
                            continue;
                        var outputIndex = Array.IndexOf(outputNodes, node);
                        if (partKind == SplitCharacterPart.Hair)
                        {
                            output[outputIndex].Layers[0].Keys.Add(new KeyType31Dancing {
                                Time = time, Position = position
                            });
                            output[outputIndex].Layers[1].Keys.Add(new PRSKey(KeyType.NodeRHalf) {
                                Time = time, Rotation = Quaternion.Normalize(rotation)
                            });
                        }
                        else
                        {
                            output[outputIndex].Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS) {
                                Time = time, Position = position, Rotation = Quaternion.Normalize(rotation), Scale = scale
                            });
                        }
                    }
                }
                animation.Controllers.AddRange(output);
                result.Animations.Add(animation);
            }
            return result;
        }

        private static Node[] HairAnimationNodes(Model part, Node[] nodes)
        {
            var boneNodes = (part.Bones ?? new List<Bone>())
                .Select(bone => bone.NodeIndex < nodes.Length ? nodes[bone.NodeIndex] : null)
                .Where(node => node != null)
                .ToHashSet();
            var head = nodes.FirstOrDefault(node =>
                node.Name.Equals("head", StringComparison.OrdinalIgnoreCase));
            var sharedAttachmentNodes = new HashSet<Node>();
            for (var node = head; node != null; node = node.Parent)
                sharedAttachmentNodes.Add(node);

            // Native Dance hair packs contain the root and the hair bones.
            // The shared neck/head chain is driven by the body pack, while
            // mesh containers are static and never receive hair controllers.
            return nodes.Where(node => node == part.RootNode ||
                                       (boneNodes.Contains(node) && !sharedAttachmentNodes.Contains(node)))
                .ToArray();
        }

        private static int AnimationTargetId(Node[] nodes, Node[] outputNodes, Node node,
            SplitCharacterPart partKind)
        {
            if (partKind != SplitCharacterPart.Hair)
                return Array.IndexOf(nodes, node);

            // Dance component GAPs compact the controller IDs after omitting
            // the shared neck/head chain. The root remains ID 0.
            return Array.IndexOf(outputNodes, node);
        }

        private static T Copy<T>(T resource) where T : Resource
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            using var stream = new MemoryStream();
            resource.Save(stream, true);
            stream.Position = 0;
            return Resource.Load<T>(stream, true);
        }
    }
}
