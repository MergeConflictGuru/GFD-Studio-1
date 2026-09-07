using System;
using System.IO;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;
using GFDStudio.AnimationMatching.Index;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.AnimationMatching.Search;
using GFDStudio.AnimationMatching.Stitching;
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
    public void MemoryMappedCachePreservesEveryIndexedFrameAndSearches()
    {
        var options = CreateOptions();
        var clip = new FakeClip("clip", Vector3.Zero, 0f);
        var corpus = new AnimationCorpus(new IAnimationClip[] { clip });
        using var built = AnimationSearchDatabase.Build(corpus, options);
        var expectedFrames = built.Addresses.Select(address => address.FrameIndex).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "animatch-test-" + Guid.NewGuid().ToString("N") + ".bin");
        AnimationSearchDatabase? loaded = null;

        try
        {
            AnimationIndexCache.Save(path, built, "test-corpus");
            loaded = AnimationIndexCache.TryLoad(path, corpus, options, "test-corpus");

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.IsMemoryMapped, "A v3 cache should search directly from a memory mapping.");
            Assert.AreEqual(built.SampleCount, loaded.SampleCount);
            CollectionAssert.AreEqual(expectedFrames, loaded.Addresses.Select(address => address.FrameIndex).ToArray(),
                "Memory mapping must not change temporal sampling; IndexStride=1 still means every eligible frame.");

            var results = new AnimationMatcher(loaded).Search(new FakeClip("query", Vector3.Zero, 0f));
            Assert.IsTrue(results.Count > 0, "The mapped FP16 descriptor cache must remain searchable.");
        }
        finally
        {
            loaded?.Dispose();
            try { File.Delete(path); } catch { }
        }
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

    [TestMethod]
    public void TailStartsAtMatchedFrameAndUsesZeroBasedFrames()
    {
        var candidate = new FakeClip("candidate", Vector3.Zero, 0f);
        var tail = new TailAnimation(candidate, 5);
        var pose = new BoneTransform[tail.Skeleton.BoneCount];
        var candidatePose = new BoneTransform[candidate.Skeleton.BoneCount];

        tail.SampleGlobalPose(0, pose);
        candidate.SampleGlobalPose(5, candidatePose);

        Assert.AreEqual(candidate.FrameCount - 5, tail.FrameCount);
        Assert.AreEqual(candidatePose[0].Position, pose[0].Position);

        tail.SampleGlobalPose(tail.FrameCount - 1, pose);
        candidate.SampleGlobalPose(candidate.FrameCount - 1, candidatePose);
        Assert.AreEqual(candidatePose[0].Position, pose[0].Position);
    }

    [TestMethod]
    public void StitchCanAlignOrPreserveCandidateWorldPositionAndYaw()
    {
        var source = new FakeClip("source", new Vector3(2f, 3f, -4f), 0.35f);
        var candidate = new FakeClip("candidate", new Vector3(-8f, 6f, 12f), -1.1f);
        var aligned = new StitchedAnimation(source, 5, candidate, 5, 0f, alignPositionAndYaw: true);
        var unaligned = new StitchedAnimation(source, 5, candidate, 5, 0f, alignPositionAndYaw: false);
        var sourcePose = new BoneTransform[source.Skeleton.BoneCount];
        var candidatePose = new BoneTransform[source.Skeleton.BoneCount];
        var stitchedPose = new BoneTransform[source.Skeleton.BoneCount];

        source.SampleGlobalPose(6, sourcePose);
        candidate.SampleGlobalPose(6, candidatePose);
        aligned.SampleGlobalPose(6, stitchedPose);

        Assert.IsTrue(aligned.AlignPositionAndYaw);
        AssertPositionEqual(sourcePose[0].Position, stitchedPose[0].Position);
        AssertRotationEqual(sourcePose[0].Rotation, stitchedPose[0].Rotation);

        unaligned.SampleGlobalPose(6, stitchedPose);
        Assert.IsFalse(unaligned.AlignPositionAndYaw);
        AssertPositionEqual(candidatePose[0].Position, stitchedPose[0].Position);
        AssertRotationEqual(candidatePose[0].Rotation, stitchedPose[0].Rotation);
    }

    [TestMethod]
    public void StitchExportPartsAreCutAtTheMatchBoundaryAndAligned()
    {
        var source = new FakeClip("source", new Vector3(2f, 3f, -4f), 0.35f);
        var candidate = new FakeClip("candidate", new Vector3(-8f, 6f, 12f), -1.1f);
        var stitched = new StitchedAnimation(source, 5, candidate, 7, 0f, alignPositionAndYaw: true);
        var parts = stitched.CreateExportParts();
        var sourcePose = new BoneTransform[source.Skeleton.BoneCount];
        var sourcePartPose = new BoneTransform[source.Skeleton.BoneCount];
        var candidatePartPose = new BoneTransform[source.Skeleton.BoneCount];

        source.SampleGlobalPose(5, sourcePose);
        parts.Source.SampleGlobalPose(parts.Source.FrameCount - 1, sourcePartPose);
        parts.Candidate.SampleGlobalPose(0, candidatePartPose);

        Assert.AreEqual(6, parts.Source.FrameCount);
        Assert.AreEqual(candidate.FrameCount - 7, parts.Candidate.FrameCount);
        AssertPositionEqual(sourcePose[0].Position, sourcePartPose[0].Position);
        AssertRotationEqual(sourcePose[0].Rotation, sourcePartPose[0].Rotation);
        AssertPositionEqual(sourcePose[0].Position, candidatePartPose[0].Position);
        AssertRotationEqual(sourcePose[0].Rotation, candidatePartPose[0].Rotation);
    }

    [TestMethod]
    public void FullSkeletonPreviewUsesAnimatedMotionRootForStitchAlignment()
    {
        var fileRoot = new Node("RootNode");
        var axisRoot = new Node("root");
        var motionRoot = new Node("Bip01");
        fileRoot.AddChildNode(axisRoot);
        axisRoot.AddChildNode(motionRoot);
        var model = new Model { RootNode = fileRoot };
        var clip = new GfdTargetAnimationClip("preview", "preview", model, new Animation(), 30f);

        Assert.AreEqual(2, clip.Skeleton.RootBoneIndex,
            "Bip01 carries locomotion on dancing-style rigs and must win over static RootNode/root ancestors.");
    }

    [TestMethod]
    public void FullSkeletonPreviewFallsBackToPersonaMotionRoot()
    {
        var fileRoot = new Node("RootNode");
        fileRoot.AddChildNode(new Node("root"));
        var model = new Model { RootNode = fileRoot };
        var clip = new GfdTargetAnimationClip("preview", "preview", model, new Animation(), 30f);

        Assert.AreEqual(1, clip.Skeleton.RootBoneIndex,
            "Persona-style rigs carry locomotion on root rather than the static file-level RootNode.");
    }

    private static void AssertPositionEqual(Vector3 expected, Vector3 actual)
    {
        Assert.AreEqual(expected.X, actual.X, 1e-4f);
        Assert.AreEqual(expected.Y, actual.Y, 1e-4f);
        Assert.AreEqual(expected.Z, actual.Z, 1e-4f);
    }

    private static void AssertRotationEqual(Quaternion expected, Quaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.AreEqual(1f, dot, 1e-4f);
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
