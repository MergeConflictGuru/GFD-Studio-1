using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using GFDLibrary.Animations;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Stitching;

/// <summary>
/// Runtime stitched clip for preview/export. When alignment is enabled, candidate root motion is
/// rigidly translated and yaw-aligned so its first emitted frame lands on the source continuation.
/// Semantic/file roots above the motion root remain in the target model's axis space; unlabelled
/// intermediate axis helpers are anchored to the source handoff so they cannot reintroduce a
/// world-space offset at the cut.
/// Optional crossfade blends global transforms, then the candidate owns the rest of the clip. A
/// short motion-subtree continuity guard also prevents a hard aligned cut from visibly popping.
/// </summary>
public sealed class StitchedAnimation : IAnimationClip
{
    private readonly IAnimationClip _source;
    private readonly IAnimationClip _candidate;
    private readonly int _sourceFrame;
    private readonly int _candidateFrame;
    private readonly int _blendFrames;
    private readonly Quaternion _yawAlignment;
    private readonly Vector3 _translationAlignment;
    private readonly Quaternion _matchYawAlignment;
    private readonly Vector3 _matchTranslationAlignment;
    private readonly bool[] _motionSubtree;
    private readonly bool[] _axisAncestors;
    private readonly BoneTransform[] _axisAnchors;
    private readonly Quaternion[] _axisRotations;
    private readonly Vector3[] _axisTranslations;
    private const int ContinuityGuardFrames = 3;

    public StitchedAnimation(
        IAnimationClip source,
        int sourceFrame,
        IAnimationClip candidate,
        int candidateFrame,
        float blendSeconds,
        bool alignPositionAndYaw = true)
    {
        if (Math.Abs(source.FramesPerSecond - candidate.FramesPerSecond) > 0.01f)
            throw new ArgumentException("StitchedAnimation expects clips at the same frame rate. Resample in the host adapter first.");
        if (source.Skeleton.BoneCount != candidate.Skeleton.BoneCount)
            throw new ArgumentException("Stitched clips must use the same retargeted skeleton.");

        _source = source;
        _candidate = candidate;
        _sourceFrame = Math.Clamp(sourceFrame, 0, source.FrameCount - 1);
        _candidateFrame = Math.Clamp(candidateFrame, 0, candidate.FrameCount - 1);
        _blendFrames = Math.Max(0, (int)MathF.Round(blendSeconds * source.FramesPerSecond));
        _motionSubtree = StitchAlignment.BuildMotionSubtree(Skeleton);
        _axisAncestors = StitchAlignment.BuildAxisAncestors(Skeleton);
        _axisAnchors = new BoneTransform[Skeleton.BoneCount];
        _axisRotations = new Quaternion[Skeleton.BoneCount];
        _axisTranslations = new Vector3[Skeleton.BoneCount];
        StitchAlignment.InitializeAxisCorrections(
            _axisAncestors,
            _axisAnchors,
            _axisRotations,
            _axisTranslations);

        AlignPositionAndYaw = alignPositionAndYaw;
        if (alignPositionAndYaw)
        {
            (_matchYawAlignment, _matchTranslationAlignment) = CalculateAlignment(
                source,
                _sourceFrame,
                candidate,
                _candidateFrame);
            // The stitched output does not emit the matched candidate frame. Its first candidate
            // sample is the following frame, so align that actual handoff sample to the last
            // source frame that is actually emitted. A source continuation frame is not part of
            // this clip when the cut is inside the source animation; using it would introduce a
            // one-frame displacement at the visible boundary.
            var sourceHandoffFrame = _sourceFrame;
            var candidateHandoffFrame = Math.Min(_candidateFrame + 1, candidate.FrameCount - 1);
            (_yawAlignment, _translationAlignment) = CalculateAlignment(
                source,
                sourceHandoffFrame,
                candidate,
                candidateHandoffFrame);

            var sourceHandoffPose = new BoneTransform[Skeleton.BoneCount];
            var candidateHandoffPose = new BoneTransform[Skeleton.BoneCount];
            source.SampleGlobalPose(sourceHandoffFrame, sourceHandoffPose);
            candidate.SampleGlobalPose(candidateHandoffFrame, candidateHandoffPose);
            StitchAlignment.InitializeAxisCorrections(
                _axisAncestors,
                sourceHandoffPose,
                candidateHandoffPose,
                _axisAnchors,
                _axisRotations,
                _axisTranslations);
        }
        else
        {
            _yawAlignment = Quaternion.Identity;
            _translationAlignment = Vector3.Zero;
            _matchYawAlignment = Quaternion.Identity;
            _matchTranslationAlignment = Vector3.Zero;
        }
    }

