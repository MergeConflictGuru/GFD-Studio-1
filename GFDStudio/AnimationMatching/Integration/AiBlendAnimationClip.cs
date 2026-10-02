using System;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

public sealed class AiBlendAnimationClip : IAnimationClip
{
    private readonly GfdTargetAnimationClip _clip;
    public AiBlendAnimationClip(string id, string name, Model model, Animation joined, AnimationPack pieces)
    {
        _clip = new GfdTargetAnimationClip(id, name, model, joined, 30f);
        Pieces = pieces;
    }
    public AnimationPack Pieces { get; }
    public string Id => _clip.Id;
    public string DisplayName => _clip.DisplayName;
    public SkeletonDefinition Skeleton => _clip.Skeleton;
    public int FrameCount => _clip.FrameCount;
    public float FramesPerSecond => _clip.FramesPerSecond;
    public void SampleGlobalPose(int frame, Span<BoneTransform> destination) => _clip.SampleGlobalPose(frame, destination);
}
