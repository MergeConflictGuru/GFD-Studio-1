using System.Collections.Generic;
using System.Numerics;
using GFDLibrary.Animations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GFDLibrary.Tests
{
    [TestClass]
    public class AnimationAnalysisTests
    {
        [TestMethod]
        public void BodyMotionRejectsNonNodeTracks()
        {
            var animation = CreateAnimation(TargetKind.Material, CreateKey(Vector3.Zero));

            Assert.IsFalse(AnimationAnalysis.HasBodyMotion(animation));
        }

        [TestMethod]
        public void BodyMotionRejectsSinglePoseTrack()
        {
            var animation = CreateAnimation(TargetKind.Node, CreateKey(Vector3.Zero));

            Assert.IsFalse(AnimationAnalysis.HasBodyMotion(animation));
        }

        [TestMethod]
        public void BodyMotionRejectsRepeatedPoseKeys()
        {
            var first = CreateKey(Vector3.Zero);
            var second = CreateKey(Vector3.Zero);
            second.Time = 1;
            var animation = CreateAnimation(TargetKind.Node, first, second);

            Assert.IsFalse(AnimationAnalysis.HasBodyMotion(animation));
        }

        [TestMethod]
        public void BodyMotionRejectsZeroDuration()
        {
            var first = CreateKey(Vector3.Zero);
            var second = CreateKey(new Vector3(0, 1, 0));
            second.Time = 1;
            var animation = CreateAnimation(TargetKind.Node, first, second);
            animation.Duration = 0;

            Assert.IsFalse(AnimationAnalysis.HasBodyMotion(animation));
        }

        [TestMethod]
        public void BodyMotionRejectsKeysOutsidePlaybackDuration()
        {
            var first = CreateKey(Vector3.Zero);
            var second = CreateKey(new Vector3(0, 1, 0));
            second.Time = 2;
            var animation = CreateAnimation(TargetKind.Node, first, second);
            animation.Duration = 1;

            Assert.IsFalse(AnimationAnalysis.HasBodyMotion(animation));
        }

        [TestMethod]
        public void BodyMotionAcceptsChangingPositionKeys()
        {
            var first = CreateKey(Vector3.Zero);
            var second = CreateKey(new Vector3(0, 1, 0));
            second.Time = 1;
            var animation = CreateAnimation(TargetKind.Node, first, second);

            Assert.IsTrue(AnimationAnalysis.HasBodyMotion(animation));
        }

        [TestMethod]
        public void BodyMotionCanRequireMatchingModelNode()
        {
            var first = CreateKey(Vector3.Zero);
            var second = CreateKey(new Vector3(0, 1, 0));
            second.Time = 1;
            var animation = CreateAnimation(TargetKind.Node, first, second);

            Assert.IsFalse(AnimationAnalysis.HasBodyMotion(
                animation, new HashSet<string> { "OtherNode" }));
            Assert.IsTrue(AnimationAnalysis.HasBodyMotion(
                animation, new HashSet<string> { "TestNode" }));
        }

        private static PRSKey CreateKey(Vector3 position)
        {
            return new PRSKey(KeyType.NodePR)
            {
                Position = position,
                Rotation = Quaternion.Identity,
                Time = 0
            };
        }

        private static Animation CreateAnimation(TargetKind targetKind, params PRSKey[] keys)
        {
            var layer = new AnimationLayer(ResourceVersion.Persona5)
            {
                KeyType = KeyType.NodePR
            };
            foreach (var key in keys)
                layer.Keys.Add(key);

            var controller = new AnimationController(ResourceVersion.Persona5)
            {
                TargetKind = targetKind,
                TargetName = "TestNode"
            };
            controller.Layers.Add(layer);

            var animation = new Animation(ResourceVersion.Persona5);
            animation.Duration = 2;
            animation.Controllers.Add(controller);
            return animation;
        }
    }
}
