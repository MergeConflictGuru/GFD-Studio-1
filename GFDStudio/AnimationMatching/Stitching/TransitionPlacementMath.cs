using System;
using System.Numerics;

namespace GFDStudio.AnimationMatching.Stitching;

// Placement in model units. Ground contact is shared with the PS4 matcher.
internal static class TransitionPlacementMath
{
    private static readonly int[] BodyContactPoints =
        {0,1,3,5,6,9,10,11,12,16,17,18,19,20,22,23,25,28,29,35,37,39,42};
    public static float LowestBodyPoint(Vector3[] points)
    {
        if(points.Length!=43) throw new ArgumentException("Body contact needs the 43-point rig.");
        float lowest=float.PositiveInfinity;
        foreach(int index in BodyContactPoints) lowest=MathF.Min(lowest,points[index].Y);
        return lowest;
    }

    public static Vector3 TravelVelocity(Vector3[] samples, float step)
    {
        if (samples.Length < 3 || !float.IsFinite(step) || step <= 0)
            throw new ArgumentException("Travel projection needs at least three samples and valid times.");
        int half = samples.Length / 2;
        Vector3 early = Vector3.Zero, late = Vector3.Zero;
        float distance = 0;
        for (int i = 0; i < half; ++i)
        {
            early += samples[i] / half;
            late += samples[samples.Length - half + i] / half;
        }
        for (int i = 1; i < samples.Length; ++i)
        {
            var d = samples[i] - samples[i - 1];
            distance += MathF.Sqrt(d.X*d.X + d.Z*d.Z);
        }
        var net = samples[^1] - samples[0];
        float confidence = distance > 1e-6f ? MathF.Sqrt(net.X*net.X + net.Z*net.Z) / distance : 0;
        float sustained = confidence < .5f ? 0 : confidence;
        var velocity = (late - early) / ((samples.Length - half) * step) * sustained;
        return new Vector3(velocity.X, 0, velocity.Z);
    }

    public static Vector3 ProjectTravel(Vector3[] samples, float step, float duration, Vector3 destinationVelocity)
    {
        if (!float.IsFinite(duration) || duration < 0)
            throw new ArgumentException("Blend duration must be finite and nonnegative.");
        var sourceVelocity = TravelVelocity(samples, step);
        // Reach B's speed within half a second. A longer gap adds time at B's
        // speed, rather than stretching a running-to-rest stop across the gap.
        const float brakingTime = .5f;
        float braking = MathF.Min(duration, brakingTime);
        float momentum = braking - braking * braking / (2 * brakingTime);
        destinationVelocity.Y = 0;
        var travel = destinationVelocity * duration + (sourceVelocity - destinationVelocity) * momentum;
        return samples[^1] + travel;
    }

    public static Vector3 BlendTrajectory(Vector3[] sourceHistory, float step, float duration,
        Vector3 candidateTravel, float persistence)
    {
        if(!float.IsFinite(duration) || duration<0 || !float.IsFinite(persistence) || persistence<0 || persistence>1)
            throw new ArgumentOutOfRangeException(nameof(persistence));
        candidateTravel.Y=0;
        var sourceTravel=TravelVelocity(sourceHistory,step)*duration;
        return sourceHistory[^1]+Vector3.Lerp(candidateTravel,sourceTravel,persistence);
    }

    public static float GroundHeightOffset(float sourceLow, float beforeLow, float candidateLow,
        float floor, float height, float step, bool enabled)
    {
        bool grounded = MathF.Abs(sourceLow-floor) <= height*.015f &&
            (sourceLow-beforeLow)/step <= height*.1f;
        return enabled && grounded ? floor-candidateLow : 0;
    }
}
