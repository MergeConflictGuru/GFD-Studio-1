using System;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Index;
using GFDStudio.AnimationMatching.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GFDStudio.AnimationMatching.Tests;

[TestClass]
public sealed class AdaptiveAnimationMatcherTests
{
    [TestMethod]
    public void ExpandsFramePoolToFillDistinctAnimationResults()
    {
        var options = new AnimationMatchOptions
        {
            IndexStride = 1,
            QueryStride = 1,
            ApproximateNeighborCount = 1,
            ResultCount = 3,
            ResultSuppressionSeconds = 0f,
            ProjectionDimensions = 8,
            HistorySeconds = new[] { -0.1f, 0f },
            VelocityDeltaSeconds = 1f / 30f,
            MinimumContinuationSeconds = 0f,
            MaxFeatureBones = 6
        };
        var clips = new IAnimationClip[]
        {
            new FlatClip("candidate-a"),
            new FlatClip("candidate-b"),
            new FlatClip("candidate-c")
        };

        using var database = AnimationSearchDatabase.Build(new AnimationCorpus(clips), options);
        var results = new AnimationMatcher(database).Search(new FlatClip("query"));

        Assert.AreEqual(3, results.Count,
            "Frame-level nearest-neighbor truncation must not hide distinct animations.");
    }

    private sealed class FlatClip : IAnimationClip
    {
        private static readonly SkeletonDefinition sSkeleton = new(
            new[] { "root", "pelvis", "spine", "left_hand", "right_hand", "left_foot", "right_foot" },
            new[] { -1, 0, 1, 2, 2, 1, 1 },
            0,
            2f);

        public FlatClip(string id) => Id = id;

        public string Id { get; }
        public string DisplayName => Id;
        public SkeletonDefinition Skeleton => sSkeleton;
        public int FrameCount => 12;
        public float FramesPerSecond => 30f;

        public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
        {
            for (var i = 0; i < sSkeleton.BoneCount; i++)
                destination[i] = new BoneTransform(new Vector3(0f, i * 0.1f, 0f), Quaternion.Identity, Vector3.One);
        }
    }
}
