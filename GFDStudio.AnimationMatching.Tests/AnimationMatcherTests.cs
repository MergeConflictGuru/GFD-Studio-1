using System;
using System.Linq;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;
using GFDStudio.AnimationMatching.Index;
using GFDStudio.AnimationMatching.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GFDStudio.AnimationMatching.Tests;

[TestClass]
public sealed class AnimationMatcherTests
{
    [TestMethod]
    public void StableIdentityExcludesTrivialSelfMatches()
    {
        var options = CreateOptions();
        options.SelfMatchExclusionSeconds = 10f;

        var indexed = new FakeClip("clip-a", Vector3.Zero, 0f);
        var database = AnimationSearchDatabase.Build(
            new AnimationCorpus(new IAnimationClip[] { indexed }), options);
        var matcher = new AnimationMatcher(database);

        var sameIdentity = new FakeClip("clip-a", Vector3.Zero, 0f);
        var differentIdentity = new FakeClip("clip-b", Vector3.Zero, 0f);

        Assert.AreEqual(0, matcher.Search(sameIdentity).Count,
            "Every candidate frame lies inside the configured self-exclusion window.");
        Assert.IsTrue(matcher.Search(differentIdentity).Count > 0,
            "A geometrically identical but differently identified clip must remain searchable.");
    }

    [TestMethod]
    public void IndexContainsMiddleFramesNotOnlyClipStarts()
    {
        var options = CreateOptions();
        var clip = new FakeClip("clip", Vector3.Zero, 0f);
        var database = AnimationSearchDatabase.Build(
            new AnimationCorpus(new IAnimationClip[] { clip }), options);

        Assert.IsTrue(database.Addresses.Any(address => address.FrameIndex > 0 && address.FrameIndex < clip.FrameCount - 1),
            "AniMatch must index candidate continuation points from the middle of animations.");
    }

    [TestMethod]
    public void DescriptorIsInvariantToWorldTranslationAndYaw()
    {
        var options = CreateOptions();
        var extractor = new PoseFeatureExtractor(options);
        var original = new FakeClip("a", Vector3.Zero, 0f);
        var transformed = new FakeClip("b", new Vector3(17f, -3f, 9f), 1.17f);
        var bones = extractor.SelectFeatureBones(original.Skeleton);

        var first = extractor.Extract(original, 7, bones);
        var second = extractor.Extract(transformed, 7, bones);

        Assert.AreEqual(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
            Assert.AreEqual(first[i], second[i], 1e-4f, $"Descriptor dimension {i} changed under a rigid world transform.");
    }

    private static AnimationMatchOptions CreateOptions() => new()
    {
        IndexStride = 1,
        QueryStride = 1,
        ApproximateNeighborCount = 4,
        ResultCount = 8,
        ResultSuppressionSeconds = 0f,
        ProjectionDimensions = 8,
        HistorySeconds = new[] { -0.1f, 0f },
        VelocityDeltaSeconds = 1f / 30f,
        MinimumContinuationSeconds = 0f,
        MaxFeatureBones = 6
    };

    private sealed class FakeClip : IAnimationClip
    {
        private static readonly SkeletonDefinition sSkeleton = new(
            new[] { "root", "pelvis", "spine", "left_hand", "right_hand", "left_foot", "right_foot" },
            new[] { -1, 0, 1, 2, 2, 1, 1 },
            0,
            2f);

        private static readonly Vector3[] sOffsets =
        {
            Vector3.Zero,
            new(0f, 0.9f, 0f),
            new(0f, 1.35f, 0f),
            new(-0.55f, 1.35f, 0.05f),
            new(0.55f, 1.35f, 0.05f),
            new(-0.2f, 0.05f, 0.08f),
            new(0.2f, 0.05f, 0.08f)
        };

        private readonly Vector3 mWorldTranslation;
        private readonly Quaternion mWorldYaw;

        public FakeClip(string id, Vector3 worldTranslation, float worldYaw)
        {
            Id = id;
            mWorldTranslation = worldTranslation;
            mWorldYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, worldYaw);
        }

        public string Id { get; }
        public string DisplayName => Id;
        public SkeletonDefinition Skeleton => sSkeleton;
        public int FrameCount => 12;
        public float FramesPerSecond => 30f;

        public void SampleGlobalPose(int frameIndex, Span<BoneTransform> destination)
        {
            var frame = Math.Clamp(frameIndex, 0, FrameCount - 1);
            var rootLocal = new Vector3(frame * 0.04f, frame * 0.002f, frame * 0.015f);

            for (var i = 0; i < sOffsets.Length; i++)
            {
                // A little symmetric limb motion makes velocity/pose channels non-degenerate while
                // the final world transform remains a pure translation + yaw of the same motion.
                var offset = sOffsets[i];
                if (i == 3)
                    offset.Z += MathF.Sin(frame * 0.25f) * 0.08f;
                else if (i == 4)
                    offset.Z -= MathF.Sin(frame * 0.25f) * 0.08f;

                var worldPosition = Vector3.Transform(rootLocal + offset, mWorldYaw) + mWorldTranslation;
                destination[i] = new BoneTransform(worldPosition, mWorldYaw, Vector3.One);
            }
        }
    }
}
