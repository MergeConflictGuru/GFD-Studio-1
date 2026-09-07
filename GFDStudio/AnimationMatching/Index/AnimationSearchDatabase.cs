using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Index;

public sealed class AnimationSearchDatabase : IDisposable
{
    private readonly FrameAddress[]? _addresses;
    private readonly float[]? _descriptors;
    private readonly float[]? _projected;
    private readonly MappedAnimationIndex? _mapped;

    private AnimationSearchDatabase(
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        PoseFeatureExtractor extractor,
        int[][] clipFeatureBones,
        FrameAddress[] addresses,
        float[] descriptors,
        float[] mean,
        float[] invStd,
        float[] dimensionWeights,
        RandomProjection projection,
        float[] projected,
        VpTree tree)
    {
        Corpus = corpus;
        Options = options;
        Extractor = extractor;
        ClipFeatureBones = clipFeatureBones;
        _addresses = addresses;
        _descriptors = descriptors;
        _projected = projected;
        Addresses = addresses;
        Mean = mean;
        InvStd = invStd;
        DimensionWeights = dimensionWeights;
        Projection = projection;
        Tree = tree;
        DescriptorDimensions = mean.Length;
    }

    private AnimationSearchDatabase(
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        PoseFeatureExtractor extractor,
        int[][] clipFeatureBones,
        MappedAnimationIndex mapped,
        float[] mean,
        float[] invStd,
        float[] dimensionWeights,
        RandomProjection projection,
        VpTree tree)
    {
        Corpus = corpus;
        Options = options;
        Extractor = extractor;
        ClipFeatureBones = clipFeatureBones;
        _mapped = mapped;
        Addresses = new MappedAddressList(mapped);
        Mean = mean;
        InvStd = invStd;
        DimensionWeights = dimensionWeights;
        Projection = projection;
        Tree = tree;
        DescriptorDimensions = mean.Length;
    }

    public AnimationCorpus Corpus { get; }
    public AnimationMatchOptions Options { get; }
    public PoseFeatureExtractor Extractor { get; }
    public int[][] ClipFeatureBones { get; }
    public IReadOnlyList<FrameAddress> Addresses { get; }
    public float[] Mean { get; }
    public float[] InvStd { get; }
    public float[] DimensionWeights { get; }
    public RandomProjection Projection { get; }
    public VpTree Tree { get; }
    public int DescriptorDimensions { get; }
    public int SampleCount => _mapped?.SampleCount ?? _addresses!.Length;
    public bool IsMemoryMapped => _mapped is not null;

    // Kept for legacy cache upgrade code. A v3 memory-mapped database intentionally has no
    // materialized multi-gigabyte float arrays.
    public float[] Descriptors => _descriptors ?? throw new InvalidOperationException("Descriptors are memory-mapped in this database.");
    public float[] Projected => _projected ?? throw new InvalidOperationException("Projected vectors are memory-mapped in this database.");

    internal float[]? InMemoryDescriptors => _descriptors;
    internal float[]? InMemoryProjected => _projected;

    public FrameAddress GetAddress(int sampleIndex)
        => _mapped?.GetAddress(sampleIndex) ?? _addresses![sampleIndex];

    public ReadOnlySpan<float> GetDescriptor(int sampleIndex)
    {
        if (_descriptors is null)
            throw new InvalidOperationException("Use CopyDescriptor for a memory-mapped AniMatch database.");
        return _descriptors.AsSpan(sampleIndex * DescriptorDimensions, DescriptorDimensions);
    }

    public void CopyDescriptor(int sampleIndex, Span<float> destination)
    {
        if (destination.Length < DescriptorDimensions)
            throw new ArgumentException("Destination is smaller than the descriptor.", nameof(destination));

        if (_mapped is not null)
        {
            _mapped.CopyDescriptor(sampleIndex, destination);
            return;
        }

        _descriptors!.AsSpan(sampleIndex * DescriptorDimensions, DescriptorDimensions).CopyTo(destination);
    }

