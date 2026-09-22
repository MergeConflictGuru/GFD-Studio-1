using System;
using GFDLibrary;
using GFDLibrary.Animations;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// Target-model data for a live OpenGL candidate thumbnail. The animation is a target-model
/// range baked for display; the pixels are rendered continuously by the thumbnail control.
/// </summary>
public sealed class AnimationThumbnailScene
{
    public AnimationThumbnailScene(ModelPack modelPack, Animation animation, double seamTimeSeconds)
    {
        ModelPack = modelPack ?? throw new ArgumentNullException(nameof(modelPack));
        Animation = animation ?? throw new ArgumentNullException(nameof(animation));
        SeamTimeSeconds = Math.Max(0.0, seamTimeSeconds);
    }

    public ModelPack ModelPack { get; }
    public Animation Animation { get; }
    public double SeamTimeSeconds { get; }
}
