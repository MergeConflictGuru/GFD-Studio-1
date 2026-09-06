using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Index;

public sealed class AnimationSearchDatabase
{
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
        Addresses = addresses;
        Descriptors = descriptors;
        Mean = mean;
        InvStd = invStd;
        DimensionWeights = dimensionWeights;
        Projection = projection;
        Projected = projected;
        Tree = tree;
        DescriptorDimensions = mean.Length;
    }

    public AnimationCorpus Corpus { get; }
    public AnimationMatchOptions Options { get; }
    public PoseFeatureExtractor Extractor { get; }
    public int[][] ClipFeatureBones { get; }
    public FrameAddress[] Addresses { get; }
    public float[] Descriptors { get; }
    public float[] Mean { get; }
    public float[] InvStd { get; }
    public float[] DimensionWeights { get; }
    public RandomProjection Projection { get; }
    public float[] Projected { get; }
    public VpTree Tree { get; }
    public int DescriptorDimensions { get; }
    public int SampleCount => Addresses.Length;

    public ReadOnlySpan<float> GetDescriptor(int sampleIndex)
        => Descriptors.AsSpan(sampleIndex * DescriptorDimensions, DescriptorDimensions);

    internal static AnimationSearchDatabase FromCache(
        AnimationCorpus corpus, AnimationMatchOptions options, PoseFeatureExtractor extractor, int[][] clipFeatureBones,
        FrameAddress[] addresses, float[] descriptors, float[] mean, float[] invStd, float[] dimensionWeights,
        RandomProjection projection, float[] projected, VpTree tree)
        => new(corpus, options, extractor, clipFeatureBones, addresses, descriptors, mean, invStd, dimensionWeights, projection, projected, tree);

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

        var dimensions = extractor.GetDescriptorLength(commonBoneCount);
        var addresses = new List<FrameAddress>();
        for (var clipIndex = 0; clipIndex < corpus.Clips.Count; clipIndex++)
        {
            var clip = corpus.Clips[clipIndex];
            var minContinuationFrames = Math.Max(1, (int)MathF.Ceiling(options.MinimumContinuationSeconds * clip.FramesPerSecond));
            if (clip.FrameCount <= minContinuationFrames) continue;
            var last = clip.FrameCount - 1 - minContinuationFrames;
            for (var frame = 0; frame <= last; frame += options.IndexStride)
                addresses.Add(new FrameAddress(clipIndex, frame));
        }

        var addressArray = addresses.ToArray();
        if (addressArray.Length == 0)
            throw new InvalidOperationException("No animation has enough frames to provide a searchable continuation.");
        var packed = new float[addressArray.Length * dimensions];
        var completed = 0;
        Parallel.For(0, addressArray.Length, new ParallelOptions { CancellationToken = cancellationToken }, sampleIndex =>
        {
            var address = addressArray[sampleIndex];
            var clip = corpus.Clips[address.ClipIndex];
            extractor.Extract(clip, address.FrameIndex, featureBones[address.ClipIndex], packed.AsSpan(sampleIndex * dimensions, dimensions));
            var now = Interlocked.Increment(ref completed);
            if ((now & 127) == 0 || now == addressArray.Length) progress?.Report((now, addressArray.Length));
        });

        var mean = new float[dimensions];
        var invStd = new float[dimensions];
        var dimensionWeights = extractor.GetPostNormalizationWeights(commonBoneCount);
        ComputeNormalization(packed, addressArray.Length, dimensions, mean, invStd);
        NormalizeInPlace(packed, addressArray.Length, dimensions, mean, invStd, dimensionWeights);

        var projection = new RandomProjection(dimensions, options.ProjectionDimensions, options.ProjectionSeed);
        var projected = new float[addressArray.Length * projection.OutputDimensions];
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
}
