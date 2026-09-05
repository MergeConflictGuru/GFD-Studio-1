using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using GFDLibrary.Common;
using GFDLibrary.IO;

namespace GFDLibrary.Models
{
    public sealed class Model : Resource
    {
        public override ResourceType ResourceType => ResourceType.Model;

        private ModelFlags mFlags;
        public ModelFlags Flags
        {
            get => mFlags;
            set
            {
                mFlags = value;
                ValidateFlags();
            }
        }

        private List<Bone> mBones;
        public List<Bone> Bones
        {
            get => mBones;
            set
            {
                mBones = value;
                ValidateFlags();
            }
        }

        private BoundingBox? mBoundingBox;
        public BoundingBox? BoundingBox
        {
            get => mBoundingBox;
            set
            {
                mBoundingBox = value;
                ValidateFlags();
            }
        }

        private BoundingSphere? mBoundingSphere;
        public BoundingSphere? BoundingSphere
        {
            get => mBoundingSphere;
            set
            {
                mBoundingSphere = value;
                ValidateFlags();
            }
        }

        public Node RootNode { get; set; }

        public byte Field100_10 { get; set; }

        public IEnumerable<Node> Nodes
        {
            get
            {
                IEnumerable<Node> RecursivelyAddToList( Node node )
                {
                    yield return node;
                    foreach ( var childNode in node.Children )
                    {
                        foreach ( var childChildNode in RecursivelyAddToList( childNode ) )
                            yield return childChildNode;
                    }
                }

                return RecursivelyAddToList( RootNode );
            }
        }

        public IEnumerable<Mesh> Meshes
            => Nodes.SelectMany( n => n.Meshes );

        public Model()
        {         
        }

        public Model(uint version) : base(version)
        {
        }

        protected override void ReadCore( ResourceReader reader )
        {
            var flags = ( ModelFlags ) reader.ReadInt32();

            if ( flags.HasFlag( ModelFlags.HasSkinning ) )
            {
                int boneCount = reader.ReadInt32();

                var inverseBindMatrices = new Matrix4x4[boneCount];
                var boneToNodeIndices = new ushort[boneCount];
                for ( int i = 0; i < boneCount; i++ )
                    inverseBindMatrices[i] = reader.ReadMatrix4x4();

                for ( int i = 0; i < boneCount; i++ )
                    boneToNodeIndices[i] = reader.ReadUInt16();

                Bones = new List<Bone>( boneCount );
                for ( int i = 0; i < boneCount; i++ )
                    Bones.Add( new Bone( boneToNodeIndices[ i ], inverseBindMatrices[ i ] ) );
                if ( Version >= 0x2040001 )
                    Field100_10 = reader.ReadByte();
            }

            if ( flags.HasFlag( ModelFlags.HasBoundingBox ) )
                BoundingBox = reader.ReadBoundingBox();

            if ( flags.HasFlag( ModelFlags.HasBoundingSphere ) )
                BoundingSphere = reader.ReadBoundingSphere();

            RootNode = Node.ReadRecursive( reader, Version );
            Flags = flags;
        }

        protected override void WriteCore( ResourceWriter writer )
        {
            writer.WriteInt32( ( int ) Flags );

            if ( Flags.HasFlag( ModelFlags.HasSkinning ) )
            {
                writer.WriteInt32( Bones.Count );

                foreach ( var bone in Bones )
                    writer.WriteMatrix4x4( bone.InverseBindMatrix );

                foreach ( var bone in Bones )
                    writer.WriteUInt16( bone.NodeIndex );

                if ( Version >= 0x2040001 )
                    writer.WriteByte( Field100_10 );
            }

            if ( Flags.HasFlag( ModelFlags.HasBoundingBox ) )
                writer.WriteBoundingBox( BoundingBox.Value );

            if ( Flags.HasFlag( ModelFlags.HasBoundingSphere ) )
                writer.WriteBoundingSphere( BoundingSphere.Value );

            Node.WriteRecursive( writer, RootNode );
        }

        public Node GetNode( int nodeIndex )
        {
            var i = 0;
            return Nodes.FirstOrDefault( node => i++ == nodeIndex );
        }

