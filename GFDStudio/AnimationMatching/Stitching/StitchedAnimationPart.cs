using System;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Stitching;

/// <summary>
/// A clipped view of one side of a <see cref="StitchedAnimation"/>. The candidate side can carry
/// the same rigid alignment used by the stitched preview while retaining the original frame rate
/// and skeleton, so it can be exported for an external blend operation.
/// </summary>
public sealed class StitchedAnimationPart : IAnimationClip
{
    private readonly IAnimationClip _clip;
    private readonly int _startFrame;
    private readonly int _frameCount;
    private readonly Quaternion _rotation;
    private readonly Vector3 _translation;
    private readonly bool[] _motionSubtree;
    private readonly bool[] _axisAncestors;
    private readonly BoneTransform[] _axisAnchors;
    private readonly Quaternion[] _axisRotations;
    private readonly Vector3[] _axisTranslations;
    private readonly bool _normalizeAxisAncestors;

    public StitchedAnimationPart(
        IAnimationClip clip,
        int startFrame,
        int frameCount,
        Quaternion rotation,
        Vector3 translation,
        string displayName)
        : this(clip, startFrame, frameCount, rotation, translation, null, 0, displayName)
    {
    }

    public StitchedAnimationPart(
        IAnimationClip clip,
        int startFrame,
        int frameCount,
        Quaternion rotation,
        Vector3 translation,
        IAnimationClip axisReferenceClip,
        int axisReferenceFrame,
        string displayName)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        if (clip.FrameCount <= 0)
            throw new ArgumentException("The animation clip has no frames.", nameof(clip));

        _startFrame = Math.Clamp(startFrame, 0, clip.FrameCount - 1);
        _frameCount = Math.Clamp(frameCount, 1, clip.FrameCount - _startFrame);
        _rotation = Quaternion.Normalize(rotation);
        _translation = translation;
        _motionSubtree = StitchAlignment.BuildMotionSubtree(Skeleton);
        _axisAncestors = StitchAlignment.BuildAxisAncestors(Skeleton);
        _axisAnchors = new BoneTransform[Skeleton.BoneCount];
        _axisRotations = new Quaternion[Skeleton.BoneCount];
        _axisTranslations = new Vector3[Skeleton.BoneCount];
        _normalizeAxisAncestors = axisReferenceClip != null && Array.IndexOf(_axisAncestors, true) >= 0;
        StitchAlignment.InitializeAxisCorrections(
            _axisAncestors,
            _axisAnchors,
            _axisRotations,
            _axisTranslations);
        if (_normalizeAxisAncestors)
        {
            if (axisReferenceClip.Skeleton.BoneCount != Skeleton.BoneCount)
                throw new ArgumentException("Axis reference clip must use the same skeleton.", nameof(axisReferenceClip));

            var referencePose = new BoneTransform[Skeleton.BoneCount];
            var candidatePose = new BoneTransform[Skeleton.BoneCount];
            axisReferenceClip.SampleGlobalPose(axisReferenceFrame, referencePose);
            _clip.SampleGlobalPose(_startFrame, candidatePose);
            StitchAlignment.InitializeAxisCorrections(
                _axisAncestors,
                referencePose,
                candidatePose,
                _axisAnchors,
                _axisRotations,
                _axisTranslations);
        }
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("A display name is required.", nameof(displayName))
            : displayName;
    }

    public string Id => $"{_clip.Id}:part:{_startFrame}:{_frameCount}";
    public string DisplayName { get; }
    public SkeletonDefinition Skeleton => _clip.Skeleton;
    public int FrameCount => _frameCount;
    public float FramesPerSecond => _clip.FramesPerSecond;
    public int StartFrame => _startFrame;

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        var localFrame = Math.Clamp(frameIndex, 0, _frameCount - 1);
        _clip.SampleGlobalPose(_startFrame + localFrame, destination);

        StitchAlignment.ApplyRigidTransform(destination[..Skeleton.BoneCount], _motionSubtree, _rotation, _translation);
        if (_normalizeAxisAncestors)
            StitchAlignment.ApplyAxisCorrections(
                destination[..Skeleton.BoneCount],
                _motionSubtree,
                _axisAncestors,
                Skeleton.Parents,
                _axisAnchors,
                _axisRotations,
                _axisTranslations);
    }
}
