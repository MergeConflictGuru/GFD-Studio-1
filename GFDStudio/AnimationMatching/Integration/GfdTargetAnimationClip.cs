using System;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// A complete target-model animation used only at the preview/export boundary. Unlike
/// <see cref="GfdAnimationClip"/>, this clip intentionally exposes every target node rather than
/// the reduced canonical matcher skeleton.
/// </summary>
public sealed class GfdTargetAnimationClip : IAnimationClip
{
    private readonly Model _model;
    private readonly Animation _animation;
    private readonly Node[] _nodes;
    private readonly SkeletonDefinition _skeleton;

    public GfdTargetAnimationClip(
        string id,
        string displayName,
        Model model,
        Animation animation,
        float framesPerSecond)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? string.Empty;
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _animation = animation ?? throw new ArgumentNullException(nameof(animation));
        _nodes = model.Nodes.ToArray();
        if (_nodes.Length == 0)
            throw new InvalidOperationException("The target model has no nodes.");

        _skeleton = CreateSkeleton(model, _nodes);
        FramesPerSecond = MathF.Max(1f, framesPerSecond);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public SkeletonDefinition Skeleton => _skeleton;
    public float FramesPerSecond { get; }
    public int FrameCount => Math.Max(1, (int)MathF.Ceiling(_animation.Duration * FramesPerSecond) + 1);

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        if (destination.Length < _nodes.Length)
            throw new ArgumentException("Destination pose buffer is too small.", nameof(destination));

        var clamped = Math.Clamp(frameIndex, 0, FrameCount - 1);
        var time = clamped / FramesPerSecond;
        var transforms = AnimationPoseEvaluator.Evaluate(_model, _animation, time);
        for (var i = 0; i < _nodes.Length; i++)
        {
            var matrix = transforms[_nodes[i]];
            if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
            {
                scale = Vector3.One;
                rotation = Quaternion.Identity;
                translation = Vector3.Zero;
            }
            if (rotation.LengthSquared() < 1e-10f)
                rotation = Quaternion.Identity;
            destination[i] = new BoneTransform(translation, Quaternion.Normalize(rotation), scale);
        }
    }

    private static SkeletonDefinition CreateSkeleton(Model model, Node[] nodes)
    {
        var indices = nodes
            .Select((node, index) => (node, index))
            .ToDictionary(item => item.node, item => item.index);
        var parents = nodes.Select(node =>
            node.Parent != null && indices.TryGetValue(node.Parent, out var parentIndex)
                ? parentIndex
                : -1).ToArray();
        var rootIndex = model.RootNode != null && indices.TryGetValue(model.RootNode, out var modelRootIndex)
            ? modelRootIndex
            : Array.FindIndex(parents, parent => parent < 0);
        if (rootIndex < 0)
            rootIndex = 0;

        var bindPose = nodes.Select(node => ToBoneTransform(node.WorldTransform)).ToArray();
        return new SkeletonDefinition(
            nodes.Select(node => node.Name).ToArray(),
            parents,
            rootIndex,
            CalculateReferenceHeight(nodes),
            bindPose);
    }

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

    private static float CalculateReferenceHeight(Node[] nodes)
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
