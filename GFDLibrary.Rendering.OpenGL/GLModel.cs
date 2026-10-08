using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using GFDLibrary.Animations;
using GFDLibrary.Common;
using GFDLibrary.Materials;
using GFDLibrary.Models;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace GFDLibrary.Rendering.OpenGL
{
    public class GLModel : IDisposable
    {
        public ModelPack ModelPack { get; }
        public bool UseGpuSkinning {get;set;}=true;
        private GLSkinningPalette _skinning;
        public bool UseBufferedAnimation {get;set;}
        private readonly Dictionary<Animation,GLAuthoredAnimation> _bufferedAnimations=new();
        private readonly Dictionary<Animation,Task<AuthoredAnimationKeys>> _bufferPreparations=new();
        private readonly CancellationTokenSource _bufferCancellation=new();
        private GLAuthoredPoseRenderer _authoredPoseRenderer;
        private long _bufferBytes,_bufferUse;
        private const long BufferedCacheBudget=2L*1024*1024*1024;
        public long BufferedUploads {get;private set;}
        public long BufferedDraws {get;private set;}
        public long BufferedCacheBytes => _bufferBytes;
        public long BufferedEvictions {get;private set;}
        private bool SelectBufferedAnimation(double time)
        {
            if(!_bufferedAnimations.TryGetValue(Animation,out var buffer))
            {
                if(!_bufferPreparations.TryGetValue(Animation,out var preparation))
                {
                    if(_bufferPreparations.Count>=2)return false;
                    var animation=Animation;var token=_bufferCancellation.Token;
                    preparation=Task.Run(()=>AuthoredAnimationKeys.Copy(ModelPack.Model,animation,token),token);
                    _bufferPreparations.Add(animation,preparation);
                }
                if(!preparation.IsCompleted)return false;
                UploadPreparedAnimation(Animation,preparation.GetAwaiter().GetResult());
                buffer=_bufferedAnimations[Animation];
            }
            buffer.LastUse=++_bufferUse;
            _authoredPoseRenderer ??=new GLAuthoredPoseRenderer(ModelPack.Model);
            _authoredPoseRenderer.Draw(buffer,time);
            for(int i=0;i<Nodes.Count;i++)
                if(Nodes[i].IsVisible)foreach(var mesh in Nodes[i].Meshes)
                    mesh.PrepareBufferedSkinning(_authoredPoseRenderer.Palette,ModelPack.Model.Bones.Count+i);
            BufferedDraws++;return true;
        }
        public void FinishBufferedPreparations()
        {
            foreach(var item in _bufferPreparations.Where(item=>item.Value.IsCompleted).ToArray())
                UploadPreparedAnimation(item.Key,item.Value.GetAwaiter().GetResult());
        }
        private void UploadPreparedAnimation(Animation animation,AuthoredAnimationKeys keys)
        {
            _bufferPreparations.Remove(animation);
            while(_bufferBytes+keys.Bytes>BufferedCacheBudget && _bufferedAnimations.Count>0)
            {
                var oldest=_bufferedAnimations.OrderBy(item=>item.Value.LastUse).First();
                _bufferBytes-=oldest.Value.Bytes;oldest.Value.Dispose();_bufferedAnimations.Remove(oldest.Key);BufferedEvictions++;
            }
            var buffer=new GLAuthoredAnimation(keys){LastUse=++_bufferUse};
            _bufferedAnimations.Add(animation,buffer);_bufferBytes+=buffer.Bytes;BufferedUploads++;
        }

        public List<GLNode> Nodes { get; }

        public Dictionary<string, GLBaseMaterial> Materials { get; }

        private sealed class ControllerBindings
        {
            private readonly AnimationController[] _controllers;
            private readonly string[] _names;
            private readonly TargetKind[] _kinds;
            public readonly Dictionary<string,AnimationController[]> ByNode;
            public ControllerBindings(Animation animation)
            {
                _controllers=animation.Controllers.ToArray();_names=_controllers.Select(c=>c.TargetName).ToArray();_kinds=_controllers.Select(c=>c.TargetKind).ToArray();
                ByNode=_controllers.Where(c=>c.TargetKind==TargetKind.Node && c.TargetName!=null).GroupBy(c=>c.TargetName,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            }
            public bool Matches(Animation animation)
            {
                if(animation.Controllers.Count!=_controllers.Length)return false;
                for(int i=0;i<_controllers.Length;i++)
                {
                    var controller=animation.Controllers[i];
                    if(!ReferenceEquals(controller,_controllers[i]) || controller.TargetKind!=_kinds[i] || controller.TargetName!=_names[i])return false;
                }
                return true;
            }
        }
        private readonly ConditionalWeakTable<Animation,ControllerBindings> _controllerBindings=new();
        private ControllerBindings Bindings(Animation animation)
        {
            if(_controllerBindings.TryGetValue(animation,out var bindings) && bindings.Matches(animation))return bindings;
            _controllerBindings.Remove(animation);bindings=new ControllerBindings(animation);_controllerBindings.Add(animation,bindings);return bindings;
        }
        public Animation Animation { get; private set; }

        public Animation BlendAnimation { get; private set; }

        public GLModel( ModelPack modelPack, MaterialTextureCreator textureCreator ) : this(modelPack,textureCreator,false) {}

        private GLModel(ModelPack modelPack,MaterialTextureCreator textureCreator,bool cpuPoseOnly)
        {
            ModelPack = modelPack;
            if(!cpuPoseOnly)_skinning=new GLSkinningPalette(modelPack.Model.Bones.Count);

            var nodes = modelPack.Model.Nodes.ToList();
            Nodes = nodes.Select( x => new GLNode( x ) ).ToList();

            if(cpuPoseOnly)
            {
                Materials=new Dictionary<string,GLBaseMaterial>();
                foreach(var node in Nodes)node.Parent=Nodes.FirstOrDefault(n=>n.Node==node.Node.Parent);
                return;
            }
            Materials = modelPack.Materials?.ToDictionary( x => x.Key, y => GLBaseMaterial.CreateGLMaterial( y.Value, textureCreator ) ) ??
                        new Dictionary<string, GLBaseMaterial>();

            foreach ( var glNode in Nodes )
            {
                glNode.Parent = Nodes.FirstOrDefault( x => x.Node == glNode.Node.Parent );
                if ( !glNode.Node.HasAttachments )
                    continue;

                foreach ( var attachment in glNode.Node.Attachments )
                {
                    if ( attachment.Type != NodeAttachmentType.Mesh )
                        continue;

                    var glMesh = new GLMesh( attachment.GetValue<Mesh>(), glNode.Node.WorldTransform, modelPack.Model.Bones, Nodes, Materials );
                    glNode.Meshes.Add( glMesh );
                }
            }
        }

        public void LoadAnimation( Animation animation )
        {
            Animation = animation;
            BlendAnimation = null;
            var bindings=Bindings(animation);

            foreach ( var glNode in Nodes )
            {
                glNode.Controllers.Clear();
                glNode.BlendControllers.Clear();
                if(bindings.ByNode.TryGetValue(glNode.Node.Name,out var controllers))glNode.Controllers.AddRange(controllers);
            }
        }

        public void LoadBlendAnimation( Animation animation )
        {
            BlendAnimation = animation;
            var bindings=Bindings(animation);

            foreach ( var glNode in Nodes )
            {
                glNode.BlendControllers.Clear();
                if(bindings.ByNode.TryGetValue(glNode.Node.Name,out var controllers))glNode.BlendControllers.AddRange(controllers);
            }
        }

        public void UnloadBlendAnimation()
        {
            BlendAnimation = null;

            foreach ( var glNode in Nodes )
                glNode.BlendControllers.Clear();
        }

        public void UnloadAnimation()
        {
            Animation = null;
            BlendAnimation = null;

            foreach ( var glNode in Nodes )
            {
                glNode.Controllers.Clear();
                glNode.BlendControllers.Clear();

                // Calculate current transform
                var transform = Matrix4x4.CreateFromQuaternion( glNode.Node.Rotation ) * Matrix4x4.CreateScale( glNode.Node.Scale );
                transform.Translation   = glNode.Node.Translation;
                glNode.CurrentTransform = transform;

                // Calculate world transform
                glNode.WorldTransform =
                    glNode.Parent == null ? glNode.CurrentTransform : glNode.CurrentTransform * glNode.Parent.WorldTransform;
            }

            _skinning.Update(ModelPack.Model.Bones,Nodes);
            foreach ( var glNode in Nodes )
            {
                foreach ( var glMesh in glNode.Meshes )
                    if(UseGpuSkinning)glMesh.PrepareGpuSkinning(_skinning,ModelPack.Model.Bones,Nodes,glNode.WorldTransform);
                    else glMesh.UpdateAnimatedVertices(ModelPack.Model.Bones,Nodes,glNode.WorldTransform);
            }
        }

        /// <summary>
        /// Updates node and bone transforms for an animation pose without
        /// rebuilding the GPU meshes. This keeps future-bound sampling CPU-only.
        /// </summary>
        public void UpdateAnimationPose( double animationTime )
        {
            if ( Animation != null )
                AnimateNodes( animationTime );
        }

        /// <summary>
        /// Calculates the visible model bounds for the current node and bone
        /// transforms without allocating replacement OpenGL buffers.
        /// </summary>
        public bool TryGetWorldBounds( out BoundingBox bounds )
        {
            var minimum = new Vector3( float.PositiveInfinity );
            var maximum = new Vector3( float.NegativeInfinity );
            var hasBounds = false;

            foreach ( var glNode in Nodes )
            {
                if ( !glNode.IsVisible )
                    continue;

                foreach ( var glMesh in glNode.Meshes )
                {
                    if ( !glMesh.IsVisible || glMesh.CalculateVertexBounds( ModelPack.Model.Bones, Nodes, glNode.WorldTransform ) is not { } meshBounds )
                        continue;

                    for ( var x = -1; x <= 1; x += 2 )
                    for ( var y = -1; y <= 1; y += 2 )
                    for ( var z = -1; z <= 1; z += 2 )
                    {
                        var localPoint = new Vector3(
                            x < 0 ? meshBounds.Min.X : meshBounds.Max.X,
                            y < 0 ? meshBounds.Min.Y : meshBounds.Max.Y,
                            z < 0 ? meshBounds.Min.Z : meshBounds.Max.Z );
                        var worldPoint = Vector3.Transform( localPoint, glNode.WorldTransform );
                        if ( !float.IsFinite( worldPoint.X ) || !float.IsFinite( worldPoint.Y ) || !float.IsFinite( worldPoint.Z ) )
                            continue;

                        minimum = Vector3.Min( minimum, worldPoint );
                        maximum = Vector3.Max( maximum, worldPoint );
                        hasBounds = true;
                    }
                }
            }

            bounds = new BoundingBox( minimum, maximum );
            return hasBounds;
        }

        private void UpdateAnimatedMeshes()
        {
            foreach ( var glNode in Nodes )
            {
                if ( !glNode.IsVisible )
                    continue;

                foreach ( var glMesh in glNode.Meshes )
                {
                    if(UseGpuSkinning)glMesh.PrepareGpuSkinning(_skinning,ModelPack.Model.Bones,Nodes,glNode.WorldTransform);
                    else glMesh.UpdateAnimatedVertices(ModelPack.Model.Bones,Nodes,glNode.WorldTransform);
                }
            }
        }

        private GLShaderProgram GetTargetShader(ShaderRegistry shaderRegistry, GLMesh glMesh, Matrix4 view, Matrix4 projection, HashSet<ResourceType> shaderPrograms )
        {
            if ( typeof( GLMetaphorMaterial ).IsInstanceOfType( glMesh.Material )
                && shaderRegistry.mMetaphorShaders.TryGetValue( ( (GLMetaphorMaterial)glMesh.Material ).ParameterSet.ResourceType, out var metaphorShader ) )
            {
                if ( !shaderPrograms.Contains( ((GLMetaphorMaterial)glMesh.Material).ParameterSet.ResourceType ) )
                {
                    metaphorShader.Use();
                    metaphorShader.SetUniform( "uView", view );
                    metaphorShader.SetUniform( "uProjection", projection );
                }
                return metaphorShader;
            }
            return shaderRegistry.mDefaultShader;
        }

        private bool ShouldMeshShowAsSelected( DrawContext context, GLMesh glMesh )
        {
            return context.SelectedMesh == glMesh.Mesh || context.SelectedMaterial?.Name == glMesh.Mesh.MaterialName;
        }

        public void Draw( DrawContext context )
        {
            if(!(UseBufferedAnimation && UseGpuSkinning && Animation!=null && BlendAnimation==null &&
                 SelectBufferedAnimation(context.AnimationTime)))
            {
                if(Animation!=null)UpdateAnimationPose(context.AnimationTime);
                if(UseGpuSkinning)_skinning.Update(ModelPack.Model.Bones,Nodes);
                UpdateAnimatedMeshes();
            }
            context.ShaderRegistry.mDefaultShader.Use();
            context.ShaderRegistry.mDefaultShader.SetUniform( "uView", context.Camera.View );
            context.ShaderRegistry.mDefaultShader.SetUniform( "uProjection", context.Camera.Projection );

            HashSet<ResourceType> shaderProgramsInUse = new();

            // List to hold transparent meshes and their world transforms
            List<Tuple<GLMesh, Matrix4, GLShaderProgram>> transparentMeshes = new();

            // Draw opaque objects first
            foreach ( var glNode in Nodes )
            {
                if ( !glNode.IsVisible )
                    continue;

                for ( var i = 0; i < glNode.Meshes.Count; i++ )
                {
                    var glMesh = glNode.Meshes[i];
                    GLShaderProgram targetShader = GetTargetShader( context.ShaderRegistry, glMesh, context.Camera.View, context.Camera.Projection, shaderProgramsInUse );
                    if ( !glMesh.Material.IsMaterialTransparent() ) // If opaque
                    {
                        if ( glMesh.Mesh != null )
                            targetShader.SetUniform( "uIsSelected", ShouldMeshShowAsSelected(context, glMesh ) );
                        glMesh.Draw( glNode.WorldTransform.ToOpenTK(), targetShader );
                    }
                    else // If transparent
                    {
                        transparentMeshes.Add( Tuple.Create( glMesh, glNode.WorldTransform.ToOpenTK(), targetShader ) );
                    }
                }
            }

            // Enable blending for transparent objects
            GL.Enable( EnableCap.Blend );

            // Sort transparent objects based on their distance from the camera
            transparentMeshes.Sort( ( a, b ) => OpenTK.Mathematics.Vector3.Distance( context.Camera.Translation, b.Item2.ExtractTranslation() ).CompareTo( OpenTK.Mathematics.Vector3.Distance( context.Camera.Translation, a.Item2.ExtractTranslation() ) ) );

            // Disable depth mask
            GL.DepthMask( false );

            // Then draw transparent objects
            foreach ( (var glMesh, var worldTransform, var shaderProgram) in transparentMeshes )
            {
                switch ( glMesh.Material.DrawMethod )
                {
                    case 1:
                        GL.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha );
                        break;
                    case 2:
                        GL.BlendFuncSeparate( BlendingFactorSrc.SrcAlpha, BlendingFactorDest.One, // RGB blending
                                              BlendingFactorSrc.Zero, BlendingFactorDest.One ); // Alpha blending
                        break;
                    case 4:
                        GL.BlendFunc( BlendingFactor.DstColor, BlendingFactor.Zero );
                        break;
                }

                if ( glMesh.Mesh != null )
                    shaderProgram.SetUniform( "uIsSelected", ShouldMeshShowAsSelected( context, glMesh ) );
                glMesh.Draw( worldTransform, shaderProgram );
            }

            // Re-enable depth mask
            GL.DepthMask( true );

            // Disable blending after drawing transparent objects
            GL.Disable( EnableCap.Blend );
        }


        public Func<CancellationToken,(Vector3 center,Vector3 minimum,Vector3 maximum)> CaptureMotionBounds(double currentTime,double futureSeconds,int samples,bool bboxCenter)
        {
            var pack=ModelPack;var animation=Animation;var overlay=BlendAnimation;
            var meshes=Nodes.SelectMany((node,index)=>node.IsVisible?node.Meshes.Where(m=>m.IsVisible&&m.Mesh!=null).Select(m=>(mesh:m.Mesh,node:index)):Enumerable.Empty<(Mesh mesh,int node)>()).ToArray();
            return cancellation=>
            {
                var pose=new GLModel(pack,null,true);
                if(animation!=null)pose.LoadAnimation(animation);
                if(overlay!=null)pose.LoadBlendAnimation(overlay);
                var matrices=new Matrix4x4[pack.Model.Bones.Count];
                var minimum=new Vector3(float.PositiveInfinity);var maximum=new Vector3(float.NegativeInfinity);
                var average=Vector3.Zero;int count=0;
                double speed=Math.Max(0,animation?.Speed.GetValueOrDefault(1f)??1);
                if(!double.IsFinite(speed))speed=1;
                for(int frame=0;frame<=samples;frame++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double time=currentTime+(samples>0?futureSeconds*speed*frame/samples:0);
                    if(animation?.Duration>0){time%=animation.Duration;pose.AnimateNodes(time);}
                    for(int bone=0;bone<matrices.Length;bone++)matrices[bone]=pack.Model.Bones[bone].InverseBindMatrix*pose.Nodes[pack.Model.Bones[bone].NodeIndex].WorldTransform;
                    var low=new Vector3(float.PositiveInfinity);var high=new Vector3(float.NegativeInfinity);
                    foreach(var item in meshes)
                    {
                        cancellation.ThrowIfCancellationRequested();var mesh=item.mesh;
                        for(int v=0;v<mesh.VertexCount;v++)
                        {
                            Vector3 position;
                            if(mesh.VertexWeights==null)position=Vector3.Transform(mesh.Vertices[v],pose.Nodes[item.node].WorldTransform);
                            else
                            {
                                position=Vector3.Zero;var weights=mesh.VertexWeights[v];
                                for(int j=0;j<weights.Weights.Length;j++)
                                    if(weights.Weights[j]!=0)position+=Vector3.Transform(mesh.Vertices[v],matrices[weights.Indices[j]])*weights.Weights[j];
                            }
                            low=Vector3.Min(low,position);high=Vector3.Max(high,position);
                        }
                    }
                    minimum=Vector3.Min(minimum,low);maximum=Vector3.Max(maximum,high);average+=(low+high)*.5f;count++;
                }
                return (bboxCenter?(minimum+maximum)*.5f:average/Math.Max(1,count),minimum,maximum);
            };
        }

        private void AnimateNodes( double animationTime )
        {
            foreach ( var glNode in Nodes )
            {
                var rotation    = glNode.Node.Rotation;
                var translation = glNode.Node.Translation;
                var scale       = glNode.Node.Scale;

                ApplyControllers( glNode.Controllers, animationTime, Animation.Duration, ref rotation, ref translation, ref scale, false );

                if ( BlendAnimation != null && BlendAnimation.Duration > 0 )
                {
                    var blendTime = animationTime % BlendAnimation.Duration;
                    ApplyControllers( glNode.BlendControllers, blendTime, BlendAnimation.Duration, ref rotation, ref translation, ref scale, true );
                }

                // Calculate current transform
                var transform = Matrix4x4.CreateFromQuaternion( Quaternion.Normalize(rotation) ) * Matrix4x4.CreateScale( scale );
                transform.Translation   = translation;
                glNode.CurrentTransform = transform;

                // Calculate world transform
                glNode.WorldTransform =
                    glNode.Parent == null ? glNode.CurrentTransform : glNode.CurrentTransform * glNode.Parent.WorldTransform;
            }
        }

        private void ApplyControllers( IEnumerable<AnimationController> controllers, double animationTime, double animationDuration,
                                       ref Quaternion rotation, ref Vector3 translation, ref Vector3 scale, bool isBlend )
        {
            foreach ( var controller in controllers )
            {
                foreach ( var layer in controller.Layers )
                {
                    var (curKey, nextKey) = GetCurrentAndNextKeys( layer, animationTime );
                    if ( curKey == null || !layer.HasPRSKeyFrames )
                        continue;

                    var prsKey = ( PRSKey )curKey;
                    var nextPrsKey = ( PRSKey )nextKey;

                    if ( isBlend )
                    {
                        if ( nextPrsKey != null )
                            InterpolateBlendKey( animationTime, layer, animationDuration, ref rotation, ref translation, ref scale, prsKey, nextPrsKey );
                        else
                            ApplyBlendKey( prsKey, layer, ref rotation, ref translation, ref scale );
                    }
                    else if ( nextPrsKey != null )
                    {
                        InterpolateKeys( animationTime, layer, animationDuration, ref rotation, ref translation, ref scale, prsKey, nextPrsKey );
                    }
                    else
                    {
                        if ( prsKey.HasRotation )
                            rotation = prsKey.Rotation;

                        if ( prsKey.HasPosition )
                            translation = prsKey.Position * layer.PositionScale;

                        if ( prsKey.HasScale )
                            scale = prsKey.Scale * layer.ScaleScale;
                    }
                }
            }
        }

        private static void ApplyBlendKey( PRSKey prsKey, AnimationLayer layer,
                                           ref Quaternion rotation, ref Vector3 translation, ref Vector3 scale )
        {
            if ( prsKey.HasRotation )
                rotation = Quaternion.Normalize( rotation * Quaternion.Inverse( prsKey.Rotation ) );

            if ( prsKey.HasPosition )
                translation += prsKey.Position * layer.PositionScale;

            if ( prsKey.HasScale )
                scale += prsKey.Scale * layer.ScaleScale;
        }

        private static void InterpolateBlendKey( double animationTime, AnimationLayer layer, double animationDuration,
                                                 ref Quaternion rotation, ref Vector3 translation, ref Vector3 scale,
                                                 PRSKey prsKey, PRSKey nextPrsKey )
        {
            var blend = GetInterpolationAmount( animationTime, animationDuration, prsKey, nextPrsKey );

            if ( prsKey.HasRotation )
                rotation = Quaternion.Normalize( rotation * Quaternion.Inverse( Quaternion.Slerp( prsKey.Rotation, nextPrsKey.Rotation, blend ) ) );

            if ( prsKey.HasPosition )
            {
                translation += Vector3.Lerp( prsKey.Position * layer.PositionScale,
                                             nextPrsKey.Position * layer.PositionScale,
                                             blend );
            }

            if ( prsKey.HasScale )
            {
                scale += Vector3.Lerp( prsKey.Scale * layer.ScaleScale,
                                       nextPrsKey.Scale * layer.ScaleScale,
                                       blend );
            }
        }

        private static void InterpolateKeys( double animationTime, AnimationLayer layer, double animationDuration,
                                             ref Quaternion rotation, ref Vector3 translation, ref Vector3 scale,
                                             PRSKey prsKey, PRSKey nextPrsKey )
        {
            var blend = GetInterpolationAmount( animationTime, animationDuration, prsKey, nextPrsKey );

            if ( prsKey.HasRotation )
                rotation = Quaternion.Slerp( Quaternion.Normalize(prsKey.Rotation), Quaternion.Normalize(nextPrsKey.Rotation), blend );

            if ( prsKey.HasPosition )
            {
                translation = Vector3.Lerp( prsKey.Position * layer.PositionScale,
                                            nextPrsKey.Position * layer.PositionScale,
                                            blend );
            }

            if ( prsKey.HasScale )
            {
                scale = Vector3.Lerp( prsKey.Scale * layer.ScaleScale,
                                      nextPrsKey.Scale * layer.ScaleScale,
                                      blend );
            }
        }

        private static float GetInterpolationAmount( double animationTime, double animationDuration,
                                                      PRSKey prsKey, PRSKey nextPrsKey )
        {
            var currentTime = ( double ) prsKey.Time;
            var nextTime = ( double ) nextPrsKey.Time;
            var sampleTime = animationTime;

            if ( nextTime <= currentTime )
            {
                nextTime += animationDuration;
                if ( sampleTime < currentTime )
                    sampleTime += animationDuration;
            }

            var interval = nextTime - currentTime;
            if ( interval <= 0 )
                return 0;

            return Math.Clamp( ( float ) ( ( sampleTime - currentTime ) / interval ), 0, 1 );
        }

        private static (Key curKey, Key nextKey) GetCurrentAndNextKeys(AnimationLayer layer,double animationTime)
        {
            if(layer.Keys.Count==0)return (null,null);
            int low=0,high=layer.Keys.Count;
            while(low<high)
            {
                int middle=low+(high-low)/2;
                if(layer.Keys[middle].Time<=animationTime)low=middle+1;else high=middle;
            }
            var current=layer.Keys[Math.Max(0,low-1)];
            var next=low<layer.Keys.Count?layer.Keys[low]:null;
            if(ReferenceEquals(current,next))next=null;
            // End keys hold their authored pose; the clock alone wraps playback.
            return (current,next);
        }

        #region IDisposable Support
        private bool mDisposed; // To detect redundant calls

        protected virtual void Dispose( bool disposing )
        {
            if ( !mDisposed )
            {
                if ( disposing )
                {
                    _bufferCancellation.Cancel();
                    _bufferPreparations.Clear();
                    foreach(var buffer in _bufferedAnimations.Values)buffer.Dispose();
                    _bufferedAnimations.Clear();
                    _authoredPoseRenderer?.Dispose();
                    _skinning.Dispose();
                    GLVertexArray.UnbindAll();
                    GL.BindBuffer( BufferTarget.ArrayBuffer, 0 );
                    GL.BindBuffer( BufferTarget.ElementArrayBuffer, 0 );
                    GL.BindTexture( TextureTarget.Texture2D, 0 );

                    foreach ( var glNode in Nodes )
                    {
                        glNode.Dispose();

                        foreach ( var geometry in glNode.Meshes )
                            geometry.Dispose();
                    }
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

    public class DrawContext
    {
        public ShaderRegistry ShaderRegistry { get; init; }
        public GLCamera Camera { get; init; }
        public double AnimationTime { get; init; }
        public Mesh SelectedMesh { get; init; }
        public Material SelectedMaterial { get; init; }
    }
}