    public string Id => $"stitch:{_source.Id}:{_sourceFrame}:{_candidate.Id}:{_candidateFrame}";
    public string DisplayName => $"{_source.DisplayName} → {_candidate.DisplayName}";
    public SkeletonDefinition Skeleton => _source.Skeleton;
    public float FramesPerSecond => _source.FramesPerSecond;
    public int TransitionFrame => _sourceFrame;
    public int CandidateStartFrame => _candidateFrame;
    public int BlendFrames => _blendFrames;
    public float BlendSeconds => _blendFrames / FramesPerSecond;
    public bool AlignPositionAndYaw { get; }
    public int FrameCount => _sourceFrame + 1 + Math.Max(0, _candidate.FrameCount - _candidateFrame - 1);
    public IAnimationClip SourceClip => _source;
    public IAnimationClip CandidateClip => _candidate;

    /// <summary>
    /// Creates the source prefix and aligned candidate suffix used by an external blend tool.
    /// The source ends at the selected transition frame; the candidate begins at its matched
    /// frame and receives match-point alignment. Intermediate axis helpers are anchored to the
    /// source transition in the exported candidate too, so separate files can be blended without
    /// reproducing the preview seam. The preview uses a handoff alignment for the first emitted
    /// candidate continuation frame so the visible cut stays continuous.
    /// </summary>
    public (IAnimationClip Source, IAnimationClip Candidate) CreateExportParts()
    {
        var sourceName = $"{_source.DisplayName} (source through f{_sourceFrame})";
        var candidateName = $"{_candidate.DisplayName} (candidate from f{_candidateFrame})";
        return (
            new StitchedAnimationPart(
                _source,
                0,
                _sourceFrame + 1,
                Quaternion.Identity,
                Vector3.Zero,
                null,
                0,
                sourceName),
            new StitchedAnimationPart(
                _candidate,
                _candidateFrame,
                _candidate.FrameCount - _candidateFrame,
                _matchYawAlignment,
                _matchTranslationAlignment,
                AlignPositionAndYaw ? _source : null,
                _sourceFrame,
                candidateName));
    }

    private static (Quaternion yaw, Vector3 translation) CalculateAlignment(
        IAnimationClip source,
        int sourceFrame,
        IAnimationClip candidate,
        int candidateFrame)
    {
        var sourcePose = new BoneTransform[source.Skeleton.BoneCount];
        var candidatePose = new BoneTransform[candidate.Skeleton.BoneCount];
        source.SampleGlobalPose(sourceFrame, sourcePose);
        candidate.SampleGlobalPose(candidateFrame, candidatePose);
        var sourceRoot = sourcePose[source.Skeleton.RootBoneIndex];
        var candidateRoot = candidatePose[candidate.Skeleton.RootBoneIndex];
        var yaw = CalculateYawAlignment(sourceRoot, candidateRoot);
        var translation = sourceRoot.Position - Vector3.Transform(candidateRoot.Position, yaw);
        return (yaw, translation);
    }

