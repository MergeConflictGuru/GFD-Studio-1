using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// Adapts a GFD animation + target model to the matcher without involving the OpenGL renderer.
/// Each corpus clip can lazy-load and retarget its GAP animation on a worker thread.
/// </summary>
public sealed class GfdAnimationClip : IAnimationClip
{
    private readonly Model _model;
    private readonly Node[] _nodes;
    private readonly Lazy<Animation> _animation;

    public GfdAnimationClip(
        string id,
        string displayName,
        Model model,
        SkeletonDefinition skeleton,
        Func<Animation> animationLoader,
        float framesPerSecond = 30f)
    {
        Id = id;
        DisplayName = displayName;
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _nodes = model.Nodes.ToArray();
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        FramesPerSecond = MathF.Max(1f, framesPerSecond);
        _animation = new Lazy<Animation>(
            () => animationLoader() ?? throw new InvalidOperationException($"Could not load animation {displayName}."),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public SkeletonDefinition Skeleton { get; }
    public float FramesPerSecond { get; }
    public Animation Animation => _animation.Value;
    public int FrameCount => Math.Max(1, (int)MathF.Ceiling(Animation.Duration * FramesPerSecond) + 1);

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        if (destination.Length < Skeleton.BoneCount)
            throw new ArgumentException("Destination pose buffer is too small.", nameof(destination));

        var clamped = Math.Clamp(frameIndex, 0, FrameCount - 1);
        var time = clamped / FramesPerSecond;
        var transforms = AnimationPoseEvaluator.Evaluate(_model, Animation, time);

        for (var i = 0; i < _nodes.Length && i < destination.Length; i++)
        {
            if (!transforms.TryGetValue(_nodes[i], out var matrix) ||
                !Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
            {
                matrix = _nodes[i].WorldTransform;
                Matrix4x4.Decompose(matrix, out scale, out rotation, out translation);
            }

            if (rotation.LengthSquared() < 1e-10f)
                rotation = Quaternion.Identity;
            destination[i] = new BoneTransform(translation, Quaternion.Normalize(rotation), scale);
        }
    }

    public static SkeletonDefinition CreateSkeleton(Model model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        var nodes = model.Nodes.ToArray();
        if (nodes.Length == 0)
            throw new InvalidOperationException("The selected model has no nodes.");

        var index = new Dictionary<Node, int>(nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
            index[nodes[i]] = i;

        var parents = new int[nodes.Length];
        var root = 0;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        for (var i = 0; i < nodes.Length; i++)
        {
            parents[i] = nodes[i].Parent != null && index.TryGetValue(nodes[i].Parent, out var parent)
                ? parent
                : -1;
            if (parents[i] < 0)
                root = i;

            var y = nodes[i].WorldTransform.Translation.Y;
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }

        var referenceHeight = MathF.Max(0.01f, maxY - minY);
        return new SkeletonDefinition(nodes.Select(node => node.Name).ToArray(), parents, root, referenceHeight);
    }
}
