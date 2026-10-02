using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.GUI.Forms;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// Adapts a GFD animation and its source model to the canonical AniMatch skeleton. The source
/// model is deliberately independent from the model currently shown in the Character Browser.
/// </summary>
public sealed class GfdAnimationClip : IAnimationClip, IAnimationClipResourceOwner
{
    private readonly Func<Model> _modelLoader;
    private readonly Func<Model> _previewModelLoader;
    private readonly Func<Animation> _animationLoader;
    private readonly object _modelSync = new();
    private readonly object _animationSync = new();
    private readonly object _poseCacheSync = new();
    private readonly Dictionary<int, BoneTransform[]> _poseCache = new();
    private Model _model;
    private Node[] _canonicalNodes;
    private SkeletonDefinition _skeleton;
    private Quaternion _characterBindRotation;
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
        : this(id, displayName, sourceModelLoader, animationLoader, sourceModelLoader, framesPerSecond)
    {
    }

    public GfdAnimationClip(
        string id,
        string displayName,
        Func<Model> sourceModelLoader,
        Func<Animation> animationLoader,
        Func<Model> previewModelLoader,
        float framesPerSecond = 30f)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? string.Empty;
        _modelLoader = sourceModelLoader ?? throw new ArgumentNullException(nameof(sourceModelLoader));
        _previewModelLoader = previewModelLoader ?? _modelLoader;
        _animationLoader = animationLoader ?? throw new ArgumentNullException(nameof(animationLoader));
        FramesPerSecond = MathF.Max(1f, framesPerSecond);
    }

    public string SourcePackPath { get; set; }
    public int SourceClipIndex { get; set; }
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
                if (_animation == null)
                {
                    _animation = _animationLoader() ??
                        throw new InvalidOperationException($"Could not load animation {DisplayName}.");

                    // Evaluating a raw GAP uses TargetName, but fixing IDs here also makes the same
                    // source clip safe to pass through the preview/export baker later.
                    var context = EnsureModelContext();
                    _animation.FixTargetIds(context.model);
                    _poseSampler = new AnimationPoseSampler(context.model, _animation, context.canonicalNodes);
                }
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

            var calculated = GfdAnimationFrameCount.Calculate(Animation, FramesPerSecond, DisplayName);
            Interlocked.CompareExchange(ref _frameCount, calculated, 0);
            return Volatile.Read(ref _frameCount);
        }
    }

    /// <summary>
    /// Creates the full-skeleton clip used by the final preview. Matching remains canonical and
    /// model-independent, but preview must retain every node in the original animation, including
    /// clavicles, upper arms, fingers, and cosmetic branches.
    /// </summary>
    public IAnimationClip CreateTargetPreviewClip(Model targetModel)
        => CreateTargetPreviewClip(targetModel, GfdTargetAnimationClip.CreateSkeleton(targetModel));

    internal IAnimationClip CreateTargetPreviewClip(Model targetModel, SkeletonDefinition targetSkeleton)
    {
        if (targetModel == null)
            throw new ArgumentNullException(nameof(targetModel));
        if (targetSkeleton == null)
            throw new ArgumentNullException(nameof(targetSkeleton));

        var sourceModel = _previewModelLoader() ??
            throw new InvalidOperationException($"Could not load preview source model for {DisplayName}.");
        var animation = _animationLoader() ??
            throw new InvalidOperationException($"Could not load animation {DisplayName} for preview.");

        if (ReferenceEquals(sourceModel, targetModel))
            animation.FixTargetIds(targetModel);
        else
            animation.Retarget(sourceModel, targetModel, false,
                MainForm.settings.UseLocalBindSpaceRetargeting);

        ComposeCostumeAccessoryTracks(animation, targetModel);
        return new GfdTargetAnimationClip(Id, DisplayName, targetModel, animation, FramesPerSecond, targetSkeleton);
    }

    private void ComposeCostumeAccessoryTracks(Animation animation, Model model)
    {
        if (string.IsNullOrWhiteSpace(SourcePackPath)) return;
        var directory = Path.GetDirectoryName(SourcePackPath);
        var stem = Path.GetFileNameWithoutExtension(SourcePackPath);
        if (!Directory.Exists(directory)) return;
        var missing = model.Nodes.Where(n =>
            string.IsNullOrWhiteSpace(AnimationSkeletonRoles.GetRole(n.Name)))
            .Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0) return;
        // A corpus clip may have been resolved with a different costume's source
        // model. Its canonical matching pose is still usable, but the displayed
        // costume needs its own accessory curves. Costume curves also replace
        // bind-pose accessory tracks left by a body bake. Semantic body roots
        // and limb tracks remain owned by the body animation.
        var candidates = Directory.EnumerateFiles(directory, stem + "_*.GAP")
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileNameWithoutExtension(file), "^" +
                System.Text.RegularExpressions.Regex.Escape(stem) + @"_\d+$"))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidates)
        {
            var pack = GFDLibrary.Resource.Load<AnimationPack>(file);
            if ((uint)SourceClipIndex >= (uint)pack.Animations.Count) continue;
            var component = pack.Animations[SourceClipIndex];
            if (Math.Abs(component.Duration - animation.Duration) > 1f / FramesPerSecond) continue;
            foreach (var controller in component.Controllers)
                if (controller.TargetKind == TargetKind.Node && missing.Remove(controller.TargetName))
                {
                    animation.Controllers.RemoveAll(c => c.TargetKind == TargetKind.Node && c.TargetName == controller.TargetName);
                    animation.Controllers.Add(controller);
                }
            if (missing.Count == 0) break;
        }
        animation.FixTargetIds(model);
    }

    /// <summary>Drops decoded GAP data while retaining the source skeleton and duration.</summary>
    public void ReleaseResources()
    {
        lock (_poseCacheSync)
        {
            _poseCache.Clear();
            lock (_animationSync)
            {
                _poseSampler = null;
                _animation = null;
            }
            _canonicalNodes = null;
            _model = null;
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
            frameCount = GfdAnimationFrameCount.Calculate(animation, FramesPerSecond, DisplayName);
            Interlocked.CompareExchange(ref _frameCount, frameCount, 0);
            frameCount = Volatile.Read(ref _frameCount);
        }

        var clamped = Math.Clamp(frameIndex, 0, frameCount - 1);
        lock (_poseCacheSync)
        {
            if (!ReferenceEquals(poseSampler, Volatile.Read(ref _poseSampler)))
                throw new InvalidOperationException($"Animation resources were released while sampling {DisplayName}.");

            if (_poseCache.TryGetValue(clamped, out var cached))
            {
                cached.AsSpan().CopyTo(destination);
                return;
            }

            var time = clamped / FramesPerSecond;
            Span<Matrix4x4> transforms = stackalloc Matrix4x4[CanonicalSkeleton.JointCount];
            poseSampler.Evaluate(transforms, time);
            var sampled = new BoneTransform[CanonicalSkeleton.JointCount];
            var bindRoot = context.skeleton.BindPose[(int)CanonicalJoint.Root];
            var inverseCharacterRotation = Quaternion.Inverse(_characterBindRotation);
            for (var i = 0; i < context.canonicalNodes.Length; i++)
            {
                var matrix = transforms[i];
                if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
                {
                    scale = Vector3.One;
                    rotation = Quaternion.Identity;
                    translation = Vector3.Zero;
                }

                if (rotation.LengthSquared() < 1e-10f)
                    rotation = Quaternion.Identity;

                // A motion-root bone's local axes are not the character's right/up/forward:
                // P5R Bip01 and P5D root use different sideways bases. Use the anatomical bind
                // frame for positions, and remove each joint's own bind axes from orientation.
                // Keep the world-space rotation delta so animated yaw, lean and joint motion
                // remain comparable even when the same body is authored with different axes.
                var canonicalPosition = Vector3.Transform(
                    translation - bindRoot.Position,
                    inverseCharacterRotation);
                var canonicalRotation = Quaternion.Normalize(
                    inverseCharacterRotation * rotation *
                    Quaternion.Inverse(context.skeleton.BindPose[i].Rotation) * _characterBindRotation);
                sampled[i] = new BoneTransform(canonicalPosition, canonicalRotation, scale);
            }

            _poseCache.Add(clamped, sampled);
            sampled.AsSpan().CopyTo(destination);
        }
    }

    public static SkeletonDefinition CreateSkeleton(Model model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        var nodes = ResolveCanonicalNodes(model);
        var bindPose = nodes.Select(GetWorldTransform).ToArray();
        return CanonicalSkeleton.Create(bindPose, CalculateReferenceHeight(bindPose));
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
        canonical[(int)CanonicalJoint.Root] = AnimationSkeletonRoles.ResolveMotionRoot(model);
        canonical[(int)CanonicalJoint.Pelvis] = Pick("hips");
        canonical[(int)CanonicalJoint.LowerSpine] = Pick("spine", "spine1", "spine2");
        canonical[(int)CanonicalJoint.UpperSpine] = Pick("spine2", "spine1", "spine");
        canonical[(int)CanonicalJoint.Neck] = Pick("neck");
        canonical[(int)CanonicalJoint.Head] = Pick("head");
        // The canonical shoulder slot represents the upper arm. Clavicles are retained only by
        // the full target-model preview adapter; indexing the clavicle here leaves the actual arm
        // chain out of the matcher and produces incorrect arm motion when poses are compared.
        canonical[(int)CanonicalJoint.LeftShoulder] = Pick("leftarm", "leftshoulder");
        canonical[(int)CanonicalJoint.LeftElbow] = Pick("leftforearm");
        canonical[(int)CanonicalJoint.LeftHand] = Pick("lefthand");
        canonical[(int)CanonicalJoint.RightShoulder] = Pick("rightarm", "rightshoulder");
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
        if (_skeleton != null && _model != null && _canonicalNodes != null)
            return (_model, _canonicalNodes, _skeleton);

        lock (_modelSync)
        {
            if (_skeleton != null && _model != null && _canonicalNodes != null)
                return (_model, _canonicalNodes, _skeleton);

            _model = _modelLoader() ?? throw new InvalidOperationException($"Could not load source model for {DisplayName}.");
            if (_model.Nodes == null || !_model.Nodes.Any())
                throw new InvalidOperationException($"Source model for {DisplayName} has no nodes.");

            _canonicalNodes = ResolveCanonicalNodes(_model);
            if (_skeleton == null)
            {
                var bindPose = _canonicalNodes.Select(GetWorldTransform).ToArray();
                _characterBindRotation = CalculateCharacterBindRotation(bindPose);
                _skeleton = CanonicalSkeleton.Create(bindPose, CalculateReferenceHeight(bindPose));
            }
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

    private static Quaternion CalculateCharacterBindRotation(IReadOnlyList<BoneTransform> bindPose)
    {
        Vector3 Position(CanonicalJoint joint) => bindPose[(int)joint].Position;
        var feet = (Position(CanonicalJoint.LeftFoot) + Position(CanonicalJoint.RightFoot)) * .5f;
        var up = Position(CanonicalJoint.Head) - feet;
        if (up.LengthSquared() < 1e-10f)
            throw new InvalidOperationException("Source skeleton has no anatomical height.");
        up = Vector3.Normalize(up);
        var right = Position(CanonicalJoint.LeftHip) - Position(CanonicalJoint.RightHip);
        right -= up * Vector3.Dot(right, up);
        if (right.LengthSquared() < 1e-10f)
        {
            right = Position(CanonicalJoint.LeftShoulder) - Position(CanonicalJoint.RightShoulder);
            right -= up * Vector3.Dot(right, up);
        }
        if (right.LengthSquared() < 1e-10f)
            throw new InvalidOperationException("Source skeleton has no anatomical width.");
        right = Vector3.Normalize(right);
        var forward = Vector3.Normalize(Vector3.Cross(right, up));
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1)));
    }

    private static float CalculateReferenceHeight(IReadOnlyList<BoneTransform> bindPose)
    {
        // Cosmetic/helper branches and the file's choice of world axes must not affect scale.
        var up = Vector3.Transform(Vector3.UnitY, CalculateCharacterBindRotation(bindPose));
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        for (var i = 1; i < bindPose.Count; i++)
        {
            var height = Vector3.Dot(bindPose[i].Position, up);
            minimum = MathF.Min(minimum, height);
            maximum = MathF.Max(maximum, height);
        }
        return MathF.Max(0.01f, maximum - minimum);
    }
}

internal static class GfdAnimationFrameCount
{
    public static int Calculate(Animation animation, float framesPerSecond, string displayName)
    {
        if (animation == null)
            throw new ArgumentNullException(nameof(animation));
        if (!float.IsFinite(framesPerSecond) || framesPerSecond <= 0f)
            throw new InvalidDataException($"Animation '{displayName}' has an invalid frame rate ({framesPerSecond}).");
        if (!float.IsFinite(animation.Duration) || animation.Duration < 0f)
            throw new InvalidDataException($"Animation '{displayName}' has an invalid duration ({animation.Duration}).");

        var frameCountWithoutFinalSample = MathF.Ceiling(animation.Duration * framesPerSecond);
        if (!float.IsFinite(frameCountWithoutFinalSample) || frameCountWithoutFinalSample >= int.MaxValue)
            throw new InvalidDataException(
                $"Animation '{displayName}' is too long to index ({animation.Duration:0.###} seconds at {framesPerSecond:0.###} FPS).");

        return Math.Max(1, checked((int)frameCountWithoutFinalSample + 1));
    }
}
