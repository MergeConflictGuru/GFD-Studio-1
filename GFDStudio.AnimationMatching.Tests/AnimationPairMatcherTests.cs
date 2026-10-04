using System;
using System.Numerics;
using System.Threading;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GFDStudio.AnimationMatching.Tests;

[TestClass]
public sealed class AnimationPairMatcherTests
{
    [TestMethod]
    public void CandidateCanBeInMiddleOfSecondSelection()
    {
        var result = AnimationPairMatcher.Match(new PairFakeClip(0), new PairFakeClip(12), (10, 10), (19, 25));
        Assert.AreEqual(10, result.SourceFrame);
        Assert.AreEqual(22, result.CandidateFrame);
    }

    [TestMethod]
    public void SourceCanBeInMiddleOfFirstSelection()
    {
        var result = AnimationPairMatcher.Match(new PairFakeClip(0), new PairFakeClip(12), (5, 15), (22, 22));
        Assert.AreEqual(10, result.SourceFrame);
        Assert.AreEqual(22, result.CandidateFrame);
    }

    [TestMethod]
    public void NoHighlightsUsesLastAndFirstFrames()
    {
        var result = AnimationPairMatcher.Match(new PairFakeClip(0), new PairFakeClip(12));
        Assert.AreEqual(39, result.SourceFrame);
        Assert.AreEqual(0, result.CandidateFrame);
    }

    [TestMethod]
    public void ReversedRangesStayWithinSelectedSets()
    {
        var result = AnimationPairMatcher.Match(new PairFakeClip(0), new PairFakeClip(12), (15, 5), (25, 19));
        Assert.IsTrue(result.SourceFrame >= 5 && result.SourceFrame <= 15);
        Assert.IsTrue(result.CandidateFrame >= 19 && result.CandidateFrame <= 25);
    }

    [TestMethod]
    public void SameAnimationCanMatchItsOwnSelectedFrame()
    {
        var clip = new PairFakeClip(0);
        var result = AnimationPairMatcher.Match(clip, clip, (10, 10), (10, 10));
        Assert.AreEqual(0f, result.Distance, .0001f);
    }

    [TestMethod]
    public void CancellationStopsPairSearch()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() =>
            AnimationPairMatcher.Match(new PairFakeClip(0), new PairFakeClip(12), null, null, cancel.Token));
    }
    private sealed class PairFakeClip : IAnimationClip
    {
        private readonly int shift;
        public PairFakeClip(int value) { shift=value; }
        public string Id=>"clip"+shift; public string DisplayName=>Id;
        public SkeletonDefinition Skeleton {get;}=new(new[]{"root","hips","left_hand","right_hand","left_foot","right_foot"},new[]{-1,0,1,1,1,1},0,2f);
        public int FrameCount=>40; public float FramesPerSecond=>30;
        public void SampleGlobalPose(int frame,Span<BoneTransform> pose)
        {
            float t=frame-shift;
            pose[0]=new(Vector3.Zero,Quaternion.Identity,Vector3.One);
            pose[1]=new(new Vector3(0,1,0),Quaternion.Identity,Vector3.One);
            pose[2]=new(new Vector3(-.5f,t*t*.002f+1,t*.01f),Quaternion.CreateFromAxisAngle(Vector3.UnitX,t*.03f),Vector3.One);
            pose[3]=new(new Vector3(.5f,1,-t*.015f),Quaternion.Identity,Vector3.One);
            pose[4]=new(new Vector3(-.2f,0,0),Quaternion.Identity,Vector3.One);
            pose[5]=new(new Vector3(.2f,0,0),Quaternion.Identity,Vector3.One);
        }
    }
}
