using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using GFDLibrary.Common;
using GFDLibrary.Models;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using Vector3 = System.Numerics.Vector3;

namespace GFDLibrary.Rendering.OpenGL
{
    public class GLMesh : IDisposable
    {
        public Mesh Mesh { get; }

        public GLVertexArray VertexArray { get; }

        /// <summary>
        /// The vertex positions currently uploaded for this mesh. For skinned
        /// meshes these are rebuilt from the current animation pose before the
        /// mesh is drawn, so consumers can calculate an animated world bound.
        /// </summary>
        public Vector3[] VertexPositions { get; private set; }

        /// <summary>
        /// Bounds for the vertex positions currently uploaded for this mesh.
        /// Skinned meshes get a new value whenever their animated vertex buffer
        /// is rebuilt, while static meshes reuse the source mesh bounds.
        /// The value is consumed by animated viewport helpers after Draw.
        /// </summary>
        public BoundingBox? VertexBounds { get; private set; }

        public GLBaseMaterial Material { get; }

        public bool IsVisible { get; }

        public GLMesh( GLVertexArray vertexArray, GLBaseMaterial material, bool isVisible )
        {
            Mesh = null;
            VertexPositions = null;
            VertexBounds = null;
            VertexArray = vertexArray;
            Material = material;
            IsVisible = isVisible;
        }

        public GLMesh( Mesh mesh, Matrix4x4 modelMatrix, List<Bone> bones, List<GLNode> nodes, Dictionary<string, GLBaseMaterial> materials )
        {
            Mesh = mesh;

            var vertices = mesh.Vertices;
            var normals = mesh.Normals;

            if ( mesh.VertexWeights != null )
            {
                vertices = new Vector3[mesh.VertexCount];
                normals = null;

                if ( mesh.Normals != null )
                    normals = new Vector3[mesh.VertexCount];

                Matrix4x4.Invert( modelMatrix, out var modelMatrixInv );

                for ( int i = 0; i < mesh.VertexCount; i++ )
                {
                    var position = mesh.Vertices[i];
                    var normal = mesh.Normals?[i] ?? Vector3.Zero;

                    var newPosition = Vector3.Zero;
                    var newNormal = Vector3.Zero;
                    for ( int j = 0; j < mesh.VertexWeights[i].Weights.Length; j++ )
                    {
                        var weight = mesh.VertexWeights[i].Weights[j];
                        if ( weight == 0 )
                            continue;

                        var boneIndex = mesh.VertexWeights[i].Indices[j];
                        TransformVertex( bones, nodes, position, normal, ref newPosition, ref newNormal, weight, boneIndex );
                    }

                    vertices[i] = Vector3.Transform( newPosition, modelMatrixInv );

                    if ( normals != null )
                        normals[i] =
                            Vector3.Normalize( Vector3.TransformNormal( newNormal, modelMatrixInv ) );
                }
            }

            VertexPositions = vertices;
            VertexBounds = mesh.VertexWeights != null
                ? BoundingBox.Calculate( vertices )
                : mesh.BoundingBox ?? ( vertices.Length > 0 ? BoundingBox.Calculate( vertices ) : null );

            var indices = new uint[mesh.Triangles.Length * 3];
            for ( int i = 0; i < mesh.Triangles.Length; i++ )
            {
                indices[( i * 3 ) + 0] = mesh.Triangles[i].A;
                indices[( i * 3 ) + 1] = mesh.Triangles[i].B;
                indices[( i * 3 ) + 2] = mesh.Triangles[i].C;
            }

            VertexArray = new GLVertexArray( vertices, normals, 
                new[] { mesh.TexCoordsChannel0, mesh.TexCoordsChannel1, mesh.TexCoordsChannel2 }, 
                new[] { mesh.ColorChannel0, mesh.ColorChannel1, mesh.ColorChannel2 },
                indices, PrimitiveType.Triangles );

            // material
            if ( mesh.MaterialName != null && materials != null )
            {
                if ( materials.TryGetValue( mesh.MaterialName, out var material ) )
                {
                    Material = material;
                }
                else
                {
                    Trace.TraceError( $"Mesh referenced material \"{mesh.MaterialName}\" which does not exist in the model" );
                    Material = new GLP5Material();
                }
            }
            else
            {
                Material = new GLP5Material();
            }

            IsVisible = true;
        }

