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
                new[] { "RootNode", "Hips", "Spine", "LeftArm" },
                animation.Controllers.Select( controller => controller.TargetName ).ToArray() );
            Assert.AreEqual( 0, animation.Controllers[ 0 ].TargetId );
            Assert.AreEqual( 2, animation.Controllers[ 1 ].TargetId );
            Assert.AreEqual( 3, animation.Controllers[ 2 ].TargetId );
            Assert.AreEqual( 4, animation.Controllers[ 3 ].TargetId );
            Assert.AreEqual( ResourceVersion.Persona5Dancing, animation.Version );
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
