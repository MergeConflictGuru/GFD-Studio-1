using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GFDStudio.AnimationMatching.Core;

public sealed class AnimationMatchOptions
{
    /// <summary>Frames between corpus index samples. 1 gives every frame; 2 is a useful 60 fps default.</summary>
    public int IndexStride { get; set; } = 1;

    /// <summary>Frames between source pivots when a range is selected.</summary>
    public int QueryStride { get; set; } = 1;

    /// <summary>How many approximate neighbors are reranked with the full descriptor.</summary>
    public int ApproximateNeighborCount { get; set; } = 128;

    public int ResultCount { get; set; } = 32;

    /// <summary>Suppress near-identical neighboring frames from the same clip in the final list.</summary>
    public float ResultSuppressionSeconds { get; set; } = 0.20f;
    public int ProjectionDimensions { get; set; } = 24;
    public int ProjectionSeed { get; set; } = 0x47FD51;

    /// <summary>History offsets in seconds before the transition frame.</summary>
    public float[] HistorySeconds { get; set; } = new[] { -0.20f, -0.10f, 0f };

    /// <summary>Finite difference interval for linear and angular velocities.</summary>
    public float VelocityDeltaSeconds { get; set; } = 1f / 30f;

    /// <summary>Minimum amount of animation that must remain after a candidate match frame.</summary>
    public float MinimumContinuationSeconds { get; set; } = 0.20f;

    /// <summary>Weights for descriptor channels.</summary>
    public float PositionWeight { get; set; } = 1.0f;
    public float VelocityWeight { get; set; } = 0.65f;
    public float OrientationWeight { get; set; } = 0.35f;
    public float RootSpeedWeight { get; set; } = 0.55f;
    public float RootYawRateWeight { get; set; } = 0.35f;

    /// <summary>
    /// Optional names for important bones. If empty or unmatched, all non-root bones are sampled.
    /// Matching by name keeps descriptors compact on humanoids.
    /// </summary>
    public string[] PreferredBones { get; set; } = new[]
    {
        "pelvis", "hips", "spine", "chest", "head",
        "l_hand", "r_hand", "left_hand", "right_hand",
        "l_foot", "r_foot", "left_foot", "right_foot"
    };

    public int MaxFeatureBones { get; set; } = 18;

    /// <summary>Ignore trivial self matches around the selected source frame.</summary>
    public float SelfMatchExclusionSeconds { get; set; } = 0.35f;

    /// <summary>Blend duration used by stitched preview/export when enabled.</summary>
    public float BlendSeconds { get; set; } = 0.12f;

    /// <summary>0 = pure best match. Positive values mildly prefer later frames within a highlighted source range.</summary>
    public float LaterSourceFrameBias { get; set; } = 0.015f;


    public string GetIndexFingerprint()
    {
        static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        return string.Join("|", new[]
        {
            IndexStride.ToString(CultureInfo.InvariantCulture),
            ProjectionDimensions.ToString(CultureInfo.InvariantCulture),
            ProjectionSeed.ToString(CultureInfo.InvariantCulture),
            F(VelocityDeltaSeconds), F(MinimumContinuationSeconds),
            F(PositionWeight), F(VelocityWeight), F(OrientationWeight), F(RootSpeedWeight), F(RootYawRateWeight),
            MaxFeatureBones.ToString(CultureInfo.InvariantCulture),
            string.Join(",", HistorySeconds.Select(F)),
            string.Join(",", PreferredBones.Select(x => x.ToLowerInvariant()))
        });
    }

    public void Validate()
    {
        if (IndexStride < 1) throw new ArgumentOutOfRangeException(nameof(IndexStride));
        if (QueryStride < 1) throw new ArgumentOutOfRangeException(nameof(QueryStride));
        if (ApproximateNeighborCount < 1) throw new ArgumentOutOfRangeException(nameof(ApproximateNeighborCount));
        if (ResultCount < 1) throw new ArgumentOutOfRangeException(nameof(ResultCount));
        if (ProjectionDimensions < 2) throw new ArgumentOutOfRangeException(nameof(ProjectionDimensions));
        if (HistorySeconds.Length == 0) throw new ArgumentException("At least one history sample is required.");
        if (VelocityDeltaSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(VelocityDeltaSeconds));
    }
}
