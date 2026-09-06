using System;
using System.Buffers;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Stitching;

/// <summary>
/// Runtime stitched clip for preview/export. Candidate root motion is rigidly aligned in the
/// horizontal plane so its match frame lands at the source transition transform. Optional
/// crossfade blends global transforms, then the aligned candidate owns the rest of the clip.
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

    public StitchedAnimation(IAnimationClip source, int sourceFrame, IAnimationClip candidate, int candidateFrame, float blendSeconds)
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

        var sourcePose = new BoneTransform[Skeleton.BoneCount];
        var candidatePose = new BoneTransform[Skeleton.BoneCount];
        source.SampleGlobalPose(_sourceFrame, sourcePose);
        candidate.SampleGlobalPose(_candidateFrame, candidatePose);
        var sourceRoot = sourcePose[Skeleton.RootBoneIndex];
        var candidateRoot = candidatePose[Skeleton.RootBoneIndex];
        _yawAlignment = Quaternion.Normalize(PoseFeatureExtractor.ExtractYaw(sourceRoot.Rotation) * Quaternion.Inverse(PoseFeatureExtractor.ExtractYaw(candidateRoot.Rotation)));
        _translationAlignment = sourceRoot.Position - Vector3.Transform(candidateRoot.Position, _yawAlignment);
    }

    public string Id => $"stitch:{_source.Id}:{_sourceFrame}:{_candidate.Id}:{_candidateFrame}";
    public string DisplayName => $"{_source.DisplayName} → {_candidate.DisplayName}";
    public SkeletonDefinition Skeleton => _source.Skeleton;
    public float FramesPerSecond => _source.FramesPerSecond;
    public int TransitionFrame => _sourceFrame;
    public int CandidateStartFrame => _candidateFrame;
    public int BlendFrames => _blendFrames;
    public int FrameCount => _sourceFrame + 1 + Math.Max(0, _candidate.FrameCount - _candidateFrame - 1);

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
            else
            {
                candidatePose.CopyTo(destination);
            }
        }
        finally { pool.Return(candidateBuffer, clearArray: false); }
    }

    private void AlignCandidate(Span<BoneTransform> pose)
    {
        for (var i = 0; i < pose.Length; i++)
        {
            var p = Vector3.Transform(pose[i].Position, _yawAlignment) + _translationAlignment;
            var r = Quaternion.Normalize(_yawAlignment * pose[i].Rotation);
            pose[i] = new BoneTransform(p, r, pose[i].Scale);
        }
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