        public void ReplaceWith( Model other )
        {
            // Remove geometries from this scene
            RemoveGeometryAttachments();

            Bones = other.Bones;
            BoundingBox = other.BoundingBox;
            BoundingSphere = other.BoundingSphere;
            Flags = other.Flags;

            var otherNodes = other.Nodes.ToList();

            // Replace common nodes and get the unique nodes
            var uniqueNodes = ReplaceCommonNodesAndGetUniqueNodes( otherNodes );

            // Remove nodes that dont have attachments
            uniqueNodes.RemoveAll( x => !x.HasAttachments );

            // Fix unique nodes
            FixUniqueNodes( other.RootNode, otherNodes, uniqueNodes );

            // Add unique nodes to root.
            foreach ( var uniqueNode in uniqueNodes )
                RootNode.AddChildNode( uniqueNode );

            // Rebuild matrix palette
            RebuildBonePalette( otherNodes );
        }

        /// <summary>
        /// Adds the geometry from another model to this model while keeping the
        /// geometry that is already present. This is used for games that store a
        /// character's body, face, and hair as separate model files.
        /// </summary>
        public void MergeWith( Model other )
        {
            if ( other?.RootNode == null )
                return;

            var thisNodes = Nodes.ToList();
            var otherNodes = other.Nodes.ToList();
            var otherToThisNodes = new Dictionary<Node, Node>();
            var meshBoneNodes = new Dictionary<Mesh, Node[]>();

            if ( Bones != null )
            {
                foreach ( var node in thisNodes )
                {
                    foreach ( var mesh in node.Meshes )
                        meshBoneNodes[mesh] = Bones.Select( bone => thisNodes[bone.NodeIndex] ).ToArray();
                }
            }

            if ( other.Bones != null )
            {
                foreach ( var node in otherNodes )
                {
                    foreach ( var mesh in node.Meshes )
                        meshBoneNodes[mesh] = other.Bones.Select( bone => otherNodes[bone.NodeIndex] ).ToArray();
                }
            }

            otherToThisNodes[other.RootNode] = RootNode;

            foreach ( var otherNode in otherNodes )
            {
                if ( otherNode == other.RootNode )
                    continue;

                var thisNode = thisNodes.FirstOrDefault( node => node.Name == otherNode.Name );
                if ( thisNode == null )
                    continue;

                otherToThisNodes[otherNode] = thisNode;
                Matrix4x4.Invert( thisNode.WorldTransform, out var thisNodeWorldTransformInv );
                var offsetMatrix = otherNode.WorldTransform * thisNodeWorldTransformInv;

                foreach ( var attachment in otherNode.Attachments.ToList() )
                {
                    if ( attachment.Type == NodeAttachmentType.Epl )
                        continue;

                    if ( attachment.Type == NodeAttachmentType.Mesh )
                        TransformMesh( attachment.GetValue<Mesh>(), offsetMatrix );

                    thisNode.Attachments.Add( attachment );
                }

                foreach ( var property in otherNode.Properties )
                    thisNode.Properties[property.Key] = property.Value;
            }

            // Move each hierarchy that exists only in the incoming model under
            // this model's root while preserving its world-space transform.
            foreach ( var otherNode in otherNodes )
            {
                if ( otherNode == other.RootNode || otherToThisNodes.ContainsKey( otherNode ) )
                    continue;

                if ( otherNode.Parent != null && !otherToThisNodes.ContainsKey( otherNode.Parent ) )
                    continue;

                var worldTransform = otherNode.WorldTransform;
                var originalParent = otherNode.Parent;
                originalParent?.RemoveChildNode( otherNode );

                // Keep incoming geometry under the corresponding animated node.
                // Face meshes commonly live directly below "head", while hair
                // meshes are below "mesh_grp". Moving them to the model root
                // preserves the bind pose but disconnects them from animation.
                if ( originalParent != null && otherToThisNodes.TryGetValue( originalParent, out var mappedParent ) )
                {
                    Matrix4x4.Invert( mappedParent.WorldTransform, out var mappedParentWorldInv );
                    otherNode.LocalTransform = worldTransform * mappedParentWorldInv;
                    mappedParent.AddChildNode( otherNode );
                }
                else
                {
                    otherNode.LocalTransform = worldTransform;
                    RootNode.AddChildNode( otherNode );
                }

                // Allow descendants of an unmatched hierarchy to be attached
                // beneath this newly moved node on the next pass.
                otherToThisNodes[otherNode] = otherNode;
            }

            // Rebuild one palette for both the original and incoming meshes.
            // The captured node references let us translate each file's old bone
            // indices after their node hierarchies have been combined.
            var finalNodes = Nodes.ToList();
            var combinedBones = new List<Bone>();
            foreach ( var node in finalNodes )
            {
                var nodeInverseWorld = Matrix4x4.Invert( node.WorldTransform, out var inverseWorld )
                    ? inverseWorld
                    : Matrix4x4.Identity;

                foreach ( var mesh in node.Meshes )
                {
                    if ( mesh.VertexWeights == null || !meshBoneNodes.TryGetValue( mesh, out var sourceBoneNodes ) )
                        continue;

                    foreach ( var weight in mesh.VertexWeights )
                    {
                        for ( var i = 0; i < weight.Indices.Length; i++ )
                        {
                            if ( weight.Weights[i] == 0 || weight.Indices[i] >= sourceBoneNodes.Length )
                                continue;

                            var sourceBoneNode = sourceBoneNodes[weight.Indices[i]];
                            if ( otherToThisNodes.TryGetValue( sourceBoneNode, out var mappedBoneNode ) )
                                sourceBoneNode = mappedBoneNode;

                            var boneNodeIndex = finalNodes.IndexOf( sourceBoneNode );
                            if ( boneNodeIndex < 0 )
                                boneNodeIndex = finalNodes.IndexOf( RootNode );

                            var bindMatrix = sourceBoneNode.WorldTransform * nodeInverseWorld;
                            Matrix4x4.Invert( bindMatrix, out var inverseBindMatrix );
                            var newBoneIndex = combinedBones.FindIndex( bone =>
                                bone.NodeIndex == boneNodeIndex && bone.InverseBindMatrix.Equals( inverseBindMatrix ) );

                            if ( newBoneIndex < 0 )
                            {
                                combinedBones.Add( new Bone( (ushort)boneNodeIndex, inverseBindMatrix ) );
                                newBoneIndex = combinedBones.Count - 1;
                            }

                            weight.Indices[i] = (ushort)newBoneIndex;
                        }
                    }
                }
            }

            if ( combinedBones.Count > 0 )
                Bones = combinedBones;
        }