    internal float GetProjectedValue(int sampleIndex, int dimension)
        => _mapped?.GetProjectedValue(sampleIndex, dimension)
           ?? _projected![sampleIndex * Projection.OutputDimensions + dimension];

    internal static AnimationSearchDatabase FromCache(
        AnimationCorpus corpus, AnimationMatchOptions options, PoseFeatureExtractor extractor, int[][] clipFeatureBones,
        FrameAddress[] addresses, float[] descriptors, float[] mean, float[] invStd, float[] dimensionWeights,
        RandomProjection projection, float[] projected, VpTree tree)
        => new(corpus, options, extractor, clipFeatureBones, addresses, descriptors, mean, invStd, dimensionWeights, projection, projected, tree);

    internal static AnimationSearchDatabase FromMappedCache(
        AnimationCorpus corpus, AnimationMatchOptions options, PoseFeatureExtractor extractor, int[][] clipFeatureBones,
        MappedAnimationIndex mapped, float[] mean, float[] invStd, float[] dimensionWeights,
        RandomProjection projection, VpTree tree)
        => new(corpus, options, extractor, clipFeatureBones, mapped, mean, invStd, dimensionWeights, projection, tree);

    public static Task<AnimationSearchDatabase> BuildAsync(
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Build(corpus, options, progress, cancellationToken), cancellationToken);

    public static AnimationSearchDatabase Build(
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (corpus.Clips.Count == 0) throw new ArgumentException("The animation corpus is empty.", nameof(corpus));

        var extractor = new PoseFeatureExtractor(options);
        var featureBones = new int[corpus.Clips.Count][];
        for (var i = 0; i < corpus.Clips.Count; i++) featureBones[i] = extractor.SelectFeatureBones(corpus.Clips[i].Skeleton);

        // A corpus should normally share one skeleton after GFD showroom retargeting. To support rigs
        // with different bone counts, descriptor cardinality is forced to the smallest selected set.
        var commonBoneCount = int.MaxValue;
        foreach (var bones in featureBones) commonBoneCount = Math.Min(commonBoneCount, bones.Length);
        if (commonBoneCount < 1) throw new InvalidOperationException("No feature bones were found.");
        for (var i = 0; i < featureBones.Length; i++)
            if (featureBones[i].Length != commonBoneCount)
                featureBones[i] = featureBones[i][..commonBoneCount];

        var frameCounts = new int[corpus.Clips.Count];
        Parallel.For(0, corpus.Clips.Count, new ParallelOptions { CancellationToken = cancellationToken }, clipIndex =>
        {
            var clip = corpus.Clips[clipIndex];
            try
            {
                frameCounts[clipIndex] = clip.FrameCount;
            }
            finally
            {
                if (clip is IAnimationClipResourceOwner resourceOwner)
                    resourceOwner.ReleaseResources();
            }
        });

        var dimensions = extractor.GetDescriptorLength(commonBoneCount);
        var addresses = new List<FrameAddress>();
        var clipSampleStarts = new int[corpus.Clips.Count + 1];
        for (var clipIndex = 0; clipIndex < corpus.Clips.Count; clipIndex++)
        {
            clipSampleStarts[clipIndex] = addresses.Count;
            var clip = corpus.Clips[clipIndex];
            var minContinuationFrames = Math.Max(1, (int)MathF.Ceiling(options.MinimumContinuationSeconds * clip.FramesPerSecond));
            if (frameCounts[clipIndex] <= minContinuationFrames) continue;
            var last = frameCounts[clipIndex] - 1 - minContinuationFrames;
            for (var frame = 0; frame <= last; frame += options.IndexStride)
                addresses.Add(new FrameAddress(clipIndex, frame));
        }
        clipSampleStarts[corpus.Clips.Count] = addresses.Count;

        var addressArray = addresses.ToArray();
        if (addressArray.Length == 0)
            throw new InvalidOperationException("No animation has enough frames to provide a searchable continuation.");

        var packed = new float[checked(addressArray.Length * dimensions)];
        var completed = 0;

        Parallel.For(0, corpus.Clips.Count, new ParallelOptions { CancellationToken = cancellationToken }, clipIndex =>
        {
            var clip = corpus.Clips[clipIndex];
            var firstSample = clipSampleStarts[clipIndex];
            var lastSampleExclusive = clipSampleStarts[clipIndex + 1];
            try
            {
                for (var sampleIndex = firstSample; sampleIndex < lastSampleExclusive; sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var address = addressArray[sampleIndex];
                    extractor.Extract(
                        clip,
                        address.FrameIndex,
                        featureBones[clipIndex],
                        packed.AsSpan(sampleIndex * dimensions, dimensions));

                    var now = Interlocked.Increment(ref completed);
                    if ((now & 127) == 0 || now == addressArray.Length)
                        progress?.Report((now, addressArray.Length));
                }
            }
            finally
            {
                if (clip is IAnimationClipResourceOwner resourceOwner)
                    resourceOwner.ReleaseResources();
            }
        });

        var mean = new float[dimensions];
        var invStd = new float[dimensions];
        var dimensionWeights = extractor.GetPostNormalizationWeights(commonBoneCount);
        ComputeNormalization(packed, addressArray.Length, dimensions, mean, invStd);
        NormalizeInPlace(packed, addressArray.Length, dimensions, mean, invStd, dimensionWeights);

        var projection = new RandomProjection(dimensions, options.ProjectionDimensions, options.ProjectionSeed);
        var projected = new float[checked(addressArray.Length * projection.OutputDimensions)];
        Parallel.For(0, addressArray.Length, new ParallelOptions { CancellationToken = cancellationToken }, sampleIndex =>
        {
            projection.Project(
                packed.AsSpan(sampleIndex * dimensions, dimensions),
                projected.AsSpan(sampleIndex * projection.OutputDimensions, projection.OutputDimensions));
        });

        var tree = new VpTree(projected, projection.OutputDimensions);
        return new AnimationSearchDatabase(corpus, options, extractor, featureBones, addressArray, packed, mean, invStd, dimensionWeights, projection, projected, tree);
    }

