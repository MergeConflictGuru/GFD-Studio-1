using System;
using System.Threading;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;
using GFDStudio.AnimationMatching.Index;

namespace GFDStudio.AnimationMatching.Search;

/// <summary>Matches two selected frame sets without consulting the library index.</summary>
public static class AnimationPairMatcher
{
    public static AnimationMatchResult Match(IAnimationClip first, IAnimationClip second,
        (int start, int end)? firstRange = null, (int start, int end)? secondRange = null,
        CancellationToken cancellationToken = default)
    {
        if (first == null || second == null) throw new ArgumentNullException();
        var a = ResolveRange(first, firstRange, first.FrameCount - 1);
        var b = ResolveRange(second, secondRange, 0);
        var options = new AnimationMatchOptions();
        var extractor = new PoseFeatureExtractor(options);
        var firstBones = extractor.SelectFeatureBones(first.Skeleton);
        var secondBones = extractor.SelectFeatureBones(second.Skeleton);
        if (firstBones.Length == 0 || firstBones.Length != secondBones.Length)
            throw new InvalidOperationException("The two clips need compatible matchable skeletons.");
        int dimensions = extractor.GetDescriptorLength(firstBones.Length);
        int firstCount = a.end - a.start + 1, secondCount = b.end - b.start + 1;
        int count = checked(firstCount + secondCount);
        var packed = new float[checked(count * dimensions)];
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i < firstCount)
                extractor.Extract(first, a.start + i, firstBones, packed.AsSpan(i * dimensions, dimensions));
            else
                extractor.Extract(second, b.start + i - firstCount, secondBones, packed.AsSpan(i * dimensions, dimensions));
        }
        var mean = new float[dimensions];
        var invStd = new float[dimensions];
        AnimationSearchDatabase.ComputeNormalization(packed, count, dimensions, mean, invStd);
        AnimationSearchDatabase.NormalizeInPlace(packed, count, dimensions, mean, invStd,
            extractor.GetPostNormalizationWeights(firstBones.Length));
        var candidatePoints = packed.AsSpan(firstCount * dimensions).ToArray();
        // Full descriptors, rather than a projection: every eligible frame participates,
        // and the best pair is exact even when both ranges include the same clip.
        var tree = new VpTree(candidatePoints, dimensions);
        AnimationMatchResult best = null;
        for (int i = 0; i < firstCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = packed.AsSpan(i * dimensions, dimensions);
            int candidate = tree.FindNearest(query, 1)[0];
            var distance = AnimationMatcher.ExactDistance(query, candidatePoints.AsSpan(candidate * dimensions, dimensions));
            float bias = firstRange.HasValue && firstCount > 1
                ? options.LaterSourceFrameBias * (1f - i / (float)Math.Max(1, firstCount - 1)) : 0;
            float total = distance.total + bias;
            var result = new AnimationMatchResult(second, b.start + candidate, a.start + i,
                total, AnimationMatcher.DistanceToScore(total), distance.pose, distance.velocity, distance.orientation);
            if (best == null || result.Distance < best.Distance ||
                (result.Distance == best.Distance && result.SourceFrame > best.SourceFrame)) best = result;
        }
        return best;
    }

    private static (int start, int end) ResolveRange(IAnimationClip clip, (int start, int end)? range, int defaultFrame)
    {
        if (clip.FrameCount < 1) throw new InvalidOperationException("The selected animation has no frames.");
        int start = range?.start ?? defaultFrame, end = range?.end ?? defaultFrame;
        if (start > end) (start, end) = (end, start);
        return (Math.Clamp(start, 0, clip.FrameCount - 1), Math.Clamp(end, 0, clip.FrameCount - 1));
    }
}
