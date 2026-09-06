using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// Adapts a GFD animation + target model to the matcher without involving the OpenGL renderer.
/// Each corpus clip can lazy-load and retarget its GAP animation on a worker thread.
/// Large decoded animations are releasable so indexing does not retain the entire corpus in RAM.
/// </summary>
public sealed class GfdAnimationClip : IAnimationClip, IAnimationClipResourceOwner
{
    private readonly Model _model;
    private readonly Node[] _nodes;
    private readonly Func<Animation> _animationLoader;
    private readonly object _animationSync = new();
    private Animation _animation;
    private int _frameCount;

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
        _animationLoader = animationLoader ?? throw new ArgumentNullException(nameof(animationLoader));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public SkeletonDefinition Skeleton { get; }
    public float FramesPerSecond { get; }

    public Animation Animation
    {
        get
        {
            lock (_animationSync)
            {
                _animation ??= _animationLoader() ??
                    throw new InvalidOperationException($"Could not load animation {DisplayName}.");
                return _animation;
            }
        }
    }

    public int FrameCount
    {
        get
        {
            var cached = Volatile.Read(ref _frameCount);
            if (cached > 0)
                return cached;

            var animation = Animation;
            var calculated = Math.Max(1, (int)MathF.Ceiling(animation.Duration * FramesPerSecond) + 1);
            Interlocked.CompareExchange(ref _frameCount, calculated, 0);
            return Volatile.Read(ref _frameCount);
        }
    }

    /// <summary>
    /// Drops the decoded/retargeted animation while retaining its inexpensive duration metadata.
    /// A later preview/export can transparently load it again.
    /// </summary>
    public void ReleaseResources()
    {
        lock (_animationSync)
            _animation = null;
    }

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        if (destination.Length < Skeleton.BoneCount)
            throw new ArgumentException("Destination pose buffer is too small.", nameof(destination));

        // Hold a local reference so ReleaseResources cannot affect an in-flight sample.
        var animation = Animation;
        var frameCount = Volatile.Read(ref _frameCount);
        if (frameCount <= 0)
        {
            frameCount = Math.Max(1, (int)MathF.Ceiling(animation.Duration * FramesPerSecond) + 1);
            Interlocked.CompareExchange(ref _frameCount, frameCount, 0);
            frameCount = Volatile.Read(ref _frameCount);
        }

        var clamped = Math.Clamp(frameIndex, 0, frameCount - 1);
        var time = clamped / FramesPerSecond;
        var transforms = AnimationPoseEvaluator.Evaluate(_model, animation, time);

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