        private static void TransformMesh( Mesh mesh, Matrix4x4 transform )
        {
            if ( mesh?.Vertices != null )
            {
                for ( var i = 0; i < mesh.Vertices.Length; i++ )
                {
                    var position = mesh.Vertices[i];
                    var transformedPosition = Vector3.Transform( position, transform );
                    mesh.Vertices[i] = transformedPosition;

                    if ( mesh.MorphTargets != null )
                    {
                        foreach ( var morphTarget in mesh.MorphTargets )
                        {
                            morphTarget.Vertices[i] = Vector3.Transform(
                                position + morphTarget.Vertices[i], transform ) - transformedPosition;
                        }
                    }
                }
            }

            if ( mesh?.Normals != null )
            {
                for ( var i = 0; i < mesh.Normals.Length; i++ )
                    mesh.Normals[i] = Vector3.TransformNormal( mesh.Normals[i], transform );
            }
        }

        private List<Node> ReplaceCommonNodesAndGetUniqueNodes( IEnumerable<Node> otherNodes )
        {
            var uniqueNodes = new List<Node>();

            foreach ( var otherNode in otherNodes )
            {
                if ( otherNode.Name == "RootNode" || ( otherNode.Parent == null || otherNode.Parent == otherNodes.First() ) && otherNode.Name.EndsWith( "_root" ) )
                {
                    continue;
                }

                if ( !Nodes.Any( x => x.Name == "Bip01 雜ｳ霍｡" ) )
                {
                    // Hacks to fix enemy/persona models
                    if ( otherNode.Name == "Bip01 閼頑､・" )
                        otherNode.Name = "Bip01 Spine";
                    else if ( otherNode.Parent != null && otherNode.Parent.Name == "Bip01 Spine" && otherNode.Name == "Bip01 閼頑､・" )
                        otherNode.Name = "Bip01 Spine1";
                    else if ( otherNode.Name == "Bip01 鬥・" )
                        otherNode.Name = "Bip01 Neck";
                    else if ( otherNode.Name == "Bip01 雜ｳ霍｡" )
                        otherNode.Name = "Bip01 Footsteps";
                }

                var thisNode = Nodes.SingleOrDefault( x => x.Name.Equals( otherNode.Name ) );

                if ( thisNode == null )
                {
                    // Node not present, can't merge
                    uniqueNodes.Add( otherNode );
                    continue;
                }

                // Merge attachments
                if ( otherNode.HasAttachments )
                {
                    Matrix4x4.Invert( thisNode.WorldTransform, out var thisNodeWorldTransformInv );
                    var offsetMatrix = otherNode.WorldTransform * thisNodeWorldTransformInv;

                    foreach ( var attachment in otherNode.Attachments )
                    {
                        switch ( attachment.Type )
                        {
                            case NodeAttachmentType.Mesh:
                                {
                                    var mesh = attachment.GetValue<Mesh>();

                                    for ( int i = 0; i < mesh.Vertices.Length; i++ )
                                    {
                                        var position = mesh.Vertices[ i ];
                                        var newPosition = mesh.Vertices[ i ] = Vector3.Transform( position, offsetMatrix );

                                        if ( mesh.MorphTargets != null )
                                        {
                                            foreach ( var morphTarget in mesh.MorphTargets )
                                            {
                                                Trace.Assert( morphTarget.VertexCount == mesh.VertexCount );
                                                morphTarget.Vertices[ i ] = Vector3.Transform( ( position + morphTarget.Vertices[ i ] ), offsetMatrix ) - newPosition;
                                            }
                                        }
                                    }

                                    if ( mesh.Normals != null )
                                    {
                                        for ( int i = 0; i < mesh.Normals.Length; i++ )
                                            mesh.Normals[i] = Vector3.TransformNormal( mesh.Normals[i], offsetMatrix );
                                    }
                                }
                                break;

                            case NodeAttachmentType.Epl:
                                continue;

                            case NodeAttachmentType.Light:
                                if ( thisNode.Attachments.Any( x => x.Type == NodeAttachmentType.Light ) )
                                {
                                    // Don't replace lights, likely not what we want to do
                                    continue;
                                }
                                break;
                        }

                        thisNode.Attachments.Add( attachment );
                    }
                }

                // Replace properties
                foreach ( var property in otherNode.Properties )
                    thisNode.Properties[property.Key] = property.Value;
            }

            return uniqueNodes;
        }

