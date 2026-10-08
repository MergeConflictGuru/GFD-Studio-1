using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using GFDLibrary.Common;
using GFDLibrary.Models;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;

namespace GFDLibrary.Rendering.OpenGL
{
    public class GLMesh : IDisposable
    {
        public Mesh Mesh { get; }
        private GLSkinningPalette _palette;
        private List<Bone> _bones;
        private List<GLNode> _nodes;
        private Matrix4x4 _modelMatrix, _modelInverse;
        private GLVertexAttributeBuffer<Vector2> _skinRanges;
        private int _influenceBuffer, _influenceTexture;
        private int _bufferedNode=-1;
        private bool _bufferedPose;
        private bool _gpuSkinning, _gpuVertexData=true, _cpuPoseReady;
        private readonly Dictionary<int,BoundingBox> _boneBounds=new();
        private void CreateSkinData()
        {
            if(Mesh?.VertexWeights==null)return;
            var ranges=new Vector2[Mesh.VertexCount];var influences=new List<Vector2>();
            for(int v=0;v<Mesh.VertexCount;v++)
            {
                var weights=Mesh.VertexWeights[v];int start=influences.Count;
                for(int j=0;j<weights.Weights.Length;j++)
                {
                    if(weights.Weights[j]==0)continue;
                    int bone=weights.Indices[j];influences.Add(new Vector2(bone,weights.Weights[j]));
                    var pos=Mesh.Vertices[v];
                    _boneBounds[bone]=_boneBounds.TryGetValue(bone,out var b)?new BoundingBox(Vector3.Min(b.Min,pos),Vector3.Max(b.Max,pos)):new BoundingBox(pos,pos);
                }
                ranges[v]=new Vector2(start,influences.Count-start);
            }
            GL.BindVertexArray(VertexArray.Id);
            _skinRanges=new GLVertexAttributeBuffer<Vector2>(ranges,3,2,VertexAttribPointerType.Float);
            _influenceBuffer=GL.GenBuffer();_influenceTexture=GL.GenTexture();
            GL.BindBuffer(BufferTarget.TextureBuffer,_influenceBuffer);
            var data=influences.Count>0?influences.ToArray():new[]{Vector2.Zero};
            GL.BufferData(BufferTarget.TextureBuffer,data.Length*8,data,BufferUsageHint.StaticDraw);
            GL.BindTexture(TextureTarget.TextureBuffer,_influenceTexture);
            GL.TexBuffer(TextureBufferTarget.TextureBuffer,SizedInternalFormat.Rg32f,_influenceBuffer);
        }
        public void PrepareGpuSkinning(GLSkinningPalette palette,List<Bone> bones,List<GLNode> nodes,Matrix4x4 modelMatrix,bool calculateBounds=true)
        {
            _bufferedNode=-1;_bufferedPose=false;
            if(Mesh?.VertexWeights==null)return;
            _palette=palette;_bones=bones;_nodes=nodes;_modelMatrix=modelMatrix;
            Matrix4x4.Invert(modelMatrix,out _modelInverse);_gpuSkinning=true;_cpuPoseReady=false;
            if(!_gpuVertexData){VertexArray.UpdatePositions(Mesh.Vertices);VertexArray.UpdateNormals(Mesh.Normals);_gpuVertexData=true;}
            if(!calculateBounds)return;
            var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            foreach(var item in _boneBounds)
            {
                var matrix=palette.Matrices[item.Key]*_modelInverse;var b=item.Value;
                for(int x=0;x<2;x++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)
                {
                    var p=Vector3.Transform(new Vector3(x==0?b.Min.X:b.Max.X,y==0?b.Min.Y:b.Max.Y,z==0?b.Min.Z:b.Max.Z),matrix);
                    min=Vector3.Min(min,p);max=Vector3.Max(max,p);
                }
            }
            VertexBounds=new BoundingBox(min,max);
        }
        public void PrepareBufferedSkinning(GLSkinningPalette palette,int node)
        {
            _bufferedPose=true;_bufferedNode=Mesh?.VertexWeights==null?node:-1;
            _palette=palette;_gpuSkinning=Mesh?.VertexWeights!=null;_modelInverse=Matrix4x4.Identity;
            if(!_gpuVertexData && Mesh!=null){VertexArray.UpdatePositions(Mesh.Vertices);VertexArray.UpdateNormals(Mesh.Normals);_gpuVertexData=true;}
        }
        public void PrepareCpuPicking()
        {
            if(_gpuSkinning && !_cpuPoseReady){UpdateAnimatedVertices(_bones,_nodes,_modelMatrix,false);_cpuPoseReady=true;}
        }

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
            if(mesh.VertexWeights!=null){VertexArray.UpdatePositions(mesh.Vertices);VertexArray.UpdateNormals(mesh.Normals);CreateSkinData();}

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
            shaderProgram.Use();
            shaderProgram.SetUniform("uSkinning",_gpuSkinning);
            shaderProgram.SetUniform("uSkinPalette",10);
            shaderProgram.SetUniform("uSkinInfluences",11);
            shaderProgram.SetUniform("uSkinRigidNode",_bufferedNode);
            bool buffered=_bufferedNode>=0 || _gpuSkinning;
            shaderProgram.SetUniform("uSkinFrame",buffered?_palette.FrameOffset:0);
            shaderProgram.SetUniform("uSkinNextFrame",buffered?_palette.NextFrameOffset:0);
            shaderProgram.SetUniform("uSkinFrameBlend",buffered?_palette.FrameBlend:0f);
            shaderProgram.SetUniform("uSkinModelInverse",_modelInverse.ToOpenTK());
            if(buffered)
            {
                GL.ActiveTexture(TextureUnit.Texture10);GL.BindTexture(TextureTarget.TextureBuffer,_palette.Texture);
                GL.ActiveTexture(TextureUnit.Texture11);GL.BindTexture(TextureTarget.TextureBuffer,_influenceTexture);
            }
            GL.ActiveTexture(TextureUnit.Texture0);
            shaderProgram.SetUniform( "uModel", _bufferedPose ? Matrix4.Identity : modelMatrix);
            Material.Bind( shaderProgram );
            shaderProgram.Check();
            VertexArray.Draw();
            Material.Unbind( shaderProgram );
        }

        public void UpdateAnimatedVertices( List<Bone> bones, List<GLNode> nodes, Matrix4x4 modelMatrix, bool upload = true )
        {
            if(upload){_gpuSkinning=false;_bufferedNode=-1;_bufferedPose=false;}
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
            if(upload)
            {
                VertexArray.UpdatePositions(vertices);VertexArray.UpdateNormals(normals);_gpuVertexData=false;
            }
        }

        #region IDisposable Support
        private bool mDisposed; // To detect redundant calls

        protected virtual void Dispose( bool disposing )
        {
            if ( !mDisposed )
            {
                if ( disposing )
                {
                    _skinRanges?.Dispose();
                    if(_influenceTexture!=0)GL.DeleteTexture(_influenceTexture);
                    if(_influenceBuffer!=0)GL.DeleteBuffer(_influenceBuffer);
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

