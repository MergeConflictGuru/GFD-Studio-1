using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Features;

/// <summary>
/// Extracts translation- and yaw-invariant motion descriptors.
/// Every bone position/orientation is expressed in the transition root's character space.
/// Absolute root translation and root yaw therefore disappear, while local pose, speed,
/// turning rate and motion history remain available to the matcher.
/// </summary>
public sealed class PoseFeatureExtractor
{
    private readonly AnimationMatchOptions _options;

    public PoseFeatureExtractor(AnimationMatchOptions options)
    {
        _options = options;
        _options.Validate();
    }

    public int[] SelectFeatureBones(SkeletonDefinition skeleton)
    {
        var selected = new List<int>();
        foreach (var preferred in _options.PreferredBones)
        {
            for (var i = 0; i < skeleton.BoneCount; i++)
            {
                if (i == skeleton.RootBoneIndex || selected.Contains(i))
                    continue;
                var n = NormalizeBoneName(skeleton.BoneNames[i]);
                var p = NormalizeBoneName(preferred);
                if (n == p || n.EndsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    selected.Add(i);
                    break;
                }
            }
            if (selected.Count >= _options.MaxFeatureBones)
                break;
        }

        if (selected.Count < 4)
        {
            selected.Clear();
            for (var i = 0; i < skeleton.BoneCount && selected.Count < _options.MaxFeatureBones; i++)
                if (i != skeleton.RootBoneIndex)
                    selected.Add(i);
        }

        return selected.ToArray();
    }

    public int GetDescriptorLength(int featureBoneCount)
    {
        // each history sample: pos(3) + vel(3) + orientation-forward(3) + orientation-up(3) per bone
        // plus planar root speed(2) + vertical speed(1) + yaw rate(1)
        return _options.HistorySeconds.Length * featureBoneCount * 12 + 4;
    }

    /// <summary>
    /// Per-dimension weights applied after z-score normalization. Applying them after whitening
    /// is important: doing it only before whitening would mathematically cancel the weights.
    /// </summary>
    public float[] GetPostNormalizationWeights(int featureBoneCount)
    {
        var weights = new float[GetDescriptorLength(featureBoneCount)];
        var cursor = 0;
        for (var h = 0; h < _options.HistorySeconds.Length; h++)
        {
            for (var b = 0; b < featureBoneCount; b++)
            {
                var p = MathF.Sqrt(MathF.Max(0f, _options.PositionWeight));
                var v = MathF.Sqrt(MathF.Max(0f, _options.VelocityWeight));
                var o = MathF.Sqrt(MathF.Max(0f, _options.OrientationWeight));
                for (var i = 0; i < 3; i++) weights[cursor++] = p;
                for (var i = 0; i < 3; i++) weights[cursor++] = v;
                for (var i = 0; i < 6; i++) weights[cursor++] = o;
            }
        }
        var root = MathF.Sqrt(MathF.Max(0f, _options.RootSpeedWeight));
        weights[cursor++] = root; weights[cursor++] = root; weights[cursor++] = root;
        weights[cursor++] = MathF.Sqrt(MathF.Max(0f, _options.RootYawRateWeight));
        return weights;
    }

    public float[] Extract(IAnimationClip clip, int pivotFrame, int[] featureBones)
    {
        var descriptor = new float[GetDescriptorLength(featureBones.Length)];
        Extract(clip, pivotFrame, featureBones, descriptor);
        return descriptor;
    }

