using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>Bakes an arbitrary matcher clip (including a StitchedAnimation) back to a normal GFD Animation.</summary>
public static class GfdAnimationClipBaker
{
    public static Animation Bake(IAnimationClip clip, Model targetModel, uint version, CancellationToken cancellationToken = default)
    {
        if (clip == null)
            throw new ArgumentNullException(nameof(clip));
        if (targetModel == null)
            throw new ArgumentNullException(nameof(targetModel));

        var nodes = targetModel.Nodes.ToArray();
        if (nodes.Length == 0)
            throw new InvalidOperationException("The selected model has no nodes.");

        var nodeIndex = new System.Collections.Generic.Dictionary<Node, int>(nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
            nodeIndex[nodes[i]] = i;

        var boneForNode = new int[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
            boneForNode[i] = clip.Skeleton.TryGetBone(nodes[i].Name, out var bone) ? bone : -1;

        var animation = new Animation(version)
        {
            Duration = Math.Max(0f, (clip.FrameCount - 1) / clip.FramesPerSecond)
        };

        var layers = new AnimationLayer[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            if (boneForNode[i] < 0)
                continue;

            var layer = new AnimationLayer(version)
            {
                KeyType = KeyType.NodePRS,
                PositionScale = Vector3.One,
                ScaleScale = Vector3.One
            };
            var controller = new AnimationController(version)
            {
                TargetKind = TargetKind.Node,
                TargetId = i,
                TargetName = nodes[i].Name
            };
            controller.Layers.Add(layer);
            animation.Controllers.Add(controller);
            layers[i] = layer;
        }

        var pose = new BoneTransform[clip.Skeleton.BoneCount];
        var globals = new Matrix4x4[nodes.Length];
        for (var frame = 0; frame < clip.FrameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clip.SampleGlobalPose(frame, pose);
            var time = frame / clip.FramesPerSecond;

            for (var i = 0; i < nodes.Length; i++)
            {
                var bone = boneForNode[i];
                if (bone >= 0)
                {
                    var transform = pose[bone];
                    var global = Matrix4x4.CreateFromQuaternion(transform.Rotation) * Matrix4x4.CreateScale(transform.Scale);
                    global.Translation = transform.Position;
                    globals[i] = global;
                }
                else
                {
                    globals[i] = nodes[i].WorldTransform;
                }
            }

            for (var i = 0; i < nodes.Length; i++)
            {
                var layer = layers[i];
                if (layer == null)
                    continue;

                var local = globals[i];
                if (nodes[i].Parent != null && nodeIndex.TryGetValue(nodes[i].Parent, out var parentIndex) &&
                    Matrix4x4.Invert(globals[parentIndex], out var inverseParent))
                {
                    local = globals[i] * inverseParent;
                }

                if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
                {
                    scale = Vector3.One;
                    rotation = Quaternion.Identity;
                    translation = Vector3.Zero;
                }
                if (rotation.LengthSquared() < 1e-10f)
                    rotation = Quaternion.Identity;

                layer.Keys.Add(new PRSKey(KeyType.NodePRS)
                {
                    Time = time,
                    Position = translation,
                    Rotation = Quaternion.Normalize(rotation),
                    Scale = scale
                });
            }
        }

        return animation;
    }
}
