using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Stitching;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// Converts matcher clips to the selected target model only at the preview/export boundary.
/// Index construction never calls this class and therefore never depends on the target model.
/// </summary>
public static class GfdAnimationClipBaker
{
    public static Animation Bake(
        IAnimationClip clip,
        Model targetModel,
        uint version,
        CancellationToken cancellationToken = default)
        => BakeRange(clip, targetModel, version, 0, clip?.FrameCount ?? 0, cancellationToken);

    public static Animation BakeRange(
        IAnimationClip clip,
        Model targetModel,
        uint version,
        int firstFrame,
        int frameCount,
        CancellationToken cancellationToken = default)
    {
        if (clip == null)
            throw new ArgumentNullException(nameof(clip));
        if (targetModel == null)
            throw new ArgumentNullException(nameof(targetModel));
        if (clip.FrameCount <= 0)
            throw new ArgumentException("The animation clip has no frames.", nameof(clip));
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        firstFrame = Math.Clamp(firstFrame, 0, clip.FrameCount - 1);
        frameCount = Math.Min(frameCount, clip.FrameCount - firstFrame);
        if (!clip.Skeleton.IsCanonical)
            return BakeFullSkeletonRange(clip, targetModel, version, firstFrame, frameCount, cancellationToken);

        var targetNodes = targetModel.Nodes.ToArray();
        if (targetNodes.Length == 0)
            throw new InvalidOperationException("The selected model has no nodes.");

        var targetCanonicalNodes = GfdAnimationClip.ResolveCanonicalNodes(targetModel);
        var targetCanonicalIndices = CreateTargetCanonicalIndex(targetNodes, targetCanonicalNodes);
        var targetBind = AnimationPoseEvaluator.Evaluate(targetModel, null, 0);
        var targetBindTransforms = targetNodes
            .Select(node => ToBoneTransform(targetBind[node]))
            .ToArray();
        var targetReferenceHeight = CalculateReferenceHeight(targetNodes);
        var heightRatio = targetReferenceHeight / MathF.Max(clip.Skeleton.ReferenceHeight, 1e-4f);

        var animation = new Animation(version)
        {
            Duration = Math.Max(0f, (frameCount - 1) / clip.FramesPerSecond)
        };
        var layers = new AnimationLayer[targetNodes.Length];
        for (var i = 0; i < targetNodes.Length; i++)
        {
            if (!targetCanonicalIndices.TryGetValue(targetNodes[i], out _))
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
                TargetName = targetNodes[i].Name
            };
            controller.Layers.Add(layer);
            animation.Controllers.Add(controller);
            layers[i] = layer;
        }

        var canonicalPose = new BoneTransform[CanonicalSkeleton.JointCount];
        var targetPose = new Matrix4x4[targetNodes.Length];
        var targetPoseValid = new bool[targetNodes.Length];
        var sourceBind = clip.Skeleton.BindPose;
        var rootJoint = (int)CanonicalJoint.Root;

        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clip.SampleGlobalPose(firstFrame + frame, canonicalPose);
            Array.Clear(targetPoseValid, 0, targetPoseValid.Length);
            var time = frame / clip.FramesPerSecond;

