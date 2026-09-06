using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Index;

namespace GFDStudio.AnimationMatching.Search;

public sealed class AnimationMatcher
{
    private readonly AnimationSearchDatabase _database;

    public AnimationMatcher(AnimationSearchDatabase database) => _database = database;

    /// <summary>
    /// Searches candidate transition frames. If sourceRangeStart/sourceRangeEnd are null,
    /// the source's last frame is used. Otherwise each pivot in the selected range participates
    /// and the globally best source-pivot/candidate-frame pairs win.
    /// </summary>
    public IReadOnlyList<AnimationMatchResult> Search(
        IAnimationClip source,
        int? sourceRangeStart = null,
        int? sourceRangeEnd = null,
        CancellationToken cancellationToken = default)
    {
        var options = _database.Options;
        var start = sourceRangeStart ?? source.FrameCount - 1;
        var end = sourceRangeEnd ?? source.FrameCount - 1;
        if (start > end) (start, end) = (end, start);
        start = Math.Clamp(start, 0, source.FrameCount - 1);
        end = Math.Clamp(end, 0, source.FrameCount - 1);

        var sourceBones = _database.Extractor.SelectFeatureBones(source.Skeleton);
        var requiredBones = _database.ClipFeatureBones[0].Length;
        if (sourceBones.Length < requiredBones)
            throw new InvalidOperationException("Source skeleton has too few matchable feature bones for this search database.");
        if (sourceBones.Length != requiredBones) sourceBones = sourceBones[..requiredBones];

        var bestByAddress = new Dictionary<(int clip, int frame), AnimationMatchResult>();
        var query = new float[_database.DescriptorDimensions];
        var projected = new float[_database.Projection.OutputDimensions];
        var rangeLength = Math.Max(1, end - start);

        for (var sourceFrame = start; sourceFrame <= end; sourceFrame += options.QueryStride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _database.Extractor.Extract(source, sourceFrame, sourceBones, query);
            _database.NormalizeQuery(query);
            _database.Projection.Project(query, projected);
            var neighbors = _database.Tree.FindNearest(projected, options.ApproximateNeighborCount);

            foreach (var sampleIndex in neighbors)
            {
                var address = _database.Addresses[sampleIndex];
                var candidate = _database.Corpus.Clips[address.ClipIndex];
                if (ShouldExcludeSelf(source, sourceFrame, candidate, address.FrameIndex, options)) continue;

                var exact = ExactDistance(query, _database.GetDescriptor(sampleIndex));
                var normalizedRangePosition = (sourceFrame - start) / (float)rangeLength;
                var sourceBias = options.LaterSourceFrameBias * (1f - normalizedRangePosition);
                var totalDistance = exact.total + sourceBias;
                var score = DistanceToScore(totalDistance);
                var result = new AnimationMatchResult(candidate, address.FrameIndex, sourceFrame, totalDistance, score, exact.pose, exact.velocity, exact.orientation);
                var key = (address.ClipIndex, address.FrameIndex);
                if (!bestByAddress.TryGetValue(key, out var existing) || result.Distance < existing.Distance)
                    bestByAddress[key] = result;
            }
        }

        var sorted = bestByAddress.Values
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase);

        // Temporal non-maximum suppression keeps a good clip from occupying the entire list
        // with frames 100,101,102... while still allowing genuinely different points in it.
        var output = new List<AnimationMatchResult>(options.ResultCount);
        foreach (var candidate in sorted)
        {
            var radius = Math.Max(0, (int)MathF.Round(options.ResultSuppressionSeconds * candidate.Candidate.FramesPerSecond));
            var duplicate = output.Any(existing =>
                string.Equals(existing.Candidate.Id, candidate.Candidate.Id, StringComparison.Ordinal) &&
                Math.Abs(existing.CandidateFrame - candidate.CandidateFrame) <= radius);
            if (duplicate) continue;
            output.Add(candidate);
            if (output.Count >= options.ResultCount) break;
        }
        return output;
    }

    private static bool ShouldExcludeSelf(IAnimationClip source, int sourceFrame, IAnimationClip candidate, int candidateFrame, AnimationMatchOptions options)
    {
        if (!string.Equals(source.Id, candidate.Id, StringComparison.Ordinal)) return false;
        var exclusion = Math.Max(1, (int)MathF.Round(options.SelfMatchExclusionSeconds * source.FramesPerSecond));
        return Math.Abs(sourceFrame - candidateFrame) <= exclusion;
    }

    private static (float total, float pose, float velocity, float orientation) ExactDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // Descriptor is laid out in 12-float blocks: position, velocity, facing, up,
        // with four root-motion values at the end. Splitting components gives the UI
        // meaningful diagnostics without changing the actual total ranking metric.
        var pose = 0f;
        var velocity = 0f;
        var orientation = 0f;
        var root = 0f;
        var bodyLength = a.Length - 4;
        for (var i = 0; i < bodyLength; i++)
        {
            var d = a[i] - b[i];
            var x = d * d;
            var channel = i % 12;
            if (channel < 3) pose += x;
            else if (channel < 6) velocity += x;
            else orientation += x;
        }
        for (var i = bodyLength; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            root += d * d;
        }
        var inv = 1f / Math.Max(1, a.Length);
        return ((pose + velocity + orientation + root) * inv, pose * inv, velocity * inv, orientation * inv);
    }

    private static float DistanceToScore(float distance)
        => 100f * MathF.Exp(-MathF.Max(0f, distance));
}
