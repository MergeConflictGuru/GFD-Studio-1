using System;

namespace GFDStudio.AnimationMatching.Core;

/// <summary>
/// Shared timing for the main stitched preview and the live candidate thumbnails.
/// Keeping this in one place prevents the cards from drifting away from the preview
/// seam when the preview window changes.
/// </summary>
public static class AnimationMatchingPreviewTiming
{
    public const float BeforeSeamSeconds = 2.5f;
    public const float AfterSeamSeconds = 5.0f;

    public static double LoopDurationSeconds => BeforeSeamSeconds + AfterSeamSeconds;

    public static (int startFrame, int endFrame) GetLoopFrames(
        int frameCount,
        int seamFrame,
        float framesPerSecond)
    {
        if (frameCount <= 0)
            return (0, 0);

        var fps = MathF.Max(1.0f, framesPerSecond);
        var clampedSeam = Math.Clamp(seamFrame, 0, frameCount - 1);
        var beforeFrames = Math.Max(1, (int)MathF.Ceiling(BeforeSeamSeconds * fps));
        var afterFrames = Math.Max(1, (int)MathF.Ceiling(AfterSeamSeconds * fps));
        var startFrame = Math.Max(0, clampedSeam - beforeFrames + 1);
        var endFrame = Math.Min(frameCount - 1, clampedSeam + afterFrames);
        return (startFrame, endFrame);
    }

    public static double GetSeamTimeSeconds(int startFrame, int seamFrame, float framesPerSecond)
    {
        var fps = MathF.Max(1.0f, framesPerSecond);
        return Math.Max(0, seamFrame - startFrame) / fps;
    }
}