    public void NormalizeQuery(Span<float> descriptor)
    {
        for (var d = 0; d < DescriptorDimensions; d++)
            descriptor[d] = (descriptor[d] - Mean[d]) * InvStd[d] * DimensionWeights[d];
    }

    private static void ComputeNormalization(float[] packed, int count, int dimensions, float[] mean, float[] invStd)
    {
        if (count == 0) return;
        for (var sample = 0; sample < count; sample++)
        {
            var offset = sample * dimensions;
            for (var d = 0; d < dimensions; d++) mean[d] += packed[offset + d];
        }
        for (var d = 0; d < dimensions; d++) mean[d] /= count;

        var variance = new double[dimensions];
        for (var sample = 0; sample < count; sample++)
        {
            var offset = sample * dimensions;
            for (var d = 0; d < dimensions; d++)
            {
                var x = packed[offset + d] - mean[d];
                variance[d] += x * x;
            }
        }
        for (var d = 0; d < dimensions; d++)
        {
            var std = MathF.Sqrt((float)(variance[d] / Math.Max(1, count - 1)));
            invStd[d] = 1f / MathF.Max(std, 1e-4f);
        }
    }

    private static void NormalizeInPlace(float[] packed, int count, int dimensions, float[] mean, float[] invStd, float[] dimensionWeights)
    {
        for (var sample = 0; sample < count; sample++)
        {
            var offset = sample * dimensions;
            for (var d = 0; d < dimensions; d++)
                packed[offset + d] = (packed[offset + d] - mean[d]) * invStd[d] * dimensionWeights[d];
        }
    }

    public void Dispose() => _mapped?.Dispose();

    private sealed class MappedAddressList : IReadOnlyList<FrameAddress>
    {
        private readonly MappedAnimationIndex _mapped;
        public MappedAddressList(MappedAnimationIndex mapped) => _mapped = mapped;
        public int Count => _mapped.SampleCount;
        public FrameAddress this[int index] => _mapped.GetAddress(index);

        public IEnumerator<FrameAddress> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return _mapped.GetAddress(i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