        private void FixUniqueNodes( Node otherRootNode, List<Node> otherNodes, List<Node> uniqueNodes )
        {
            foreach ( var uniqueNode in uniqueNodes.ToList() )
            {
                if ( uniqueNode.Parent == otherRootNode )
                    continue;

                // Find the last unique node in the hierarchy chain (going up the hierarchy)
                var lastUniqueNode = uniqueNode;
                while ( true )
                {
                    var parent = lastUniqueNode.Parent;
                    if ( parent == null || parent == otherRootNode || Nodes.SingleOrDefault( x => x.Name.Equals( parent.Name ) ) != null )
                        break;

                    lastUniqueNode = parent;
                }

                // Get unweighted geometries
                var unweightedGeometries = uniqueNode.Attachments.Where( x => x.Type == NodeAttachmentType.Mesh )
                                                     .Select( x => x.GetValue<Mesh>() ).Where( x => x.VertexWeights == null ).ToList();

                if ( unweightedGeometries.Any() )
                {
                    // If we have unweighted geometries, we have to assign vertex weights to them so that they
                    // properly animate.
                    // The node we are going to assign the weights to is the shared ancestor (between this model and the replacement one)
                    // in the hopes that it will work out.

                    // Find the bone index of this node
                    int lastUniqueNodeIndex = -1;
                    for ( int i = 0; i < otherNodes.Count; i++ )
                    {
                        if ( otherNodes[i].Name == lastUniqueNode.Parent.Name )
                        {
                            lastUniqueNodeIndex = i;
                            break;
                        }
                    }

                    Trace.Assert( lastUniqueNodeIndex != -1 );

                    if ( Bones == null )
                    {
                        Bones = new List<Bone>();
                    }

                    int boneIndex = Bones.FindIndex( x => x.NodeIndex == lastUniqueNodeIndex );

                    if ( boneIndex == -1 )
                    {
                        // Node wasn't used as a bone, so we add it
                        // TODO: This is a lazy hack. This should be done during the Bones fixup
                        boneIndex = Bones.Count;
                        Bones.Add( new Bone( ( ushort ) lastUniqueNodeIndex, Matrix4x4.Identity ) );
                    }

                    // Set vertex weights
                    foreach ( var geometry in unweightedGeometries )
                    {
                        geometry.VertexWeights = new VertexWeight[geometry.VertexCount];
                        for ( int i = 0; i < geometry.VertexWeights.Length; i++ )
                        {
                            ref var weight = ref geometry.VertexWeights[i];
                            weight.Indices = new ushort[4];
                            weight.Indices[0] = (ushort)boneIndex;
                            weight.Weights = new float[4];
                            weight.Weights[0] = 1f;
                        }
                    }
                }

                //// Fix morphs
                //var morphs = uniqueNode.Attachments.Where( x => x.Type == NodeAttachmentType.Morph ).Select( x => x.GetValue<Morph>() );
                //foreach ( var morph in morphs )
                //{
                //    // All unique nodes get assigned to the root node
                //    morph.NodeName = "RootNode";
                //}

                // Fix transform for the node
                var worldTransform = uniqueNode.WorldTransform;
                uniqueNode.Parent?.RemoveChildNode( uniqueNode );
                uniqueNode.LocalTransform = worldTransform;
            }
        }

