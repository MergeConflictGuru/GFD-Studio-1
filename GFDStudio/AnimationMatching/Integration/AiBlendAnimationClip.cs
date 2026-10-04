using System;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

public sealed class AiBlendAnimationClip : IAnimationClip
{
    private readonly IAnimationClip _clip;
    private readonly Lazy<AnimationPack> _pieces;
    public AiBlendAnimationClip(string id, string name, Model model, Animation joined, AnimationPack pieces)
    {
        _clip = new GfdTargetAnimationClip(id, name, model, joined, 30f);
        _pieces = new Lazy<AnimationPack>(() => pieces);
    }
    public AiBlendAnimationClip(IAnimationClip clip, Func<AnimationPack> createPieces)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _pieces = new Lazy<AnimationPack>(createPieces ?? throw new ArgumentNullException(nameof(createPieces)));
    }
    public AnimationPack Pieces => _pieces.Value;
    public string Id => _clip.Id;
    public string DisplayName => _clip.DisplayName;
    public SkeletonDefinition Skeleton => _clip.Skeleton;
    public int FrameCount => _clip.FrameCount;
    public float FramesPerSecond => _clip.FramesPerSecond;
    public void SampleGlobalPose(int frame, Span<BoneTransform> destination) => _clip.SampleGlobalPose(frame, destination);
}
