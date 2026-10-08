using System;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.AnimationMatching.Stitching;

namespace GFDStudio.GUI.Forms;

public partial class MainForm
{
    private static async Task PreloadAiBlendAsync()
    {
        try { await SlideAiBlend.PreloadAsync(); }
        catch (Exception error) { GFDLibrary.Logger.Debug("AI blend preload failed: " + error); }
    }

    Task<IAnimationClip> IGfdAnimationMatchingHost.GenerateAiBlendAsync(
        StitchedAnimation clip, float seconds, string styleHint, CancellationToken token)
    {
        var pack = GetAnimationMatchingTargetModelPack();
        if (pack?.Model == null) throw new InvalidOperationException("Load a model first.");
        return SlideAiBlend.GenerateAsync(clip, pack.Model, pack.Version, seconds, token, styleHint);
    }
}
