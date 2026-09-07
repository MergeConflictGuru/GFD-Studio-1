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
/// Adapts a GFD animation and its source model to the canonical AniMatch skeleton. The source
/// model is deliberately independent from the model currently shown in the Character Browser.
/// </summary>
public sealed class GfdAnimationClip : IAnimationClip, IAnimationClipResourceOwner
{
    private readonly Func<Model> _modelLoader;
    private readonly Func<Animation> _animationLoader;
    private readonly object _modelSync = new();
    private readonly object _animationSync = new();
    private Model _model;
    private Node[] _canonicalNodes;
    private SkeletonDefinition _skeleton;
    private Animation _animation;
    private AnimationPoseSampler _poseSampler;
    private int _frameCount;

    public GfdAnimationClip(
        string id,
        string displayName,
        Model sourceModel,
        Func<Animation> animationLoader,
        float framesPerSecond = 30f)
        : this(id, displayName, () => sourceModel, animationLoader, framesPerSecond)
    {
    }

    public GfdAnimationClip(
        string id,
        string displayName,
        Func<Model> sourceModelLoader,
        Func<Animation> animationLoader,
        float framesPerSecond = 30f)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? string.Empty;
        _modelLoader = sourceModelLoader ?? throw new ArgumentNullException(nameof(sourceModelLoader));
        _animationLoader = animationLoader ?? throw new ArgumentNullException(nameof(animationLoader));
        FramesPerSecond = MathF.Max(1f, framesPerSecond);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Model SourceModel => EnsureModelContext().model;
    public SkeletonDefinition Skeleton => EnsureModelContext().skeleton;
    public float FramesPerSecond { get; }

