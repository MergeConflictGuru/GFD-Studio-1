using System;
using System.Collections.Generic;
using System.Numerics;

namespace GFDStudio.AnimationMatching.Core;

public readonly struct BoneTransform
{
    public BoneTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = Quaternion.Normalize(rotation);
        Scale = scale;
    }

    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Scale { get; }

    public static BoneTransform Lerp(in BoneTransform a, in BoneTransform b, float t)
        => new(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t), Vector3.Lerp(a.Scale, b.Scale, t));
}

public sealed class SkeletonDefinition
{
    private readonly Dictionary<string, int> _boneLookup;

    public SkeletonDefinition(IReadOnlyList<string> boneNames, IReadOnlyList<int> parents, int rootBoneIndex, float referenceHeight = 1f)
    {
        if (boneNames.Count == 0 || boneNames.Count != parents.Count)
            throw new ArgumentException("Bone names and parent arrays must be non-empty and have equal length.");
        if ((uint)rootBoneIndex >= (uint)boneNames.Count)
            throw new ArgumentOutOfRangeException(nameof(rootBoneIndex));

        BoneNames = boneNames;
        Parents = parents;
        RootBoneIndex = rootBoneIndex;
        ReferenceHeight = MathF.Max(referenceHeight, 1e-4f);
        _boneLookup = new Dictionary<string, int>(boneNames.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < boneNames.Count; i++)
            _boneLookup[boneNames[i]] = i;
    }

    public IReadOnlyList<string> BoneNames { get; }
    public IReadOnlyList<int> Parents { get; }
    public int RootBoneIndex { get; }
    public int BoneCount => BoneNames.Count;
    public float ReferenceHeight { get; }

    public bool TryGetBone(string name, out int index) => _boneLookup.TryGetValue(name, out index);
}

/// <summary>
/// Adapter boundary between the matcher and GFD Studio's GAP/model data.
/// SampleGlobalPose must return model/global-space transforms in skeleton order.
/// </summary>
public interface IAnimationClip
{
    string Id { get; }
    string DisplayName { get; }
    SkeletonDefinition Skeleton { get; }
    int FrameCount { get; }
    float FramesPerSecond { get; }
    void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination);
}

/// <summary>
/// Optional lifecycle hook for clips backed by large decoded resources. Index construction calls
/// this after metadata inspection and after each clip's sampling batch so adapters can discard
/// decoded GAP/retargeted animation data while keeping cheap metadata resident.
/// </summary>
public interface IAnimationClipResourceOwner
{
    void ReleaseResources();
}

public sealed class AnimationCorpus
{
    public AnimationCorpus(IReadOnlyList<IAnimationClip> clips) => Clips = clips;
    public IReadOnlyList<IAnimationClip> Clips { get; }
}

public readonly record struct FrameAddress(int ClipIndex, int FrameIndex);

public sealed record AnimationMatchResult(
    IAnimationClip Candidate,
    int CandidateFrame,
    int SourceFrame,
    float Distance,
    float Score,
    float PoseDistance,
    float VelocityDistance,
    float OrientationDistance)
{
    public float CandidateTimeSeconds => CandidateFrame / Candidate.FramesPerSecond;
}
