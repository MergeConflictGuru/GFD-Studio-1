using System;
using System.Buffers;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Stitching;

/// <summary>
/// Runtime stitched clip for preview/export. When alignment is enabled, candidate root motion is
/// rigidly translated and yaw-aligned so its first emitted frame lands on the source continuation.
/// Optional crossfade blends global transforms, then the candidate owns the rest of the clip.
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

        AlignPositionAndYaw = alignPositionAndYaw;
        if (alignPositionAndYaw)
        {
            (_matchYawAlignment, _matchTranslationAlignment) = CalculateAlignment(
                source,
                _sourceFrame,
                candidate,
                _candidateFrame);
            // The stitched output does not emit the matched candidate frame. Its first candidate
            // sample is the following frame, so align that actual handoff sample to the source's
            // corresponding continuation frame. This prevents a one-frame root jump at the cut.
            var sourceHandoffFrame = Math.Min(_sourceFrame + 1, source.FrameCount - 1);
            var candidateHandoffFrame = Math.Min(_candidateFrame + 1, candidate.FrameCount - 1);
            (_yawAlignment, _translationAlignment) = CalculateAlignment(
                source,
                sourceHandoffFrame,
                candidate,
                candidateHandoffFrame);
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
    /// frame and receives match-point alignment. The preview uses a handoff alignment for the
    /// first emitted candidate continuation frame so the visible cut stays continuous.
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
                sourceName),
            new StitchedAnimationPart(
                _candidate,
                _candidateFrame,
                _candidate.FrameCount - _candidateFrame,
                _matchYawAlignment,
                _matchTranslationAlignment,
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
        var yaw = Quaternion.Normalize(
            PoseFeatureExtractor.ExtractYaw(sourceRoot.Rotation) *
            Quaternion.Inverse(PoseFeatureExtractor.ExtractYaw(candidateRoot.Rotation)));
        var translation = sourceRoot.Position - Vector3.Transform(candidateRoot.Position, yaw);
        return (yaw, translation);
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
