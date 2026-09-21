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
        => Search(source, sourceRangeStart, sourceRangeEnd, _database.Options.ResultCount, cancellationToken);

    /// <summary>
    /// Searches an expanding projected-space frame window and reranks each window with the full
    /// descriptor. Larger result limits expand the window, allowing the UI to progressively
    /// improve coverage without blocking the first page on the entire corpus.
    /// </summary>
    public IReadOnlyList<AnimationMatchResult> Search(
        IAnimationClip source,
        int? sourceRangeStart,
        int? sourceRangeEnd,
        int resultCount,
        CancellationToken cancellationToken = default)
    {
        var options = _database.Options;
        resultCount = Math.Max(1, resultCount);
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
        var rangeLength = Math.Max(1, end - start);

        var neighborCount = Math.Min(
            _database.SampleCount,
            Math.Max(1, options.ApproximateNeighborCount));
        while (true)
        {
            SearchNeighbors(
                source,
                sourceBones,
                start,
                end,
                rangeLength,
                neighborCount,
                bestByAddress,
                cancellationToken);

            var output = BuildResults(bestByAddress.Values, resultCount);
            if (output.Count >= resultCount || neighborCount >= _database.SampleCount)
                return output;

            var expanded = neighborCount <= _database.SampleCount / 2
                ? neighborCount * 2
                : _database.SampleCount;
            if (expanded == neighborCount)
                return output;
            neighborCount = expanded;
        }
    }

    private void SearchNeighbors(
        IAnimationClip source,
        int[] sourceBones,
        int start,
        int end,
        int rangeLength,
        int neighborCount,
        Dictionary<(int clip, int frame), AnimationMatchResult> bestByAddress,
        CancellationToken cancellationToken)
    {
        var options = _database.Options;
        var query = new float[_database.DescriptorDimensions];
        var candidateDescriptor = new float[_database.DescriptorDimensions];
        var projected = new float[_database.Projection.OutputDimensions];

        for (var sourceFrame = start; sourceFrame <= end; sourceFrame += options.QueryStride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _database.Extractor.Extract(source, sourceFrame, sourceBones, query);
            _database.NormalizeQuery(query);

            _database.Projection.Project(query, projected);
            var neighbors = _database.Tree.FindNearest(projected, neighborCount);

            foreach (var sampleIndex in neighbors)
            {
                var address = _database.GetAddress(sampleIndex);
                var candidate = _database.Corpus.Clips[address.ClipIndex];
                if (ShouldExcludeSelf(source, sourceFrame, candidate, address.FrameIndex, options)) continue;

                _database.CopyDescriptor(sampleIndex, candidateDescriptor);
                var exact = ExactDistance(query, candidateDescriptor);
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
    }

    private static IReadOnlyList<AnimationMatchResult> BuildResults(
        IEnumerable<AnimationMatchResult> candidates,
        int resultCount)
    {
        // The same animation identity can be present at several transition frames, and stale or
        // externally supplied corpora can contain the same definition more than once. The result
        // grid should show one best representative for each candidate identity.
        var sorted = candidates
            .GroupBy(result => result.Candidate.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(result => result.Distance)
                .ThenBy(result => result.CandidateFrame)
                .First())
            // Legacy/path-based cache entries can carry different IDs for the same browser item.
            // The browser display name contains the stable relative path and animation slot, so
            // use it as a second safety net for the result grid.
            .GroupBy(result => result.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(result => result.Distance)
                .ThenBy(result => result.CandidateFrame)
                .First())
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase);

        var output = new List<AnimationMatchResult>(Math.Min(resultCount, 4096));
        var outputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in sorted)
        {
            if (!outputIds.Add(candidate.Candidate.Id))
                continue;
            output.Add(candidate);
            if (output.Count >= resultCount) break;
        }
        return output;
    }

    private static bool ShouldExcludeSelf(IAnimationClip source, int sourceFrame, IAnimationClip candidate, int candidateFrame, AnimationMatchOptions options)
    {
        if (!string.Equals(source.Id, candidate.Id, StringComparison.Ordinal) &&
            !(source.Id.StartsWith("source:", StringComparison.Ordinal) &&
              string.Equals(source.DisplayName, candidate.DisplayName, StringComparison.OrdinalIgnoreCase)))
            return false;
        // A source animation is never a useful transition candidate for itself. The old frame
        // radius only removed the nearby copy and allowed the same animation to reappear at a
        // distant frame, which made the result grid look like it matched itself.
        return true;
    }

    private static (float total, float pose, float velocity, float orientation) ExactDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
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