        /// <summary>
        /// Calculates the local-space bounds for the mesh using the current bone
        /// transforms without creating or replacing its OpenGL buffers. This is
        /// used when a caller needs to inspect several future animation poses.
        /// </summary>
        public BoundingBox? CalculateVertexBounds( List<Bone> bones, List<GLNode> nodes, Matrix4x4 modelMatrix )
        {
            if ( Mesh == null )
                return VertexBounds;

            if ( Mesh.VertexWeights == null )
                return VertexBounds ?? Mesh.BoundingBox ??
                       ( Mesh.Vertices.Length > 0 ? BoundingBox.Calculate( Mesh.Vertices ) : null );

            if ( !Matrix4x4.Invert( modelMatrix, out var modelMatrixInv ) )
                return null;

            var minimum = new Vector3( float.PositiveInfinity );
            var maximum = new Vector3( float.NegativeInfinity );
            for ( var i = 0; i < Mesh.VertexCount; i++ )
            {
                var position = Mesh.Vertices[i];
                var normal = Mesh.Normals?[i] ?? Vector3.Zero;
                var newPosition = Vector3.Zero;
                var newNormal = Vector3.Zero;
                for ( var j = 0; j < Mesh.VertexWeights[i].Weights.Length; j++ )
                {
                    var weight = Mesh.VertexWeights[i].Weights[j];
                    if ( weight == 0 )
                        continue;

                    var boneIndex = Mesh.VertexWeights[i].Indices[j];
                    TransformVertex( bones, nodes, position, normal, ref newPosition, ref newNormal, weight, boneIndex );
                }

                var transformedPosition = Vector3.Transform( newPosition, modelMatrixInv );
                minimum = Vector3.Min( minimum, transformedPosition );
                maximum = Vector3.Max( maximum, transformedPosition );
            }

            return new BoundingBox( minimum, maximum );
        }

        private static void TransformVertex( List<Bone> bones, List<GLNode> nodes, Vector3 position, Vector3 normal, ref Vector3 newPosition, ref Vector3 newNormal, float weight, ushort boneIndex )
        {
            var bone = bones[boneIndex];
            var boneNode = nodes[bone.NodeIndex];
            var bindMatrix = boneNode.WorldTransform;
            newPosition += Vector3.Transform( Vector3.Transform( position, bone.InverseBindMatrix ),
                                                              bindMatrix * weight );

            newNormal += Vector3.TransformNormal( Vector3.TransformNormal( normal, bone.InverseBindMatrix ),
                                                                 bindMatrix * weight );
        }

        public void Draw( Matrix4 modelMatrix, GLShaderProgram shaderProgram )
        {
            //if ( Material.METAPHOR_DistortionMaterialTest )
            //    return;
            shaderProgram.SetUniform( "uModel", modelMatrix);
            Material.Bind( shaderProgram );
            shaderProgram.Check();
            VertexArray.Draw();
            Material.Unbind( shaderProgram );
        }

        public void UpdateAnimatedVertices( List<Bone> bones, List<GLNode> nodes, Matrix4x4 modelMatrix )
        {
            if ( Mesh == null || Mesh.VertexWeights == null )
                return;

            var vertices = new Vector3[Mesh.VertexCount];
            var normals = Mesh.Normals != null ? new Vector3[Mesh.VertexCount] : null;
            Matrix4x4.Invert( modelMatrix, out var modelMatrixInv );

            for ( var i = 0; i < Mesh.VertexCount; i++ )
            {
                var position = Mesh.Vertices[i];
                var normal = Mesh.Normals?[i] ?? Vector3.Zero;
                var newPosition = Vector3.Zero;
                var newNormal = Vector3.Zero;
                for ( var j = 0; j < Mesh.VertexWeights[i].Weights.Length; j++ )
                {
                    var weight = Mesh.VertexWeights[i].Weights[j];
                    if ( weight == 0 )
                        continue;

                    var boneIndex = Mesh.VertexWeights[i].Indices[j];
                    TransformVertex( bones, nodes, position, normal, ref newPosition, ref newNormal, weight, boneIndex );
                }

                vertices[i] = Vector3.Transform( newPosition, modelMatrixInv );
                if ( normals != null )
                    normals[i] = Vector3.Normalize( Vector3.TransformNormal( newNormal, modelMatrixInv ) );
            }

            VertexPositions = vertices;
            VertexBounds = BoundingBox.Calculate( vertices );
            VertexArray.UpdatePositions( vertices );
            if ( normals != null )
                VertexArray.UpdateNormals( normals );
        }

        #region IDisposable Support
        private bool mDisposed; // To detect redundant calls

        protected virtual void Dispose( bool disposing )
        {
            if ( !mDisposed )
            {
                if ( disposing )
                {
                    VertexArray.Dispose();
                }

                mDisposed = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose( true );
        }
        #endregion
    }
}