    private static Quaternion CalculateYawAlignment(
        BoneTransform sourceRoot,
        BoneTransform candidateRoot)
    {
        // Align the character's actual facing, not its travel direction. Strafes, backpedals,
        // pivots, and turn-in-place clips intentionally allow those two directions to differ.
        // Wrapping the delta selects the shortest yaw and avoids a sign flip at +/- PI during
        // the optional crossfade.
        var sourceYaw = PoseFeatureExtractor.YawRadians(sourceRoot.Rotation);
        var candidateYaw = PoseFeatureExtractor.YawRadians(candidateRoot.Rotation);
        var delta = PoseFeatureExtractor.WrapAngle(sourceYaw - candidateYaw);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, delta);
    }

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        if (destination.Length < Skeleton.BoneCount) throw new ArgumentException("Pose destination is too small.");
        frameIndex = Math.Clamp(frameIndex, 0, FrameCount - 1);
        if (frameIndex <= _sourceFrame)
        {
            _source.SampleGlobalPose(frameIndex, destination);
            return;
        }

        var pool = ArrayPool<BoneTransform>.Shared;
        var candidateBuffer = pool.Rent(Skeleton.BoneCount);
        try
        {
            var candidatePose = candidateBuffer.AsSpan(0, Skeleton.BoneCount);
            var candidateIndex = Math.Min(_candidate.FrameCount - 1, _candidateFrame + (frameIndex - _sourceFrame));
            _candidate.SampleGlobalPose(candidateIndex, candidatePose);
            AlignCandidate(candidatePose);

            var blendOffset = frameIndex - _sourceFrame;
            if (_blendFrames > 0 && blendOffset <= _blendFrames)
            {
                var sourceBuffer = pool.Rent(Skeleton.BoneCount);
                try
                {
                    var sourcePose = sourceBuffer.AsSpan(0, Skeleton.BoneCount);
                    var sourceIndex = Math.Min(_source.FrameCount - 1, frameIndex);
                    _source.SampleGlobalPose(sourceIndex, sourcePose);
                    var t = SmoothStep(blendOffset / (float)_blendFrames);
                    for (var i = 0; i < Skeleton.BoneCount; i++)
                        destination[i] = BoneTransform.Lerp(sourcePose[i], candidatePose[i], t);
                }
                finally { pool.Return(sourceBuffer, clearArray: false); }
            }
            else if (AlignPositionAndYaw && _blendFrames == 0 && blendOffset <= ContinuityGuardFrames)
            {
                // A hard cut can still visibly pop when the matcher finds a close, but not
                // identical, pose. Keep the matched root exactly continuous and ease the animated
                // motion subtree into the candidate over a few frames. Static ancestors remain
                // in the candidate's axis space, preserving the rig's file-level hierarchy.
                var sourceBuffer = pool.Rent(Skeleton.BoneCount);
                try
                {
                    var sourcePose = sourceBuffer.AsSpan(0, Skeleton.BoneCount);
                    _source.SampleGlobalPose(_sourceFrame, sourcePose);
                    var t = SmoothStep(blendOffset / (float)ContinuityGuardFrames);
                    candidatePose.CopyTo(destination);
                    for (var i = 0; i < Skeleton.BoneCount; i++)
                    {
                        if (!_motionSubtree[i] || i == Skeleton.RootBoneIndex)
                            continue;
                        destination[i] = BoneTransform.Lerp(sourcePose[i], candidatePose[i], t);
                    }
                }
                finally { pool.Return(sourceBuffer, clearArray: false); }
            }
            else
            {
                candidatePose.CopyTo(destination);
            }
        }
        finally { pool.Return(candidateBuffer, clearArray: false); }
    }

    private void AlignCandidate(Span<BoneTransform> pose)
    {
        StitchAlignment.ApplyRigidTransform(pose, _motionSubtree, _yawAlignment, _translationAlignment);
        if (AlignPositionAndYaw)
            StitchAlignment.ApplyAxisCorrections(
                pose,
                _motionSubtree,
                _axisAncestors,
                Skeleton.Parents,
                _axisAnchors,
                _axisRotations,
                _axisTranslations);
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

internal static class StitchAlignment
{
    public static bool[] BuildMotionSubtree(SkeletonDefinition skeleton)
    {
        var result = new bool[skeleton.BoneCount];
        for (var i = 0; i < result.Length; i++)
        {
            var current = i;
            var visited = 0;
            while ((uint)current < (uint)result.Length && visited++ <= result.Length)
            {
                if (current == skeleton.RootBoneIndex)
                {
                    result[i] = true;
                    break;
                }

                current = skeleton.Parents[current];
            }
        }

        return result;
    }

    public static bool[] BuildAxisAncestors(SkeletonDefinition skeleton)
    {
        // The motion root may sit below an unlabelled animated axis helper (for example
        // "rot"). Only those unlabelled ancestors are normalized; semantic roots and body bones
        // remain owned by the source/candidate clips normally.
        var result = new bool[skeleton.BoneCount];
        var ancestor = skeleton.Parents[skeleton.RootBoneIndex];
        var visited = 0;
        while ((uint)ancestor < (uint)result.Length && visited++ <= result.Length)
        {
            var role = AnimationSkeletonRoles.GetRole(skeleton.BoneNames[ancestor]);
            if (!string.IsNullOrWhiteSpace(role))
                break;

            result[ancestor] = true;
            ancestor = skeleton.Parents[ancestor];
        }

        return result;
    }

    public static void InitializeAxisCorrections(
        ReadOnlySpan<bool> axisAncestors,
        Span<BoneTransform> anchors,
        Span<Quaternion> rotations,
        Span<Vector3> translations)
    {
        if (anchors.Length < axisAncestors.Length ||
            rotations.Length < axisAncestors.Length ||
            translations.Length < axisAncestors.Length)
            throw new ArgumentException("Axis correction buffers are too small.");

        for (var i = 0; i < axisAncestors.Length; i++)
        {
            anchors[i] = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
            rotations[i] = Quaternion.Identity;
            translations[i] = Vector3.Zero;
        }
    }

    public static void InitializeAxisCorrections(
        ReadOnlySpan<bool> axisAncestors,
        ReadOnlySpan<BoneTransform> sourcePose,
        ReadOnlySpan<BoneTransform> candidatePose,
        Span<BoneTransform> anchors,
        Span<Quaternion> rotations,
        Span<Vector3> translations)
    {
        if (sourcePose.Length < axisAncestors.Length || candidatePose.Length < axisAncestors.Length)
            throw new ArgumentException("Axis correction poses are too small.");
        InitializeAxisCorrections(axisAncestors, anchors, rotations, translations);

        for (var i = 0; i < axisAncestors.Length; i++)
        {
            if (!axisAncestors[i])
                continue;

            var source = sourcePose[i];
            var candidate = candidatePose[i];
            var rawRotation = source.Rotation * Quaternion.Inverse(candidate.Rotation);
            var rotation = rawRotation.LengthSquared() >= 1e-10f
                ? Quaternion.Normalize(rawRotation)
                : Quaternion.Identity;
            anchors[i] = source;
            rotations[i] = rotation;
            translations[i] = source.Position - Vector3.Transform(candidate.Position, rotation);
        }
    }

    public static void ApplyAxisCorrections(
        Span<BoneTransform> pose,
        ReadOnlySpan<bool> motionSubtree,
        ReadOnlySpan<bool> axisAncestors,
        IReadOnlyList<int> parents,
        ReadOnlySpan<BoneTransform> anchors,
        ReadOnlySpan<Quaternion> rotations,
        ReadOnlySpan<Vector3> translations)
    {
        if (pose.Length > motionSubtree.Length || pose.Length > axisAncestors.Length || pose.Length > parents.Count ||
            pose.Length > anchors.Length || pose.Length > rotations.Length || pose.Length > translations.Length)
            throw new ArgumentException("Axis correction buffers are too small.");

        for (var i = 0; i < pose.Length; i++)
        {
            if (motionSubtree[i])
                continue;

            var axis = FindNearestAxisAncestor(i, axisAncestors, parents, pose.Length);
            if (axis < 0)
                continue;

            if (i == axis)
            {
                pose[i] = anchors[axis];
                continue;
            }

            var position = Vector3.Transform(pose[i].Position, rotations[axis]) + translations[axis];
            var rotation = Quaternion.Normalize(rotations[axis] * pose[i].Rotation);
            pose[i] = new BoneTransform(position, rotation, pose[i].Scale);
        }
    }

    private static int FindNearestAxisAncestor(
        int bone,
        ReadOnlySpan<bool> axisAncestors,
        IReadOnlyList<int> parents,
        int boneCount)
    {
        var current = bone;
        var visited = 0;
        while ((uint)current < (uint)boneCount && visited++ <= boneCount)
        {
            if (axisAncestors[current])
                return current;
            current = parents[current];
        }

        return -1;
    }

    public static void ApplyRigidTransform(
        Span<BoneTransform> pose,
        ReadOnlySpan<bool> motionSubtree,
        Quaternion rotation,
        Vector3 translation)
    {
        if (motionSubtree.Length < pose.Length)
            throw new ArgumentException("Motion subtree mask is too small.", nameof(motionSubtree));

        for (var i = 0; i < pose.Length; i++)
        {
            if (!motionSubtree[i])
                continue;

            var position = Vector3.Transform(pose[i].Position, rotation) + translation;
            var boneRotation = Quaternion.Normalize(rotation * pose[i].Rotation);
            pose[i] = new BoneTransform(position, boneRotation, pose[i].Scale);
        }
    }
}