    public Animation Animation
    {
        get
        {
            lock (_animationSync)
            {
                _animation ??= _animationLoader() ??
                    throw new InvalidOperationException($"Could not load animation {DisplayName}.");

                // Evaluating a raw GAP uses TargetName, but fixing IDs here also makes the same
                // source clip safe to pass through the preview/export baker later.
                _animation.FixTargetIds(SourceModel);
                _poseSampler = new AnimationPoseSampler(SourceModel, _animation);
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

    /// <summary>Drops decoded GAP data while retaining the source skeleton and duration.</summary>
    public void ReleaseResources()
    {
        lock (_animationSync)
        {
            _poseSampler = null;
            _animation = null;
        }
    }

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        var context = EnsureModelContext();
        if (destination.Length < CanonicalSkeleton.JointCount)
            throw new ArgumentException("Destination pose buffer is too small.", nameof(destination));

        var animation = Animation;
        var poseSampler = Volatile.Read(ref _poseSampler);
        if (poseSampler == null)
            throw new InvalidOperationException($"Could not prepare animation sampler for {DisplayName}.");
        var frameCount = Volatile.Read(ref _frameCount);
        if (frameCount <= 0)
        {
            frameCount = Math.Max(1, (int)MathF.Ceiling(animation.Duration * FramesPerSecond) + 1);
            Interlocked.CompareExchange(ref _frameCount, frameCount, 0);
            frameCount = Volatile.Read(ref _frameCount);
        }

        var clamped = Math.Clamp(frameIndex, 0, frameCount - 1);
        var time = clamped / FramesPerSecond;
        var transforms = poseSampler.Evaluate(context.canonicalNodes, time);

        for (var i = 0; i < context.canonicalNodes.Length; i++)
        {
            var node = context.canonicalNodes[i];
            var matrix = transforms.TryGetValue(node, out var evaluated)
                ? evaluated
                : node.WorldTransform;
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

    public static SkeletonDefinition CreateSkeleton(Model model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        var nodes = ResolveCanonicalNodes(model);
        var bindPose = nodes.Select(GetWorldTransform).ToArray();
        return CanonicalSkeleton.Create(bindPose, CalculateReferenceHeight(model));
    }

    /// <summary>Returns source/target nodes in the fixed canonical-joint order.</summary>
    public static Node[] ResolveCanonicalNodes(Model model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        var byRole = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in model.Nodes)
        {
            var role = AnimationSkeletonRoles.GetRole(node.Name);
            if (!string.IsNullOrWhiteSpace(role) && !byRole.ContainsKey(role))
                byRole.Add(role, node);
        }

        Node Pick(params string[] roles)
        {
            foreach (var role in roles)
                if (byRole.TryGetValue(role, out var node))
                    return node;
            return null;
        }

        var canonical = new Node[CanonicalSkeleton.JointCount];
        canonical[(int)CanonicalJoint.Root] = Pick("motionroot", "root", "rootnode");
        canonical[(int)CanonicalJoint.Pelvis] = Pick("hips");
        canonical[(int)CanonicalJoint.LowerSpine] = Pick("spine", "spine1", "spine2");
        canonical[(int)CanonicalJoint.UpperSpine] = Pick("spine2", "spine1", "spine");
        canonical[(int)CanonicalJoint.Neck] = Pick("neck");
        canonical[(int)CanonicalJoint.Head] = Pick("head");
        canonical[(int)CanonicalJoint.LeftShoulder] = Pick("leftshoulder");
        canonical[(int)CanonicalJoint.LeftElbow] = Pick("leftforearm");
        canonical[(int)CanonicalJoint.LeftHand] = Pick("lefthand");
        canonical[(int)CanonicalJoint.RightShoulder] = Pick("rightshoulder");
        canonical[(int)CanonicalJoint.RightElbow] = Pick("rightforearm");
        canonical[(int)CanonicalJoint.RightHand] = Pick("righthand");
        canonical[(int)CanonicalJoint.LeftHip] = Pick("leftupleg");
        canonical[(int)CanonicalJoint.LeftKnee] = Pick("leftleg");
        canonical[(int)CanonicalJoint.LeftFoot] = Pick("leftfoot");
        canonical[(int)CanonicalJoint.RightHip] = Pick("rightupleg");
        canonical[(int)CanonicalJoint.RightKnee] = Pick("rightleg");
        canonical[(int)CanonicalJoint.RightFoot] = Pick("rightfoot");

        // Neck is optional in the audit. Using the head bind/pose keeps the feature schema fixed
        // without inventing a transform for rigs that omit a separate neck node.
        canonical[(int)CanonicalJoint.Neck] ??= canonical[(int)CanonicalJoint.Head];

        var missing = new List<string>();
        for (var i = 0; i < canonical.Length; i++)
        {
            if (canonical[i] == null && (CanonicalJoint)i != CanonicalJoint.Neck)
                missing.Add(CanonicalSkeleton.Names[i]);
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Source model '{model.RootNode?.Name ?? "unknown"}' is not a canonical humanoid: missing {string.Join(", ", missing)}.");

        return canonical;
    }

    private (Model model, Node[] canonicalNodes, SkeletonDefinition skeleton) EnsureModelContext()
    {
        if (_skeleton != null)
            return (_model, _canonicalNodes, _skeleton);

        lock (_modelSync)
        {
            if (_skeleton != null)
                return (_model, _canonicalNodes, _skeleton);

            _model = _modelLoader() ?? throw new InvalidOperationException($"Could not load source model for {DisplayName}.");
            if (_model.Nodes == null || !_model.Nodes.Any())
                throw new InvalidOperationException($"Source model for {DisplayName} has no nodes.");

            _canonicalNodes = ResolveCanonicalNodes(_model);
            var bindPose = _canonicalNodes.Select(GetWorldTransform).ToArray();
            _skeleton = CanonicalSkeleton.Create(bindPose, CalculateReferenceHeight(_model));
            return (_model, _canonicalNodes, _skeleton);
        }
    }

    private static BoneTransform GetWorldTransform(Node node)
    {
        var matrix = node.WorldTransform;
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

    private static float CalculateReferenceHeight(Model model)
    {
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        foreach (var node in model.Nodes)
        {
            var y = node.WorldTransform.Translation.Y;
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }
        return MathF.Max(0.01f, maxY - minY);
    }
}