            for (var i = 0; i < targetNodes.Length; i++)
            {
                var targetNode = targetNodes[i];
                var local = targetNode.LocalTransform;
                var parentWorld = GetParentWorld(
                    targetNode,
                    targetNodes,
                    targetPose,
                    targetPoseValid,
                    targetBind,
                    out var parentIndex);

                if (targetCanonicalIndices.TryGetValue(targetNode, out var canonicalIndex))
                {
                    var sourceBindTransform = sourceBind[canonicalIndex];
                    var sourcePoseTransform = canonicalPose[canonicalIndex];
                    var targetBindTransform = targetBindTransforms[i];

                    var desiredWorldRotation =
                        ToRotationMatrix(targetBindTransform.Rotation) *
                        InverseRotation(sourceBindTransform.Rotation) *
                        ToRotationMatrix(sourcePoseTransform.Rotation);
                    Matrix4x4.Invert(RotationOnly(parentWorld), out var inverseParentRotation);
                    var localRotation = Quaternion.Normalize(
                        Quaternion.CreateFromRotationMatrix(desiredWorldRotation * inverseParentRotation));

                    var localPosition = targetNode.Translation;
                    if (canonicalIndex == rootJoint)
                    {
                        var worldPosition = targetBindTransform.Position +
                            (sourcePoseTransform.Position - sourceBindTransform.Position) * heightRatio;
                        if (Matrix4x4.Invert(parentWorld, out var inverseParent))
                            localPosition = Vector3.Transform(worldPosition, inverseParent);
                    }

                    local = Matrix4x4.CreateFromQuaternion(localRotation) *
                        Matrix4x4.CreateScale(targetNode.Scale);
                    local.Translation = localPosition;

                    if (layers[i] != null)
                    {
                        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
                        {
                            scale = targetNode.Scale;
                            rotation = localRotation;
                            translation = localPosition;
                        }
                        layers[i].Keys.Add(new PRSKey(KeyType.NodePRS)
                        {
                            Time = time,
                            Position = translation,
                            Rotation = Quaternion.Normalize(rotation),
                            Scale = scale
                        });
                    }
                }

                targetPose[i] = local * parentWorld;
                targetPoseValid[i] = true;
            }
        }

        return animation;
    }

    /// <summary>
    /// Converts matcher clips to full target-model clips for preview/export. This boundary is
    /// deliberately separate from the canonical clip used to build and query the index.
    /// </summary>
    public static IAnimationClip CreateTargetPreviewClip(IAnimationClip clip, Model targetModel)
    {
        if (clip == null)
            throw new ArgumentNullException(nameof(clip));
        if (targetModel == null)
            throw new ArgumentNullException(nameof(targetModel));

        var targetSkeleton = GfdTargetAnimationClip.CreateSkeleton(targetModel);
        return CreateTargetPreviewClip(clip, targetModel, targetSkeleton);
    }

    private static IAnimationClip CreateTargetPreviewClip(
        IAnimationClip clip,
        Model targetModel,
        SkeletonDefinition targetSkeleton)
    {
        if (clip == null)
            throw new ArgumentNullException(nameof(clip));

        return clip switch
        {
            IAnimationClipWrapper wrapper => CreateTargetPreviewClip(wrapper.InnerClip, targetModel, targetSkeleton),
            GfdAnimationClip gfdClip => gfdClip.CreateTargetPreviewClip(targetModel, targetSkeleton),
            StitchedAnimation stitched => new StitchedAnimation(
                CreateTargetPreviewClip(stitched.SourceClip, targetModel, targetSkeleton),
                stitched.TransitionFrame,
                CreateTargetPreviewClip(stitched.CandidateClip, targetModel, targetSkeleton),
                stitched.CandidateStartFrame,
                stitched.BlendSeconds),
            TailAnimation tail => new TailAnimation(
                CreateTargetPreviewClip(tail.CandidateClip, targetModel, targetSkeleton),
                tail.StartFrame),
            _ => clip
        };
    }

    public static Animation BakeFrame(
        IAnimationClip clip,
        Model targetModel,
        uint version,
        int frame,
        CancellationToken cancellationToken = default)
        => BakeRange(clip, targetModel, version, frame, 1, cancellationToken);

    private static Animation BakeFullSkeletonRange(
        IAnimationClip clip,
        Model targetModel,
        uint version,
        int firstFrame,
        int frameCount,
        CancellationToken cancellationToken)
    {
        var targetNodes = targetModel.Nodes.ToArray();
        if (targetNodes.Length == 0)
            throw new InvalidOperationException("The selected model has no nodes.");

        var nodeIndex = new Dictionary<Node, int>(targetNodes.Length);
        for (var i = 0; i < targetNodes.Length; i++)
            nodeIndex[targetNodes[i]] = i;

        var boneForNode = new int[targetNodes.Length];
        for (var i = 0; i < targetNodes.Length; i++)
            boneForNode[i] = clip.Skeleton.TryGetBone(targetNodes[i].Name, out var bone) ? bone : -1;

        var animation = new Animation(version)
        {
            Duration = Math.Max(0f, (frameCount - 1) / clip.FramesPerSecond)
        };
        var layers = new AnimationLayer[targetNodes.Length];
        for (var i = 0; i < targetNodes.Length; i++)
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
                TargetName = targetNodes[i].Name
            };
            controller.Layers.Add(layer);
            animation.Controllers.Add(controller);
            layers[i] = layer;
        }

        var pose = new BoneTransform[clip.Skeleton.BoneCount];
        var globals = new Matrix4x4[targetNodes.Length];
        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clip.SampleGlobalPose(firstFrame + frame, pose);
            var time = frame / clip.FramesPerSecond;

            for (var i = 0; i < targetNodes.Length; i++)
            {
                var bone = boneForNode[i];
                if (bone >= 0)
                {
                    var transform = pose[bone];
                    var global = Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                        Matrix4x4.CreateScale(transform.Scale);
                    global.Translation = transform.Position;
                    globals[i] = global;
                }
                else
                {
                    globals[i] = targetNodes[i].WorldTransform;
                }
            }

            for (var i = 0; i < targetNodes.Length; i++)
            {
                var layer = layers[i];
                if (layer == null)
                    continue;

                var local = globals[i];
                if (targetNodes[i].Parent != null &&
                    nodeIndex.TryGetValue(targetNodes[i].Parent, out var parentIndex) &&
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

    private static Dictionary<Node, int> CreateTargetCanonicalIndex(
        IReadOnlyList<Node> targetNodes,
        IReadOnlyList<Node> canonicalNodes)
    {
        var result = new Dictionary<Node, int>();
        for (var canonicalIndex = 0; canonicalIndex < canonicalNodes.Count; canonicalIndex++)
        {
            var node = canonicalNodes[canonicalIndex];
            if (!result.TryGetValue(node, out var existing) ||
                canonicalIndex == (int)CanonicalJoint.Head)
            {
                result[node] = canonicalIndex;
            }
        }

        // Keep the validation close to the map creation so an accidental model/node mismatch is
        // reported before any partial animation is returned.
        foreach (var node in result.Keys)
            if (!targetNodes.Contains(node))
                throw new InvalidOperationException("Target canonical node is not part of the target model.");
        return result;
    }

    private static Matrix4x4 GetParentWorld(
        Node node,
        IReadOnlyList<Node> nodes,
        IReadOnlyList<Matrix4x4> evaluated,
        IReadOnlyList<bool> evaluatedFlags,
        IReadOnlyDictionary<Node, Matrix4x4> bind,
        out int parentIndex)
    {
        parentIndex = -1;
        if (node.Parent == null)
            return Matrix4x4.Identity;

        for (var i = 0; i < nodes.Count; i++)
        {
            if (!ReferenceEquals(nodes[i], node.Parent))
                continue;
            parentIndex = i;
            return evaluatedFlags[i] ? evaluated[i] : bind[node.Parent];
        }

        return bind.TryGetValue(node.Parent, out var parentBind) ? parentBind : Matrix4x4.Identity;
    }

    private static Matrix4x4 RotationOnly(Matrix4x4 matrix)
    {
        return Matrix4x4.Decompose(matrix, out _, out var rotation, out _)
            ? ToRotationMatrix(rotation)
            : Matrix4x4.Identity;
    }

    private static Matrix4x4 InverseRotation(Quaternion rotation)
    {
        return Matrix4x4.Invert(ToRotationMatrix(rotation), out var inverse)
            ? inverse
            : Matrix4x4.Identity;
    }

    private static Matrix4x4 ToRotationMatrix(Quaternion rotation)
        => Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));

    private static BoneTransform ToBoneTransform(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            translation = Vector3.Zero;
        }
        if (rotation.LengthSquared() < 1e-10f)
            rotation = Quaternion.Identity;
        return new BoneTransform(translation, Quaternion.Normalize(rotation), scale);
    }

    private static float CalculateReferenceHeight(IReadOnlyList<Node> nodes)
    {
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        foreach (var node in nodes)
        {
            var y = node.WorldTransform.Translation.Y;
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }
        return MathF.Max(0.01f, maxY - minY);
    }
}