    public void Extract(IAnimationClip clip, int pivotFrame, int[] featureBones, Span<float> destination)
    {
        var skeleton = clip.Skeleton;
        if (destination.Length != GetDescriptorLength(featureBones.Length))
            throw new ArgumentException("Descriptor buffer has wrong size.", nameof(destination));

        var boneCount = skeleton.BoneCount;
        var pool = ArrayPool<BoneTransform>.Shared;
        var pivotBuffer = pool.Rent(boneCount);
        var poseBuffer = pool.Rent(boneCount);
        var prevBuffer = pool.Rent(boneCount);
        var nextBuffer = pool.Rent(boneCount);
        try
        {
            var pivotPose = pivotBuffer.AsSpan(0, boneCount);
            var pose = poseBuffer.AsSpan(0, boneCount);
            var prev = prevBuffer.AsSpan(0, boneCount);
            var next = nextBuffer.AsSpan(0, boneCount);

            clip.SampleGlobalPose(ClampFrame(clip, pivotFrame), pivotPose);
            var pivotRoot = pivotPose[skeleton.RootBoneIndex];
            var invPivotYaw = Quaternion.Inverse(ExtractYaw(pivotRoot.Rotation));
            var invHeight = 1f / skeleton.ReferenceHeight;
            var cursor = 0;

            foreach (var historySeconds in _options.HistorySeconds)
            {
                var sampleFrame = ClampFrame(clip, pivotFrame + SecondsToFrames(clip, historySeconds));
                var velocityFrames = Math.Max(1, SecondsToFrames(clip, _options.VelocityDeltaSeconds));
                var prevFrame = ClampFrame(clip, sampleFrame - velocityFrames);
                var nextFrame = ClampFrame(clip, sampleFrame + velocityFrames);

                clip.SampleGlobalPose(sampleFrame, pose);
                clip.SampleGlobalPose(prevFrame, prev);
                clip.SampleGlobalPose(nextFrame, next);
                var dt = Math.Max((nextFrame - prevFrame) / clip.FramesPerSecond, 1f / clip.FramesPerSecond);

                foreach (var boneIndex in featureBones)
                {
                    var position = Vector3.Transform(pose[boneIndex].Position - pivotRoot.Position, invPivotYaw) * invHeight;
                    var vWorld = (next[boneIndex].Position - prev[boneIndex].Position) / dt;
                    var velocity = Vector3.Transform(vWorld, invPivotYaw) * invHeight;
                    var facing = Vector3.Transform(Vector3.UnitZ, pose[boneIndex].Rotation);
                    var up = Vector3.Transform(Vector3.UnitY, pose[boneIndex].Rotation);
                    facing = SafeNormalize(Vector3.Transform(facing, invPivotYaw), Vector3.UnitZ);
                    up = SafeNormalize(Vector3.Transform(up, invPivotYaw), Vector3.UnitY);

                    WriteWeighted(destination, ref cursor, position, MathF.Sqrt(_options.PositionWeight));
                    WriteWeighted(destination, ref cursor, velocity, MathF.Sqrt(_options.VelocityWeight));
                    WriteWeighted(destination, ref cursor, facing, MathF.Sqrt(_options.OrientationWeight));
                    WriteWeighted(destination, ref cursor, up, MathF.Sqrt(_options.OrientationWeight));
                }
            }

            var deltaFrames = Math.Max(1, SecondsToFrames(clip, _options.VelocityDeltaSeconds));
            var aFrame = ClampFrame(clip, pivotFrame - deltaFrames);
            var bFrame = ClampFrame(clip, pivotFrame + deltaFrames);
            clip.SampleGlobalPose(aFrame, prev);
            clip.SampleGlobalPose(bFrame, next);
            var rootA = prev[skeleton.RootBoneIndex];
            var rootB = next[skeleton.RootBoneIndex];
            var rootDt = Math.Max((bFrame - aFrame) / clip.FramesPerSecond, 1f / clip.FramesPerSecond);
            var rootVelocity = Vector3.Transform((rootB.Position - rootA.Position) / rootDt, invPivotYaw) * invHeight;
            var yawA = YawRadians(rootA.Rotation);
            var yawB = YawRadians(rootB.Rotation);
            var yawRate = WrapAngle(yawB - yawA) / rootDt;
            var rootSpeedScale = MathF.Sqrt(_options.RootSpeedWeight);

            destination[cursor++] = rootVelocity.X * rootSpeedScale;
            destination[cursor++] = rootVelocity.Z * rootSpeedScale;
            destination[cursor++] = rootVelocity.Y * rootSpeedScale;
            destination[cursor++] = yawRate * MathF.Sqrt(_options.RootYawRateWeight);
        }
        finally
        {
            pool.Return(pivotBuffer, clearArray: false);
            pool.Return(poseBuffer, clearArray: false);
            pool.Return(prevBuffer, clearArray: false);
            pool.Return(nextBuffer, clearArray: false);
        }
    }

    private static string NormalizeBoneName(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int SecondsToFrames(IAnimationClip clip, float seconds)
        => (int)MathF.Round(seconds * clip.FramesPerSecond);

    private static int ClampFrame(IAnimationClip clip, int frame)
        => Math.Clamp(frame, 0, Math.Max(0, clip.FrameCount - 1));

    private static void WriteWeighted(Span<float> output, ref int cursor, Vector3 value, float weight)
    {
        output[cursor++] = value.X * weight;
        output[cursor++] = value.Y * weight;
        output[cursor++] = value.Z * weight;
    }

    public static Quaternion ExtractYaw(Quaternion rotation)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitY, YawRadians(rotation));

    public static float YawRadians(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        forward.Y = 0f;
        if (forward.LengthSquared() < 1e-10f)
            return 0f;
        forward = Vector3.Normalize(forward);
        return MathF.Atan2(forward.X, forward.Z);
    }

    public static float WrapAngle(float radians)
    {
        while (radians > MathF.PI) radians -= 2f * MathF.PI;
        while (radians < -MathF.PI) radians += 2f * MathF.PI;
        return radians;
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        => value.LengthSquared() < 1e-10f ? fallback : Vector3.Normalize(value);
}
