using System;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Stitching;

/// <summary>
/// A view of a candidate animation beginning at its matched frame. It keeps the candidate's
/// already-retargeted global poses while presenting the matched suffix as a new zero-based clip.
/// </summary>
public sealed class TailAnimation : IAnimationClip
{
    private readonly IAnimationClip _candidate;
    private readonly int _startFrame;

    public TailAnimation(IAnimationClip candidate, int startFrame)
    {
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        if (candidate.FrameCount <= 0)
            throw new ArgumentException("The candidate animation has no frames.", nameof(candidate));

        _startFrame = Math.Clamp(startFrame, 0, candidate.FrameCount - 1);
    }

    public string Id => $"{_candidate.Id}:tail:{_startFrame}";
    public string DisplayName => $"{_candidate.DisplayName} (tail f{_startFrame})";
    public SkeletonDefinition Skeleton => _candidate.Skeleton;
    public int FrameCount => _candidate.FrameCount - _startFrame;
    public float FramesPerSecond => _candidate.FramesPerSecond;
    public IAnimationClip CandidateClip => _candidate;
    public int StartFrame => _startFrame;

    public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
    {
        var localFrame = Math.Clamp(frameIndex, 0, FrameCount - 1);
        _candidate.SampleGlobalPose(_startFrame + localFrame, destination);
    }
}
