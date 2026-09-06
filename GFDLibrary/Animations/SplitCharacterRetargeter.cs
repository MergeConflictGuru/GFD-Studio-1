using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GFDLibrary.Materials;
using GFDLibrary.Models;
using GFDLibrary.Textures;

namespace GFDLibrary.Animations
{
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
        public static AnimationPack ForStandalonePart(ModelPack combined, Model part)
        {
            var result = new AnimationPack(part.Version) {
                // Native Dance component GAPs carry the same pack flags as the
                // body pack. In particular, Bit3 is present on the _f/_h
                // files even though they do not contain blend animations.
                Flags = combined.AnimationPack?.Flags ?? AnimationPackFlags.Bit3
            };
            var combinedNodes = combined.Model.Nodes.ToDictionary(n => n.Name);
            var nodes = part.Nodes.ToArray();
            var bind = AnimationPoseEvaluator.Evaluate(combined.Model, null, 0);
            var partBind = AnimationPoseEvaluator.Evaluate(part, null, 0);
            foreach (var source in combined.AnimationPack.Animations)
            {
                var animation = new Animation(part.Version) { Duration = source.Duration, Speed = source.Speed };
                var output = nodes.Select((n, id) => new AnimationController(part.Version) {
                    TargetKind = TargetKind.Node, TargetName = n.Name, TargetId = id,
                    Layers = { new AnimationLayer(part.Version) { KeyType = KeyType.NodePRS } }
                }).ToArray();
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
                        if (!Matrix4x4.Decompose(outputLocal, out var scale, out var rotation, out var position))
                            throw new InvalidOperationException("Cannot decompose standalone part pose.");
                        output[i].Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS) {
                            Time = time, Position = position, Rotation = Quaternion.Normalize(rotation), Scale = scale
                        });
                    }
                }
                animation.Controllers.AddRange(output);
                result.Animations.Add(animation);
            }
            return result;
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
