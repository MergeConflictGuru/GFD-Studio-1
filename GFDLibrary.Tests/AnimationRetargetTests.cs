using System;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GFDLibrary.Tests
{
    [TestClass]
    public class AnimationRetargetTests
    {
        private const string NodeName = "TestNode";

        [TestMethod]
        public void RetargetPreservesRotationOnlyKeys()
        {
            var sourceRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitX, 0.25f );
            var targetRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitY, 0.5f );
            var keyRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, 0.75f );
            var key = new PRSKey( KeyType.NodeRHalf ) { Rotation = keyRotation };
            var animation = CreateAnimation( KeyType.NodeRHalf, key );

            animation.Retarget(
                CreateModel( new Vector3( 1, 2, 3 ), sourceRotation ),
                CreateModel( new Vector3( 4, 5, 6 ), targetRotation ),
                false );

            Assert.IsFalse( key.HasPosition );
            Assert.IsTrue( key.HasRotation );
            Assert.IsFalse( key.HasScale );
            AssertQuaternionEqual( targetRotation * ( Quaternion.Inverse( sourceRotation ) * keyRotation ), key.Rotation );
        }

        [TestMethod]
        public void RetargetPreservesScaleOnlyKeys()
        {
            var keyScale = new Vector3( 1.25f, 0.75f, 2f );
            var key = new PRSKey( KeyType.NodeSHalf ) { Scale = keyScale };
            var animation = CreateAnimation( KeyType.NodeSHalf, key );

            animation.Retarget(
                CreateModel( new Vector3( 1, 2, 3 ), Quaternion.Identity ),
                CreateModel( new Vector3( 4, 5, 6 ), Quaternion.CreateFromAxisAngle( Vector3.UnitY, 0.5f ) ),
                false );

            Assert.IsFalse( key.HasPosition );
            Assert.IsFalse( key.HasRotation );
            Assert.IsTrue( key.HasScale );
            AssertVectorEqual( keyScale, key.Scale );
        }

        [TestMethod]
        public void RetargetPreservesPositionOnlyKeys()
        {
            var sourcePosition = new Vector3( 1, 2, 3 );
            var targetPosition = new Vector3( 4, 6, 8 );
            var keyPosition = new Vector3( 2, 4, 6 );
            var key = new KeyType31Dancing { Position = keyPosition };
            var animation = CreateAnimation( KeyType.Type31, key );

            animation.Retarget(
                CreateModel( sourcePosition, Quaternion.CreateFromAxisAngle( Vector3.UnitX, 0.25f ) ),
                CreateModel( targetPosition, Quaternion.CreateFromAxisAngle( Vector3.UnitY, 0.5f ) ),
                false );

            Assert.IsTrue( key.HasPosition );
            Assert.IsFalse( key.HasRotation );
            Assert.IsFalse( key.HasScale );
            AssertVectorEqual( targetPosition + keyPosition - sourcePosition, key.Position );
        }

        [TestMethod]
        public void RetargetAdjustsChannelsPresentInCombinedKeys()
        {
            var sourcePosition = new Vector3( 2, 3, 4 );
            var targetPosition = new Vector3( 5, 7, 9 );
            var sourceRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitX, 0.25f );
            var targetRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitY, 0.5f );
            var keyPosition = new Vector3( 3, 5, 7 );
            var keyRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, 0.75f );
            var key = new PRSKey( KeyType.NodePR )
            {
                Position = keyPosition,
                Rotation = keyRotation
            };
            var animation = CreateAnimation( KeyType.NodePR, key );

            animation.Retarget(
                CreateModel( sourcePosition, sourceRotation ),
                CreateModel( targetPosition, targetRotation ),
                false );

            AssertVectorEqual( targetPosition + keyPosition - sourcePosition, key.Position );
            AssertQuaternionEqual( targetRotation * ( Quaternion.Inverse( sourceRotation ) * keyRotation ), key.Rotation );
            Assert.IsFalse( key.HasScale );
        }

        [TestMethod]
        public void RetargetIgnoresDuplicateNodeNames()
        {
            var sourceRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitX, 0.25f );
            var targetRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitY, 0.5f );
            var keyRotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, 0.75f );
            var key = new PRSKey( KeyType.NodeRHalf ) { Rotation = keyRotation };
            var animation = CreateAnimation( KeyType.NodeRHalf, key );

            var sourceModel = CreateModel( new Vector3( 1, 2, 3 ), sourceRotation );
            sourceModel.RootNode.AddChildNode( new Node( NodeName ) );
            var targetModel = CreateModel( new Vector3( 4, 5, 6 ), targetRotation );
            targetModel.RootNode.AddChildNode( new Node( NodeName ) );

            animation.Retarget(
                sourceModel,
                targetModel,
                false );

            AssertQuaternionEqual( targetRotation * ( Quaternion.Inverse( sourceRotation ) * keyRotation ), key.Rotation );
        }

        [TestMethod]
        public void RetargetMapsPersonaSkeletonNamesAcrossGames()
        {
            var source = new Model( ResourceVersion.Persona5Royal );
            var sourceRoot = new Node( "root" );
            var sourceRotationRoot = new Node( "rot" );
            var sourceBipedRoot = new Node( "Bip01" );
            var sourcePelvis = new Node( "Bip01 Pelvis" );
            var sourceSpine = new Node( "Bip01 Spine" );
            var sourceLeftArm = new Node( "Bip01 L UpperArm" );
            source.RootNode = sourceRoot;
            sourceRoot.AddChildNode( sourceRotationRoot );
            sourceRotationRoot.AddChildNode( sourceBipedRoot );
            sourceBipedRoot.AddChildNode( sourcePelvis );
            sourcePelvis.AddChildNode( sourceSpine );
            sourceSpine.AddChildNode( sourceLeftArm );

            var target = new Model( ResourceVersion.Persona5Dancing );
            var targetRootNode = new Node( "RootNode" );
            var targetRoot = new Node( "root" );
            var targetHips = new Node( "Hips" );
            var targetSpine = new Node( "Spine" );
            var targetLeftArm = new Node( "LeftArm" );
            target.RootNode = targetRootNode;
            targetRootNode.AddChildNode( targetRoot );
            targetRoot.AddChildNode( targetHips );
            targetHips.AddChildNode( targetSpine );
            targetSpine.AddChildNode( targetLeftArm );

            var animation = new Animation( ResourceVersion.Persona5Royal );
            animation.Controllers.Add( CreateController( "RootNode" ) );
            animation.Controllers.Add( CreateController( "Bip01 Pelvis" ) );
            animation.Controllers.Add( CreateController( "Bip01 Spine" ) );
            animation.Controllers.Add( CreateController( "Bip01 L UpperArm" ) );
            animation.Controllers.Add( CreateController( "Bip01 Footsteps" ) );

            animation.Retarget( source, target, false );

            CollectionAssert.AreEqual(
                new[] { "root", "Hips", "Spine", "LeftArm" },
                animation.Controllers.Select( controller => controller.TargetName ).ToArray() );
            Assert.AreEqual( 1, animation.Controllers[ 0 ].TargetId );
            Assert.AreEqual( 2, animation.Controllers[ 1 ].TargetId );
            Assert.AreEqual( 3, animation.Controllers[ 2 ].TargetId );
            Assert.AreEqual( 4, animation.Controllers[ 3 ].TargetId );
            Assert.AreEqual( ResourceVersion.Persona5Dancing, animation.Version );
        }

        [TestMethod]
        public void RetargetMapsDanceHairFamilyNamesAcrossHairRigs()
        {
            var sourceRoot = new Node("RootNode");
            sourceRoot.AddChildNode(new Node("L_B_longhair_00"));
            var source = new Model(ResourceVersion.Persona5Dancing) { RootNode = sourceRoot };

            var targetRoot = new Node("RootNode");
            targetRoot.AddChildNode(new Node("L_hair_00"));
            var target = new Model(ResourceVersion.Persona5Dancing) { RootNode = targetRoot };

            var animation = new Animation(source.Version);
            animation.Controllers.Add(CreateController("L_B_longhair_00"));

            animation.Retarget(source, target, false);

            Assert.AreEqual("L_hair_00", animation.Controllers.Single().TargetName);
            Assert.AreEqual(1, animation.Controllers.Single().TargetId);
        }

        [TestMethod]
        public void RetargetMapsP5RoyalHairToDanceLongHairFamily()
        {
            var sourceRoot = new Node("RootNode");
            sourceRoot.AddChildNode(new Node("b l hair01"));
            var source = new Model(ResourceVersion.Persona5Royal) { RootNode = sourceRoot };

            var targetRoot = new Node("RootNode");
            targetRoot.AddChildNode(new Node("L_B_longhair_00"));
            var target = new Model(ResourceVersion.Persona5Royal) { RootNode = targetRoot };

            var animation = new Animation(source.Version);
            animation.Controllers.Add(CreateController("b l hair01"));

            animation.Retarget(source, target, false);

            Assert.AreEqual("L_B_longhair_00", animation.Controllers.Single().TargetName);
            Assert.AreEqual(1, animation.Controllers.Single().TargetId);
        }

        [TestMethod]
        public void CrossGameBakePreservesBindPoseWithDifferentAxesAndParents()
        {
            var (source, target) = CreateDifferentSkeletons();
            var animation = new Animation(source.Version) { Duration = 1 };
            animation.Retarget(source, target, false);
            var bind = AnimationPoseEvaluator.Evaluate(target, null, 0);
            foreach (var time in new[] {0f, .43f, 1f})
                foreach (var pair in AnimationPoseEvaluator.Evaluate(target, animation, time))
                    AssertTransformEqual(bind[pair.Key], pair.Value);
            Assert.AreEqual(animation.Controllers.Count, animation.Controllers.Select(c => c.TargetId).Distinct().Count());
        }

        [TestMethod]
        public void CrossGameBakeIncludesUnmappedAncestorMotionAndKeepsTargetOffsets()
        {
            var (source, target) = CreateDifferentSkeletons();
            var animation = new Animation(source.Version) { Duration = 1 };
            var ancestor = CreateController("rot");
            ancestor.Layers[0].Keys.Add(new PRSKey(KeyType.NodeRHalf) {
                Time = 1, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f)
            });
            animation.Controllers.Add(ancestor);
            animation.Retarget(source, target, false);
            var pose = AnimationPoseEvaluator.Evaluate(target, animation, 1);
            var bind = AnimationPoseEvaluator.Evaluate(target, null, 0);
            var arm = target.Nodes.First(n => n.Name == "LeftArm");
            Assert.IsTrue(Vector3.Distance(pose[arm].Translation, bind[arm].Translation) > 1);
            foreach (var controller in animation.Controllers.Where(c => c.TargetName != "root"))
                foreach (PRSKey key in controller.Layers[0].Keys)
                    AssertVectorEqual(target.Nodes.First(n => n.Name == controller.TargetName).Translation, key.Position);
        }

        [TestMethod]
        public void StandaloneFaceBakesHeadWorldPlacementInsteadOfBodyLocalKeys()
        {
            var root = new Node("RootNode");
            var neck = new Node("neck", new Vector3(0, 10, 0), Quaternion.Identity, Vector3.One);
            var head = new Node("head", new Vector3(0, 3, 0), Quaternion.Identity, Vector3.One);
            root.AddChildNode(neck); neck.AddChildNode(head);
            var model = new Model(ResourceVersion.Persona5Dancing) { RootNode = root };
            var animation = new Animation(model.Version) { Duration = 1 };
            var controller = CreateController("neck");
            controller.Layers[0].Keys.Add(new PRSKey(KeyType.NodeRHalf) { Time = 1, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f) });
            animation.Controllers.Add(controller);
            var combined = new ModelPack(model.Version) { Model = model, AnimationPack = new AnimationPack(model.Version) };
            combined.AnimationPack.Animations.Add(animation);
            var faceRoot = new Node("RootNode");
            var faceHead = new Node("head", new Vector3(0, 13, 0), Quaternion.Identity, Vector3.One);
            faceRoot.AddChildNode(faceHead);
            var face = new Model(model.Version) { RootNode = faceRoot };
            var exported = SplitCharacterRetargeter.ForStandalonePart(combined, face);
            foreach (var time in new[] {0f, 1f})
                AssertTransformEqual(AnimationPoseEvaluator.Evaluate(model, animation, time)[head],
                    AnimationPoseEvaluator.Evaluate(face, exported.Animations[0], time)[faceHead]);
            Assert.AreEqual(1, exported.Animations[0].Controllers.Single(c => c.TargetName == "head").TargetId);
        }

        [TestMethod]
        public void CrossGameAdditiveFailureDoesNotModifyBaseClips()
        {
            var (source, target) = CreateDifferentSkeletons();
            var pack = new AnimationPack(source.Version);
            var animation = new Animation(source.Version);
            var controller = CreateController("Bip01 Pelvis");
            animation.Controllers.Add(controller);
            pack.Animations.Add(animation);
            pack.BlendAnimations.Add(animation);
            Assert.ThrowsException<NotSupportedException>(() => pack.Retarget(source, target, false));
            Assert.AreSame(controller, animation.Controllers[0]);
            Assert.AreEqual("Bip01 Pelvis", controller.TargetName);
        }

        private static (Model source, Model target) CreateDifferentSkeletons()
        {
            var source = new Model(ResourceVersion.Persona5Royal) { RootNode = new Node("RootNode") };
            var rot = new Node("rot"); source.RootNode.AddChildNode(rot);
            var biped = new Node("Bip01", new Vector3(0, 10, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f), Vector3.One);
            rot.AddChildNode(biped);
            var pelvis = new Node("Bip01 Pelvis"); biped.AddChildNode(pelvis);
            var spine = new Node("Bip01 Spine", new Vector3(4, 0, 0), Quaternion.Identity, Vector3.One); pelvis.AddChildNode(spine);
            spine.AddChildNode(new Node("Bip01 L UpperArm", new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .6f), Vector3.One));
            var target = new Model(ResourceVersion.Persona5Dancing) { RootNode = new Node("RootNode") };
            var root = new Node("root", new Vector3(0, 15, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.5f), Vector3.One);
            target.RootNode.AddChildNode(root);
            var hips = new Node("Hips"); root.AddChildNode(hips);
            hips.AddChildNode(new Node("Spine", new Vector3(0, 2, 0), Quaternion.Identity, Vector3.One));
            hips.AddChildNode(new Node("LeftArm", new Vector3(6, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.8f), Vector3.One));
            return (source, target);
        }

        private static void AssertTransformEqual(Matrix4x4 expected, Matrix4x4 actual)
        {
            Matrix4x4.Decompose(expected, out var es, out var er, out var ep);
            Matrix4x4.Decompose(actual, out var s, out var r, out var p);
            AssertVectorEqual(ep, p); AssertVectorEqual(es, s); AssertQuaternionEqual(er, r);
        }

        private static Animation CreateAnimation( KeyType keyType, PRSKey key )
        {
            var layer = new AnimationLayer( ResourceVersion.Persona5 )
            {
                KeyType = keyType
            };
            layer.Keys.Add( key );

            var controller = new AnimationController( ResourceVersion.Persona5 )
            {
                TargetKind = TargetKind.Node,
                TargetName = NodeName
            };
            controller.Layers.Add( layer );

            var animation = new Animation( ResourceVersion.Persona5 );
            animation.Controllers.Add( controller );
            return animation;
        }

        private static AnimationController CreateController( string targetName )
        {
            var layer = new AnimationLayer( ResourceVersion.Persona5Royal )
            {
                KeyType = KeyType.NodeRHalf
            };
            layer.Keys.Add( new PRSKey( KeyType.NodeRHalf )
            {
                Rotation = Quaternion.Identity
            } );

            var controller = new AnimationController( ResourceVersion.Persona5Royal )
            {
                TargetKind = TargetKind.Node,
                TargetName = targetName
            };
            controller.Layers.Add( layer );
            return controller;
        }

        private static Model CreateModel( Vector3 position, Quaternion rotation )
        {
            var root = new Node( "RootNode" );
            root.AddChildNode( new Node( NodeName, position, rotation, Vector3.One ) );
            return new Model( ResourceVersion.Persona5 ) { RootNode = root };
        }

        private static void AssertVectorEqual( Vector3 expected, Vector3 actual )
        {
            Assert.AreEqual( expected.X, actual.X, 0.00001f );
            Assert.AreEqual( expected.Y, actual.Y, 0.00001f );
            Assert.AreEqual( expected.Z, actual.Z, 0.00001f );
        }

        private static void AssertQuaternionEqual( Quaternion expected, Quaternion actual )
        {
            var normalizedExpected = Quaternion.Normalize( expected );
            var normalizedActual = Quaternion.Normalize( actual );
            var dot = Math.Abs( Quaternion.Dot( normalizedExpected, normalizedActual ) );
            Assert.AreEqual( 1f, dot, 0.00001f );
        }
    }
}