        private void RebuildBonePalette( List<Node> otherNodes )
        {
            var uniqueBones = new List<Bone>();

            // Recalculate inverse bind matrices & update bone indices
            var nodes = Nodes.ToList();
            foreach ( var node in nodes )
            {
                if ( !node.HasAttachments )
                    continue;

                Matrix4x4.Invert( node.WorldTransform, out var nodeInvWorldTransform );

                foreach ( var geometry in node.Attachments.Where( x => x.Type == NodeAttachmentType.Mesh ).Select( x => x.GetValue<Mesh>() )
                                              .Where( x => x.VertexWeights != null ) )
                {
                    foreach ( var weight in geometry.VertexWeights )
                    {
                        for ( int i = 0; i < weight.Indices.Length; i++ )
                        {
                            var boneIndex = weight.Indices[i];
                            var boneWeight = weight.Weights[i];
                            if ( boneWeight == 0 )
                                continue;

                            var otherNodeIndex = Bones[boneIndex].NodeIndex;
                            var otherBoneNode = otherNodes[otherNodeIndex];

                            var thisBoneNode = nodes.FirstOrDefault( x => x.Name == otherBoneNode.Name );
                            if ( thisBoneNode == null )
                            {
                                // Find parent that does exist
                                var curOtherBoneNode = otherBoneNode.Parent;
                                while ( thisBoneNode == null && curOtherBoneNode != null )
                                {
                                    thisBoneNode = nodes.FirstOrDefault( x => x.Name == curOtherBoneNode.Name );
                                    curOtherBoneNode = curOtherBoneNode.Parent;
                                }

                                if ( thisBoneNode == null )
                                    thisBoneNode = RootNode;
                            }

                            var boneTransform = thisBoneNode.WorldTransform;

                            // Attempt to fix spaghetti fingers
                            //if ( thisBoneNode.Name.Contains( "Finger" ) || thisBoneNode.Name.Contains( "Hand" ) ||
                            //     thisBoneNode.Name.Contains( "hand" ) )
                            //    boneTransform = otherBoneNode.WorldTransform;

                            var thisNodeIndex = nodes.IndexOf( thisBoneNode );
                            Trace.Assert( thisNodeIndex != -1 );
                            var bindMatrix = boneTransform * nodeInvWorldTransform;
                            Matrix4x4.Invert( bindMatrix, out var inverseBindMatrix );
                            
                            var newBoneIndex =
                                uniqueBones.FindIndex( x => x.NodeIndex == thisNodeIndex && x.InverseBindMatrix.Equals( inverseBindMatrix ) );

                            if ( newBoneIndex == -1 )
                            {
                                // Add if unique
                                uniqueBones.Add( new Bone( (ushort)thisNodeIndex, inverseBindMatrix ) );
                                newBoneIndex = uniqueBones.Count - 1;
                            }

                            // Update bone index
                            weight.Indices[ i ] = (ushort)newBoneIndex;
                        }
                    }
                }
            }

            Bones = uniqueBones;
        }

        private void RemoveGeometryAttachments()
        {
            foreach ( var node in Nodes )
            {
                if ( node.HasAttachments )
                    foreach ( var geometryAttachment in node.Attachments.Where( x => x.Type == NodeAttachmentType.Mesh ).ToList() )
                        node.Attachments.Remove( geometryAttachment );
            }
        }

        private void ValidateFlags()
        {
            if ( Bones == null || Bones.Count == 0 )
                mFlags &= ~ModelFlags.HasSkinning;
            else
                mFlags |= ModelFlags.HasSkinning;

            if ( BoundingBox == null )
                mFlags &= ~ModelFlags.HasBoundingBox;
            else
                mFlags |= ModelFlags.HasBoundingBox;

            if ( BoundingSphere == null )
                mFlags &= ~ModelFlags.HasBoundingSphere;
            else
                mFlags |= ModelFlags.HasBoundingSphere;
        }
    }

    [Flags]
    public enum ModelFlags
    {
        HasBoundingBox    = 1 << 0,
        HasBoundingSphere = 1 << 1,
        HasSkinning       = 1 << 2,
        HasMorphs         = 1 << 3
    }
}
