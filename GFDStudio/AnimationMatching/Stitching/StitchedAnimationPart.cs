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

    public StitchedAnimationPart(
        IAnimationClip clip,
        int startFrame,
        int frameCount,
        Quaternion rotation,
        Vector3 translation,
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
    }
}
