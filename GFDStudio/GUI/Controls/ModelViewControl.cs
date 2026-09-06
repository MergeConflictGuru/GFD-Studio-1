using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Common;
using GFDLibrary.Textures;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDLibrary.Rendering.OpenGL;
using GFDStudio.DataManagement;
using Color = System.Drawing.Color;
using Quaternion = OpenTK.Mathematics.Quaternion;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;
using OpenTK.GLControl;
using OpenTK.Windowing.Common;
using GFDLibrary.Materials;
using GFDLibrary.Shaders;

namespace GFDStudio.GUI.Controls
{
    public partial class ModelViewControl : GLControl
    {
        private static ModelViewControl sInstance;

        public static ModelViewControl Instance => sInstance ?? ( sInstance = new ModelViewControl() );

        
        private ShaderRegistry mShaderRegistry;
        private GLPerspectiveCamera mCamera;
        private readonly bool mCanRender = true;
        private Point mLastMouseLocation;
        private Vector3 mRaypickStart;
        private Vector3 mRaypickEnd;

        // Grid
        private int mGridVertexArrayID;
        private GLBuffer<Vector3> mGridVertexBuffer;
        private int mGridSize = 96;
        private int mGridSpacing = 16;
        private float mGridMinZ;
        public Vector4 GridLineColor = new Vector4( 50.15f, 50.15f, 50.15f, 1f );
        public Color ClearColor = System.Drawing.Color.FromArgb( 60, 63, 65 );

        // Primitives
        private PrimitiveMesh mCameraPrimitive;
        private PrimitiveMesh mLightPrimitive;
        private PrimitiveMesh mEplPrimitive;
        private GuideArrowMesh mGuideArrow;

        private const float GuideArrowHeightAboveGrid = 1.0f;
        private static readonly Vector3 sGuideArrowGridAnchor =
            new Vector3( 0.0f, GuideArrowHeightAboveGrid, 0.0f );

        // Model
        private GLModel mModel;
        private bool mIsModelLoaded;
        private bool mIsFieldModel;
        private Archive mFieldTextures;
        private Mesh mSelectedMesh;
        private Material mSelectedMaterial;

        // Animation
        private Stopwatch mTimeCounter;
        private double mLastTime;
        private Timer mUpdateTimer;
        private AnimationPlaybackState mAnimationPlayback = AnimationPlaybackState.Stopped;
        private double mAnimationTime;
        private float mGuideArrowOpacity;
        private double mGuideArrowLastUpdateTime = -1.0;

        private const float GuideArrowFadeTime = 0.24f;

        public Animation Animation { get; private set; }

        public Animation AnimationOverlay { get; private set; }

        public bool IsAnimationLoaded => Animation != null;

        public AnimationPlaybackState AnimationPlayback
        {
            get => mAnimationPlayback;
            set
            {
                if ( mAnimationPlayback == value || !IsAnimationLoaded )
                    return;

                mAnimationPlayback = value;

                switch ( AnimationPlayback )
                {
                    case AnimationPlaybackState.Stopped:
                        AnimationTime = 0;
                        mModel?.UnloadAnimation();
                        ResetAnimationClock();
                        break;
                    case AnimationPlaybackState.Paused:
                        ResetAnimationClock();
                        break;
                    case AnimationPlaybackState.Playing:
                        ResetAnimationClock();
                        if ( mModel?.Animation == null && IsAnimationLoaded )
                        {
                            mModel?.LoadAnimation( Animation );
                            if ( AnimationOverlay != null )
                                mModel?.LoadBlendAnimation( AnimationOverlay );
                        }
                        break;
                }

                AnimationPlaybackStateChanged?.Invoke( this, mAnimationPlayback );
            }
        }


        public double AnimationTime
        {
            get => mAnimationTime;
            set
            {
                mAnimationTime = value;
                AnimationTimeChanged?.Invoke( this, mAnimationTime );
            }
        }

        // Events
        public event EventHandler<Animation> AnimationLoaded;
        public event EventHandler<AnimationPlaybackState> AnimationPlaybackStateChanged;
        public event EventHandler<double> AnimationTimeChanged;

        private ModelViewControl() : base( new GLControlSettings
        {
            APIVersion = new Version( 3, 3, 0, 0 ),
            Flags =
#if GL_DEBUG
                ContextFlags.Debug | ContextFlags.ForwardCompatible,
#else
                ContextFlags.ForwardCompatible,
#endif
            Profile = ContextProfile.Core,
            NumberOfSamples = 4,
            DepthBits = 24,
            StencilBits = 0
        } )
        {
            InitializeComponent();

            // make the control fill up the space of the parent cotnrol
            Dock = DockStyle.Fill;

            // required to use GL in the context of this control
            MakeCurrent();
            Context.SwapInterval = 1;
            LogGLInfo();

            if ( !InitializeShaders() )
            {
                Visible = false;
                mCanRender = false;
            }
            else
            {
                InitializeGLRenderState();
            }

            CreateGrid();
            LoadPrimitives();
        }

        private void CreateGrid()
        {
            // thanks Skyth
            var vertices = new List<Vector3>();
            for ( int i = -mGridSize; i <= mGridSize; i += mGridSpacing )
            {
                vertices.Add( new Vector3( i, 0, -mGridSize ) );
                vertices.Add( new Vector3( i, 0, mGridSize ) );
                vertices.Add( new Vector3( -mGridSize, 0, i ) );
                vertices.Add( new Vector3( mGridSize, 0, i ) );
            }

            mGridMinZ = (int)vertices.Min( x => x.Z );
            mGridVertexArrayID = GL.GenVertexArray();
            GL.BindVertexArray( mGridVertexArrayID );

            mGridVertexBuffer = new GLBuffer<Vector3>( BufferTarget.ArrayBuffer, vertices.ToArray() );

            GL.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, mGridVertexBuffer.Stride, 0 );
            GL.EnableVertexAttribArray( 0 );
        }

        private void LoadPrimitives()
        {
            mCameraPrimitive = new PrimitiveMesh( "primitives/camera.obj" );
            mLightPrimitive = new PrimitiveMesh( "primitives/light.obj" );
            mEplPrimitive = new PrimitiveMesh( "primitives/epl.obj" );
            mGuideArrow = new GuideArrowMesh();
        }

        private void DrawLine( Vector3 start, Vector3 end, Vector4 color )
        {
            var lineShaderProgram = mShaderRegistry.mLineShader.Id;

            // Line vertices
            float[] vertices = {
                start.X, start.Y, start.Z,
                end.X, end.Y, end.Z
            };

            // Create and bind VAO
            int vao = GL.GenVertexArray();
            GL.BindVertexArray( vao );

            // Create, bind, and fill VBO with vertices
            int vbo = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ArrayBuffer, vbo );
            GL.BufferData( BufferTarget.ArrayBuffer, vertices.Length * sizeof( float ), vertices, BufferUsageHint.StaticDraw );

            // Enable the shader program and set uniforms
            GL.UseProgram( lineShaderProgram );

            // Set line color uniform
            int lineColorLocation = GL.GetUniformLocation( lineShaderProgram, "uColor" );
            GL.Uniform4( lineColorLocation, color );

            var view = mCamera.View;
            int viewLoc = GL.GetUniformLocation( lineShaderProgram, "uView" );
            GL.UniformMatrix4( viewLoc, false, ref view );

            var projection = mCamera.Projection;
            int projLoc = GL.GetUniformLocation( lineShaderProgram, "uProjection" );
            GL.UniformMatrix4( projLoc, false, ref projection );

            // Define vertex layout
            GL.EnableVertexAttribArray( 0 );
            GL.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, 3 * sizeof( float ), 0 );

            // Draw the line
            GL.DrawArrays( PrimitiveType.Lines, 0, 2 );

            // Clean up
            GL.DisableVertexAttribArray( 0 );
            GL.BindBuffer( BufferTarget.ArrayBuffer, 0 );
            GL.DeleteBuffer( vbo );
            GL.BindVertexArray( 0 );
            GL.DeleteVertexArray( vao );
            GL.UseProgram( 0 );
        }

        //public readonly struct GLVertexArrayHelper : IDisposable
        //{
        //    struct BindHelper : IDisposable
        //    {
        //        public readonly void Dispose()
        //        {
        //            GL.BindVertexArray( 0 );
        //        }
        //    }

        //    public readonly int Id;

        //    public GLVertexArrayHelper()
        //    {
        //        Id = GL.GenVertexArray();
        //    }

        //    public readonly IDisposable Bind()
        //    {
        //        GL.BindVertexArray( Id );
        //        return new BindHelper();
        //    }

        //    public readonly void Dispose()
        //    {
        //        GL.DeleteVertexArray( Id );
        //    }
        //}

        //public readonly struct GLBufferHelper : IDisposable
        //{
        //    readonly struct BindHelper : IDisposable
        //    {
        //        private readonly BufferTarget _target;

        //        public BindHelper(BufferTarget target)
        //        {
        //            _target = target;
        //        }

        //        public readonly void Dispose()
        //        {
        //            GL.BindBuffer( _target, 0 );
        //        }
        //    }

        //    public readonly int Id;

        //    public GLBufferHelper()
        //    {
        //        Id = GL.GenBuffer();
        //    }

        //    public readonly IDisposable BindBuffer(BufferTarget target)
        //    {
        //        GL.BindBuffer( target, Id );
        //        return new BindHelper();
        //    }

        //    public readonly void Dispose()
        //    {
        //        GL.DeleteVertexArray( Id );
        //    }
        //}

        private void DrawSphere( Vector3 center, float radius, int latitudeSegments = 20, int longitudeSegments = 20 )
        {
            List<float> vertices = new List<float>();
            List<int> indices = new List<int>();

            // Generate vertices
            for ( int lat = 0; lat <= latitudeSegments; lat++ )
            {
                float theta = lat * MathF.PI / latitudeSegments;
                float sinTheta = MathF.Sin( theta );
                float cosTheta = MathF.Cos( theta );

                for ( int lon = 0; lon <= longitudeSegments; lon++ )
                {
                    float phi = lon * 2 * MathF.PI / longitudeSegments;
                    float sinPhi = MathF.Sin( phi );
                    float cosPhi = MathF.Cos( phi );

                    Vector3 position = new Vector3(
                        center.X + radius * cosPhi * sinTheta,
                        center.Y + radius * cosTheta,
                        center.Z + radius * sinPhi * sinTheta
                    );

                    Vector3 normal = Vector3.Normalize( position - center );

                    // Add vertex position and normal
                    vertices.Add( position.X );
                    vertices.Add( position.Y );
                    vertices.Add( position.Z );
                    vertices.Add( normal.X );
                    vertices.Add( normal.Y );
                    vertices.Add( normal.Z );
                }
            }

            // Generate indices
            for ( int lat = 0; lat < latitudeSegments; lat++ )
            {
                for ( int lon = 0; lon < longitudeSegments; lon++ )
                {
                    int first = lat * ( longitudeSegments + 1 ) + lon;
                    int second = first + longitudeSegments + 1;

                    indices.Add( first );
                    indices.Add( second );
                    indices.Add( first + 1 );

                    indices.Add( second );
                    indices.Add( second + 1 );
                    indices.Add( first + 1 );
                }
            }

            // VAO
            var vao = GL.GenVertexArray();
            GL.BindVertexArray( vao );

            // VBO
            var vbo = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ArrayBuffer, vbo );
            GL.BufferData( BufferTarget.ArrayBuffer, vertices.Count * sizeof( float ), vertices.ToArray(), BufferUsageHint.StaticDraw );

            // EBO
            var ebo = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ElementArrayBuffer, ebo );
            GL.BufferData( BufferTarget.ElementArrayBuffer, indices.Count * sizeof( int ), indices.ToArray(), BufferUsageHint.StaticDraw );

            // Specify vertex attributes
            int stride = 6 * sizeof( float ); // 3 for position, 3 for normal
            GL.EnableVertexAttribArray( 0 );
            GL.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, stride, 0 );
            GL.EnableVertexAttribArray( 1 );
            GL.VertexAttribPointer( 1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof( float ) );

            mShaderRegistry.mLineShader.Use();
            mShaderRegistry.mLineShader.SetUniform( "uView", mCamera.View );
            mShaderRegistry.mLineShader.SetUniform( "uProjection", mCamera.Projection );

            //GL.DrawArrays( PrimitiveType.Lines, 0, vertices.Count / 12 );
            GL.DrawElements( PrimitiveType.Triangles, indices.Count, DrawElementsType.UnsignedInt, 0 );

            // Clean up
            GL.UseProgram( 0 );
            GL.BindVertexArray( 0 );
            GL.DisableVertexAttribArray( 1 );
            GL.DisableVertexAttribArray( 0 );
            GL.BindBuffer( BufferTarget.ElementArrayBuffer, ebo );
            GL.BindBuffer( BufferTarget.ArrayBuffer, 0 );
            GL.BindVertexArray( 0 );
            GL.DeleteBuffer( ebo );
            GL.DeleteBuffer( vbo );
            GL.DeleteVertexArray( 0 );
        }

        /// <summary>
        /// Load a model for displaying in the control.
        /// </summary>
        /// <param name="modelPack"></param>
        public void LoadModel( ModelPack modelPack )
        {
            if ( !mCanRender || modelPack.Model == null )
                return;

            var preserveCamera = mCamera != null;
            var cameraTranslation = preserveCamera ? mCamera.Translation : Vector3.Zero;
            var cameraOffset = preserveCamera ? mCamera.Offset : Vector3.Zero;
            var modelTranslation = preserveCamera ? mCamera.ModelTranslation : Vector3.Zero;
            var modelRotation = preserveCamera ? mCamera.ModelRotation : Vector3.Zero;

            if ( mIsModelLoaded )
            {
                // Unload previously loaded model to free memory
                UnloadModel();
            }

            // Load model into optimized format
            mModel = new GLModel( modelPack, ( material, textureName ) =>
            {
                if (string.IsNullOrWhiteSpace(textureName))
                    return null;
                if ( mIsFieldModel && mFieldTextures.TryOpenFile( textureName, out var textureStream ) )
                {
                    using ( textureStream )
                    {
                        var texture = new FieldTexturePS3( textureStream );
                        return new GLTexture( texture );
                    }
                }
                else if ( modelPack.Textures.TryGetTexture( textureName, out var texture ) )
                {
                    return new GLTexture( texture );
                }
                else
                {
                    Trace.TraceWarning( $"tTexture '{textureName}' used by material '{material.Name}' is missing" );
                }

                return null;
            } );

            foreach ( var node in modelPack.Model.Nodes.Where( x => x.HasAttachments ) )
            {
                var glNode = mModel.Nodes.Find( x => x.Node == node );

                foreach ( var attachment in node.Attachments )
                {
                    switch ( attachment.Type )
                    {
                        case NodeAttachmentType.Camera:
                            glNode.Meshes.Add( mCameraPrimitive.Instantiate( true, false, PrimitiveMesh.DefaultColor ) );
                            break;

                        case NodeAttachmentType.Light:
                            glNode.Meshes.Add( mLightPrimitive.Instantiate( true, false, PrimitiveMesh.DefaultColor ) );
                            break;

                        case NodeAttachmentType.Epl:
                            glNode.Meshes.Add( mEplPrimitive.Instantiate( true, true, PrimitiveMesh.DefaultColor ) );
                            break;
                    }
                }
            }

            mIsModelLoaded = true;
            mGuideArrowOpacity = 0.0f;
            mGuideArrowLastUpdateTime = -1.0;

            // Initialize camera
            InitializeCamera();

            if ( preserveCamera )
            {
                mCamera.Translation = cameraTranslation;
                mCamera.Offset = cameraOffset;
                mCamera.ModelTranslation = modelTranslation;
                mCamera.ModelRotation = modelRotation;
            }

            UpdateViewport();

            if ( Animation != null )
            {
                // Apply previously loaded animation to new model
                var animationOverlay = AnimationOverlay;
                LoadAnimation( Animation, AnimationPlayback != AnimationPlaybackState.Playing );
                if ( animationOverlay != null )
                    LoadAnimationOverlay( animationOverlay );
            }

            Invalidate();
        }

        public void LoadAnimation( Animation animation, bool reset = true )
        {
            Animation = animation;
            AnimationOverlay = null;
            mModel?.LoadAnimation( Animation );

            AnimationLoaded?.Invoke( this, animation );

            if ( reset )
            {
                AnimationTime = 0;
                ResetAnimationClock();
                AnimationPlayback = AnimationPlaybackState.Playing;
            }
        }

        public void LoadAnimationOverlay( Animation animation )
        {
            AnimationOverlay = animation;
            mModel?.LoadBlendAnimation( animation );
        }

        public void UnloadAnimationOverlay()
        {
            AnimationOverlay = null;
            mModel?.UnloadBlendAnimation();
        }

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose( bool disposing )
        {
            if ( disposing )
            {
                components?.Dispose();

                mGuideArrow?.Dispose();
                mShaderRegistry.mDefaultShader?.Dispose();
                mShaderRegistry.mGuideArrowShader?.Dispose();

                mUpdateTimer?.Stop();
                mUpdateTimer?.Dispose();

                if ( mIsModelLoaded )
                    UnloadModel();
            }

            base.Dispose( disposing );
        }

        /// <summary>
        /// Executed during the initial load of the control.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnLoad( EventArgs e )
        {
            mTimeCounter = new Stopwatch();
            mTimeCounter.Start();
            mLastTime = mTimeCounter.Elapsed.TotalSeconds;
            mUpdateTimer = new Timer
            {
                Interval = 16
            };
            mUpdateTimer.Tick += ( sender, args ) =>
            {
                if ( mCanRender && AnimationPlayback == AnimationPlaybackState.Playing && !IsDisposed )
                    Invalidate();
            };
            mUpdateTimer.Start();
        }

        private void ExecuteTimedCallback( Action action )
        {
            // Update timings
            var curTime = mTimeCounter.Elapsed.TotalSeconds;
            var deltaTime = curTime - mLastTime;

            if ( AnimationPlayback == AnimationPlaybackState.Playing )
            {
                var nextAnimationTime = AnimationTime + ( deltaTime * Animation.Speed.GetValueOrDefault( 1f ) );
                AnimationTime = nextAnimationTime >= Animation.Duration ? 0 : nextAnimationTime;
            }

            action();

            // Remember current time
            mLastTime = curTime;
        }

        private void ResetAnimationClock()
        {
            if ( mTimeCounter != null )
                mLastTime = mTimeCounter.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// Executed when a frame is rendered.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnPaint( PaintEventArgs e )
        {
            if ( !mCanRender || mCamera == null )
                return;

            RenderFrame();
        }

        private void RenderFrame()
        {
            ExecuteTimedCallback( () =>
            {
                // clear the buffers
                GL.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );

                DrawGrid( mCamera.View, mCamera.Projection );

                if ( mIsModelLoaded )
                {
                    // Draw model
                    mModel.Draw( new DrawContext()
                    {
                        ShaderRegistry = mShaderRegistry,
                        Camera = mCamera,
                        AnimationTime = AnimationTime,
                        SelectedMaterial = mSelectedMaterial,
                        SelectedMesh = mSelectedMesh
                    } );

                    DrawGuideArrow();
                }

                //foreach ( var node in mModel.Nodes )
                //{
                //    DrawSphere( node.WorldTransform.Translation.ToOpenTK(), 1, 8, 8 );
                //    foreach ( var mesh in node.Meshes )
                //    {
                //        if (mesh.Mesh.BoundingSphere.HasValue)
                //        {
                //            var worldCenter = System.Numerics.Vector3.Transform( mesh.Mesh.BoundingSphere.Value.Center, node.WorldTransform );
                //            DrawSphere( worldCenter.ToOpenTK(), mesh.Mesh.BoundingSphere.Value.Radius, 8, 8 );
                //        }
                //    }
                //}

                //DrawLine( mRaypickStart, mRaypickEnd, new Vector4( 1, 0, 0, 1 ) );

                SwapBuffers();
            } );
        }

        private void DrawGuideArrow()
        {
            if ( mGuideArrow == null || mShaderRegistry?.mGuideArrowShader == null || !mIsModelLoaded )
                return;

            GetGuideArrowTargetBounds( out var target, out var targetExtents );
            var targetClip = ProjectGuideArrowPoint( target, out var targetInFrontOfCamera );
            var targetInView = IsGuideArrowTargetInView( target, targetExtents );
            var desiredOpacity = CalculateGuideArrowOpacity( targetClip, targetInFrontOfCamera, targetInView );
            var opacity = UpdateGuideArrowOpacity( desiredOpacity );
            if ( opacity <= 0.001f )
                return;

            var anchor = ResolveGuideArrowAnchor();
            var direction = target - anchor;
            if ( direction.LengthSquared < 0.0001f )
                return;

            direction.Normalize();
            var model = Matrix4.CreateScale( GetGuideArrowScale( anchor ) ) *
                        CreateGuideArrowOrientation( direction ) *
                        Matrix4.CreateTranslation( anchor );

            var blended = opacity < 0.999f;
            var depthTestEnabled = GL.IsEnabled( EnableCap.DepthTest );
            // The indicator is a navigation aid, so it must stay readable even when
            // the character or grid happens to occupy the same depth range.
            GL.Disable( EnableCap.DepthTest );
            if ( blended )
            {
                GL.Enable( EnableCap.Blend );
                GL.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha );
                GL.DepthMask( false );
            }
            else
            {
                // Do not inherit blending left by a model material. The arrow
                // body must be solid whenever the marker is fully visible.
                GL.Disable( EnableCap.Blend );
                GL.DepthMask( true );
            }

            mGuideArrow.Draw( mShaderRegistry.mGuideArrowShader, mCamera.View, mCamera.Projection, model, opacity );

            if ( blended )
            {
                GL.DepthMask( true );
                GL.Disable( EnableCap.Blend );
            }

            if ( depthTestEnabled )
                GL.Enable( EnableCap.DepthTest );
        }

        private float GetGuideArrowScale( Vector3 anchor )
        {
            var viewAnchor = Vector4.TransformRow( new Vector4( anchor, 1.0f ), mCamera.View );
            var viewDepth = MathF.Max( 0.25f, -viewAnchor.Z );
            var focalLength = MathF.Max( 0.1f, MathF.Abs( mCamera.Projection.M22 ) );

            // The mesh is about 2.1 units across its local bounding sphere. Size it
            // from camera depth so its projected diameter is roughly one third of
            // the viewport even when the viewer is zoomed far out.
            const float localBoundingDiameter = 2.1f;
            const float desiredViewportDiameter = 0.68f; // 34% of the NDC width/height
            var scale = desiredViewportDiameter * viewDepth /
                        ( localBoundingDiameter * focalLength );
            return Math.Clamp( scale, 0.85f, 48.0f );
        }

        private float UpdateGuideArrowOpacity( float desiredOpacity )
        {
            var currentTime = mTimeCounter?.Elapsed.TotalSeconds ?? 0.0;
            var deltaTime = mGuideArrowLastUpdateTime < 0.0
                ? 1.0 / 60.0
                : currentTime - mGuideArrowLastUpdateTime;
            mGuideArrowLastUpdateTime = currentTime;

            // Keep the transition stable if a debugger pauses or a frame takes
            // unusually long. Exponential smoothing gives both entering and
            // leaving the viewport the same gentle response.
            var step = 1.0f - MathF.Exp( -(float)Math.Clamp( deltaTime, 1.0 / 240.0, 0.10 ) /
                                         GuideArrowFadeTime );
            mGuideArrowOpacity += ( desiredOpacity - mGuideArrowOpacity ) * step;
            if ( MathF.Abs( desiredOpacity - mGuideArrowOpacity ) < 0.001f )
                mGuideArrowOpacity = desiredOpacity;

            return mGuideArrowOpacity;
        }

        private void GetGuideArrowTargetBounds( out Vector3 center, out Vector3 extents )
        {
            var model = mModel?.ModelPack?.Model;
            // Keep this path constant-time. It is called for every rendered frame
            // while animation playback is active; walking every animated vertex
            // here starves the WinForms message loop on large character models.
            if ( model?.BoundingBox is { } bounds )
            {
                center = new Vector3(
                    ( bounds.Min.X + bounds.Max.X ) * 0.5f,
                    ( bounds.Min.Y + bounds.Max.Y ) * 0.5f,
                    ( bounds.Min.Z + bounds.Max.Z ) * 0.5f );
                extents = new Vector3(
                    MathF.Max( 0.001f, MathF.Abs( bounds.Max.X - bounds.Min.X ) * 0.5f ),
                    MathF.Max( 0.001f, MathF.Abs( bounds.Max.Y - bounds.Min.Y ) * 0.5f ),
                    MathF.Max( 0.001f, MathF.Abs( bounds.Max.Z - bounds.Min.Z ) * 0.5f ) );
                return;
            }

            if ( model?.BoundingSphere is { } sphere )
            {
                center = new Vector3( sphere.Center.X, sphere.Center.Y, sphere.Center.Z );
                extents = new Vector3( MathF.Max( 0.001f, sphere.Radius ) );
                return;
            }

            center = Vector3.Zero;
            extents = new Vector3( 1.0f );
        }

        private bool IsGuideArrowTargetInView( Vector3 center, Vector3 extents )
        {
            var minimumX = float.PositiveInfinity;
            var maximumX = float.NegativeInfinity;
            var minimumY = float.PositiveInfinity;
            var maximumY = float.NegativeInfinity;
            var hasFrontPoint = false;
            for ( var x = -1; x <= 1; x += 2 )
            for ( var y = -1; y <= 1; y += 2 )
            for ( var z = -1; z <= 1; z += 2 )
            {
                var point = center + new Vector3( extents.X * x, extents.Y * y, extents.Z * z );
                var viewPosition = Vector4.TransformRow( new Vector4( point, 1.0f ), mCamera.View );
                if ( viewPosition.Z >= 0.0f || viewPosition.W <= 0.0001f )
                    continue;

                hasFrontPoint = true;
                var clipPosition = Vector4.TransformRow( viewPosition, mCamera.Projection );
                if ( clipPosition.W <= 0.0001f )
                    continue;

                var normalizedX = clipPosition.X / clipPosition.W;
                var normalizedY = clipPosition.Y / clipPosition.W;
                minimumX = MathF.Min( minimumX, normalizedX );
                maximumX = MathF.Max( maximumX, normalizedX );
                minimumY = MathF.Min( minimumY, normalizedY );
                maximumY = MathF.Max( maximumY, normalizedY );
            }

            if ( !hasFrontPoint )
                return false;

            // The projected AABB is intentionally conservative: if any portion of
            // the model overlaps the viewport, the navigation marker is hidden.
            return minimumX <= 1.0f && maximumX >= -1.0f &&
                   minimumY <= 1.0f && maximumY >= -1.0f;
        }

        private Matrix4 CreateGuideArrowOrientation( Vector3 forward )
        {
            // The mesh uses local +Z as its arrow tip direction and local +Y as
            // its broad, shaded face. Build a complete orthonormal basis from the
            // actual target direction, while keeping that broad face toward the
            // camera so the marker remains visually readable at steep angles.
            var inverseView = Matrix4.Invert( mCamera.View );
            var cameraDirection = Vector4.TransformRow( new Vector4( 0.0f, 0.0f, 1.0f, 0.0f ), inverseView );
            var faceNormal = new Vector3( cameraDirection.X, cameraDirection.Y, cameraDirection.Z );
            if ( faceNormal.LengthSquared < 0.0001f )
                faceNormal = Vector3.UnitY;
            else
                faceNormal.Normalize();

            var up = faceNormal - forward * Vector3.Dot( faceNormal, forward );
            if ( up.LengthSquared < 0.0001f )
            {
                var cameraUp = Vector4.TransformRow( new Vector4( 0.0f, 1.0f, 0.0f, 0.0f ), inverseView );
                up = new Vector3( cameraUp.X, cameraUp.Y, cameraUp.Z );
                up -= forward * Vector3.Dot( up, forward );
            }

            if ( up.LengthSquared < 0.0001f )
                up = Vector3.UnitY - forward * Vector3.Dot( Vector3.UnitY, forward );
            up.Normalize();

            var right = Vector3.Cross( up, forward );
            right.Normalize();
            up = Vector3.Cross( forward, right );
            up.Normalize();

            return new Matrix4(
                new Vector4( right, 0.0f ),
                new Vector4( up, 0.0f ),
                new Vector4( forward, 0.0f ),
                Vector4.UnitW );
        }

        private Vector3 ResolveGuideArrowAnchor()
        {
            var gridAnchorClip = ProjectGuideArrowPoint( sGuideArrowGridAnchor, out var gridAnchorInFront );
            // This is the normal placement: one world unit above the grid
            // origin. Only leave it when the marker would be clipped.
            if ( gridAnchorInFront && IsGuideArrowPointOnScreen( gridAnchorClip, 0.52f ) )
                return sGuideArrowGridAnchor;

            // If the grid anchor itself is outside the viewport, keep the
            // fallback on the same side of the screen as that real anchor. This
            // preserves the grid-based default instead of silently moving the
            // marker to the view center.
            var modelRadius = 2.0f;
            if ( mModel?.ModelPack?.Model?.BoundingSphere is { } sphere )
                modelRadius = MathF.Max( 2.0f, sphere.Radius );

            var inverseView = Matrix4.Invert( mCamera.View );
            var viewDepth = MathF.Max( 2.5f, modelRadius * 1.5f );
            var fallbackX = 0.0f;
            var fallbackY = 0.65f;
            if ( gridAnchorInFront && gridAnchorClip.W > 0.0001f )
            {
                var normalizedX = gridAnchorClip.X / gridAnchorClip.W;
                var normalizedY = gridAnchorClip.Y / gridAnchorClip.W;
                fallbackX = Math.Clamp( normalizedX, -0.52f, 0.52f );
                fallbackY = Math.Clamp( normalizedY, -0.52f, 0.52f );
            }

            var viewSpaceAnchor = new Vector4(
                fallbackX * viewDepth / MathF.Max( 0.1f, MathF.Abs( mCamera.Projection.M11 ) ),
                fallbackY * viewDepth / MathF.Max( 0.1f, MathF.Abs( mCamera.Projection.M22 ) ),
                -viewDepth, 1.0f );
            var worldAnchor = Vector4.TransformRow( viewSpaceAnchor, inverseView );
            return new Vector3( worldAnchor.X, worldAnchor.Y, worldAnchor.Z );
        }

        private Vector4 ProjectGuideArrowPoint( Vector3 worldPosition, out bool inFrontOfCamera )
        {
            var viewPosition = Vector4.TransformRow( new Vector4( worldPosition, 1.0f ), mCamera.View );
            inFrontOfCamera = viewPosition.Z < 0.0f;
            return Vector4.TransformRow( viewPosition, mCamera.Projection );
        }

        private static bool IsGuideArrowPointOnScreen( Vector4 clipPosition, float margin )
        {
            if ( clipPosition.W <= 0.0001f )
                return false;

            var normalizedX = clipPosition.X / clipPosition.W;
            var normalizedY = clipPosition.Y / clipPosition.W;
            return MathF.Abs( normalizedX ) <= margin && MathF.Abs( normalizedY ) <= margin;
        }

        private static float CalculateGuideArrowOpacity( Vector4 clipPosition, bool inFrontOfCamera,
                                                         bool targetInView )
        {
            if ( targetInView )
                return 0.0f;
            if ( !inFrontOfCamera || clipPosition.W <= 0.0001f )
                return 1.0f;

            var normalizedX = clipPosition.X / clipPosition.W;
            var normalizedY = clipPosition.Y / clipPosition.W;
            var edgeDistance = MathF.Max( MathF.Abs( normalizedX ), MathF.Abs( normalizedY ) );
            return SmoothStep( 0.72f, 0.98f, edgeDistance );
        }

        private static float SmoothStep( float edge0, float edge1, float value )
        {
            var t = Math.Clamp( ( value - edge0 ) / ( edge1 - edge0 ), 0.0f, 1.0f );
            return t * t * ( 3.0f - 2.0f * t );
        }

        private void DrawGrid( Matrix4 view, Matrix4 projection )
        {
            mShaderRegistry.mLineShader.Use();
            mShaderRegistry.mLineShader.SetUniform( "uView", view );
            mShaderRegistry.mLineShader.SetUniform( "uProjection", projection );
            mShaderRegistry.mLineShader.SetUniform( "uColor", GridLineColor );
            mShaderRegistry.mLineShader.SetUniform( "uMinZ", mGridMinZ );

            GL.BindVertexArray( mGridVertexArrayID );
            GL.DrawArrays( PrimitiveType.Lines, 0, mGridVertexBuffer.Count );
        }

        /// <summary>
        /// Executed when control is resized.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnResize( EventArgs e )
        {
            // GLControl uses its base resize handler to resize the native OpenGL
            // window. That must happen even before a model is loaded; otherwise
            // the initial model can be ready while the native render surface is
            // still 0x0 until the user causes another layout pass.
            base.OnResize( e );

            UpdateViewport();
        }

        private void UpdateViewport()
        {
            if ( !mCanRender || mCamera == null || Width <= 0 || Height <= 0 )
                return;

            mCamera.AspectRatio = (float)Width / Height;
            GL.Viewport( ClientRectangle );
        }

        /// <summary>
        /// Log GL info for diagnostics.
        /// </summary>
        private void LogGLInfo()
        {
            // todo: log to file? would help with debugging crashes on clients
            Trace.TraceInformation( "GL Info:" );
            Trace.TraceInformation( $"     Vendor         {GL.GetString( StringName.Vendor )}" );
            Trace.TraceInformation( $"     Renderer       {GL.GetString( StringName.Renderer )}" );
            Trace.TraceInformation( $"     Version        {GL.GetString( StringName.Version )}" );
            Trace.TraceInformation( $"     Extensions     {GL.GetString( StringName.Extensions )}" );
            Trace.TraceInformation( $"     GLSL version   {GL.GetString( StringName.ShadingLanguageVersion )}" );
            Trace.TraceInformation( "" );
        }

        /// <summary>
        /// Initializes GL state before rendering starts.
        /// </summary>
        private void InitializeGLRenderState()
        {
            GL.ClearColor( ClearColor );
            GL.FrontFace( FrontFaceDirection.Ccw );
            GL.CullFace( TriangleFace.Back );
            GL.Enable( EnableCap.CullFace );
            GL.Enable( EnableCap.DepthTest );

#if GL_DEBUG
            GL.Enable( EnableCap.DebugOutputSynchronous );
            GL.DebugMessageCallback( GLDebugMessageCallback, IntPtr.Zero );
#endif

            GL.Enable( EnableCap.Multisample );
        }

        [Conditional( "DEBUG" )]
        private void GLDebugMessageCallback( DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam )
        {
            // notication for buffer using VIDEO memory
            if ( id == 0x00020071 )
                return;

            var msg = Marshal.PtrToStringAnsi( message, length );
            Trace.TraceInformation( $"GL Debug: {severity} {type} {msg}" );
        }

        /// <summary>
        /// Initializes shaders and links the shader program.
        /// </summary>
        private bool InitializeShaders()
        {
            mShaderRegistry = new ShaderRegistry();
            return mShaderRegistry.InitializeShaders(x => DataStore.GetPath(x));
        }

        private void UnloadModel()
        {
            if ( !mIsModelLoaded )
                return;

            mIsModelLoaded = false;
            mGuideArrowOpacity = 0.0f;
            mGuideArrowLastUpdateTime = -1.0;
            mModel.Dispose();
        }

        private void InitializeCamera()
        {
            var cameraFov = 45f;

            BoundingSphere bSphere;
            if ( !mModel.ModelPack.Model.BoundingSphere.HasValue )
            {
                if ( mModel.ModelPack.Model.BoundingBox.HasValue )
                {
                    bSphere = BoundingSphere.Calculate( mModel.ModelPack.Model.BoundingBox.Value );
                }
                else
                {
                    bSphere = new BoundingSphere( new System.Numerics.Vector3(), 0 );
                }
            }
            else
            {
                bSphere = mModel.ModelPack.Model.BoundingSphere.Value;
            }

            mCamera = new GLPerspectiveCamera( 1f, 100000f, cameraFov,
                                (float)Width / (float)Height, bSphere, Vector3.Zero, Vector3.Zero );
        }

        //
        // Input events
        //

        private Point GetMouseLocationDelta( Point location )
        {
            location.X -= mLastMouseLocation.X;
            location.Y -= mLastMouseLocation.Y;

            return location;
        }

        protected internal float CalculateMultiplier( float baseValue = 0.5f )
        {
            float multiplier = baseValue;
            if ( ( ModifierKeys & Keys.Shift ) == Keys.Shift )
            {
                multiplier *= 10f;
            }
            else if ( ( ModifierKeys & Keys.Control ) == Keys.Control )
            {
                multiplier /= 2f;
            }
            return multiplier;
        }

        private bool RayIntersectsSphere( Vector3 rayOrigin, Vector3 rayDirection, Vector3 sphereCenter, float sphereRadius, out float distance )
        {
            // Vector from the ray's origin to the center of the sphere
            Vector3 m = rayOrigin - sphereCenter;

            float b = Vector3.Dot( m, rayDirection );
            float c = Vector3.Dot( m, m ) - sphereRadius * sphereRadius;

            // If ray starts outside sphere (c > 0) and points away from sphere (b > 0), no intersection
            if ( c > 0.0f && b > 0.0f )
            {
                distance = 0.0f;
                return false;
            }

            // Calculate discriminant
            float discriminant = b * b - c;

            // If discriminant is negative, no intersection
            if ( discriminant < 0.0f )
            {
                distance = 0.0f;
                return false;
            }

            // Calculate distance to the closest intersection point
            distance = -b - MathF.Sqrt( discriminant );

            // If distance is negative, the ray started inside the sphere so clamp to zero
            if ( distance < 0.0f )
                distance = 0.0f;

            return true;
        }

        public bool RayIntersectsBox( Vector3 rayOrigin, Vector3 rayDir, Vector3 boxMin, Vector3 boxMax, out float distance )
        {
            float tMin = ( boxMin.X - rayOrigin.X ) / rayDir.X;
            float tMax = ( boxMax.X - rayOrigin.X ) / rayDir.X;

            if ( tMin > tMax ) (tMin, tMax) = (tMax, tMin);

            float tyMin = ( boxMin.Y - rayOrigin.Y ) / rayDir.Y;
            float tyMax = ( boxMax.Y - rayOrigin.Y ) / rayDir.Y;

            if ( tyMin > tyMax ) (tyMin, tyMax) = (tyMax, tyMin);

            if ( ( tMin > tyMax ) || ( tyMin > tMax ) )
            {
                distance = 0;
                return false;
            }

            if ( tyMin > tMin ) tMin = tyMin;
            if ( tyMax < tMax ) tMax = tyMax;

            float tzMin = ( boxMin.Z - rayOrigin.Z ) / rayDir.Z;
            float tzMax = ( boxMax.Z - rayOrigin.Z ) / rayDir.Z;

            if ( tzMin > tzMax ) (tzMin, tzMax) = (tzMax, tzMin);

            if ( ( tMin > tzMax ) || ( tzMin > tMax ) )
            {
                distance = 0;
                return false;
            }

            if ( tzMin > tMin ) tMin = tzMin;
            if ( tzMax < tMax ) tMax = tzMax;

            distance = tMin;
            return tMin >= 0;
        }

        private bool Raypick(int mouseX, int mouseY)
        {
            // Step 1: Convert mouse position to NDC
            var x = ( 2.0f * mouseX ) / ClientRectangle.Width - 1.0f;
            var y = 1.0f - ( 2.0f * mouseY ) / ClientRectangle.Height;
            var rayNDC = new Vector4( x, y, -1.0f, 1.0f ); // near plane

            // Step 2: Convert NDC to world coordinates
            var invProjectionMatrix = Matrix4.Invert( mCamera.Projection );
            var invViewMatrix = Matrix4.Invert( mCamera.View );

            var rayCamera = invProjectionMatrix * rayNDC;
            rayCamera.Z = -1.0f;
            rayCamera.W = 0.0f;

            var rayWorld4 = invViewMatrix * rayCamera;
            var rayWorld = new Vector3( rayWorld4.X, rayWorld4.Y, rayWorld4.Z );
            rayWorld.Normalize();

            var rayOrigin = new Vector3( invViewMatrix.M41, invViewMatrix.M42, invViewMatrix.M43 );
            mRaypickStart = rayOrigin;
            mRaypickEnd = rayOrigin + rayWorld * 10000f;

            var anySelected = false;

            Debug.WriteLine( rayOrigin );

            // TODO sorting

            float closestDistance = float.MaxValue;
            GLNode closestNode = null;
            GLMesh closestMesh = null;

            foreach ( var glNode in mModel.Nodes )
            {
                foreach ( var glMesh in glNode.Meshes )
                {
                    if ( glMesh.IsVisible && (glMesh.Mesh?.BoundingSphere.HasValue ?? false) )
                    {
                        //var sphere = glMesh.Mesh.BoundingSphere.Value;
                        //// Transform the sphere center to world space
                        //var sphereCenterWorld = System.Numerics.Vector3.Transform( sphere.Center, glNode.WorldTransform ).ToOpenTK();

                        //// Check for intersection
                        //if ( RayIntersectsSphere( rayOrigin, rayWorld, sphereCenterWorld, sphere.Radius, out float distance ) )
                        //{
                        //    // Check if this sphere is the closest
                        //    if ( distance < closestDistance )
                        //    {
                        //        closestDistance = distance;
                        //        closestMesh = glMesh;
                        //        closestNode = glNode;
                        //    }
                        //}
                        var boundingBox = glMesh.Mesh.BoundingBox.Value;

                        // Transform the bounding box to world space
                        var boxMinWorld = System.Numerics.Vector3.Transform( boundingBox.Min, glNode.WorldTransform ).ToOpenTK();
                        var boxMaxWorld = System.Numerics.Vector3.Transform( boundingBox.Max, glNode.WorldTransform ).ToOpenTK();

                        // Check for intersection with the bounding box
                        if ( RayIntersectsBox( rayOrigin, rayWorld, boxMinWorld, boxMaxWorld, out float distance ) )
                        {
                            // Check if this bounding box is the closest intersection
                            if ( distance < closestDistance )
                            {
                                closestDistance = distance;
                                closestMesh = glMesh;
                                closestNode = glNode;
                            }
                        }
                    }
                }
            }

            // Select the closest mesh if found
            //if ( closestMesh != null )
            //{
            //    SetSelection( closestMesh.Mesh );
            //    Debug.WriteLine( $"Selected {closestNode.Node.Name} mesh" );
            //    return true;
            //}

            return false;
        }

        protected override void OnMouseUp( System.Windows.Forms.MouseEventArgs e )
        {
            if ( e.Button == MouseButtons.Left )
                Raypick( e.X, e.Y );
        }

        protected override void OnMouseDown( System.Windows.Forms.MouseEventArgs e )
        {
            mLastMouseLocation = e.Location;
            base.OnMouseDown( e );
        }

        protected override void OnMouseMove( System.Windows.Forms.MouseEventArgs e )
        {
            if ( !mIsModelLoaded )
                return;
            bool left = e.Button.HasFlag( MouseButtons.Left );
            bool right = e.Button.HasFlag( MouseButtons.Right );
            bool middle = e.Button.HasFlag( MouseButtons.Middle );
            if ( left || right || middle )
            {
                var locationDelta = GetMouseLocationDelta( e.Location );

                if ( right )
                {
                    float multiplier = CalculateMultiplier();
                    mCamera.ModelTranslation = new Vector3(
                         mCamera.ModelTranslation.X + ( locationDelta.X / 3f ) * multiplier,
                         mCamera.ModelTranslation.Y - ( locationDelta.Y / 3f ) * multiplier,
                         mCamera.ModelTranslation.Z );
                }
                else if ( left )
                {
                    float multiplier = CalculateMultiplier();
                    mCamera.ModelRotation = new Vector3(
                        mCamera.ModelRotation.X + locationDelta.Y * 0.01f * multiplier,
                        mCamera.ModelRotation.Y + locationDelta.X * 0.01f * multiplier,
                        mCamera.ModelRotation.Z );
                }
                else if ( middle )
                {
                    float multiplier = CalculateMultiplier( 0.25f );
                    var translation = mCamera.ModelTranslation;
                    translation.Z -= locationDelta.Y * multiplier;
                    mCamera.ModelTranslation = translation;
                }
                Invalidate();
            }

            mLastMouseLocation = e.Location;
        }

        protected override void OnMouseWheel( System.Windows.Forms.MouseEventArgs e )
        {
            if ( !mIsModelLoaded )
                return;

            float multiplier = CalculateMultiplier( 0.25f );

            var translation = mCamera.ModelTranslation;
            translation.Z += (float)e.Delta * multiplier;
            mCamera.ModelTranslation = translation;

            Invalidate();
        }

        protected override void OnKeyDown( KeyEventArgs e )
        {
            if ( e.KeyCode == Keys.Space )
            {
                mCamera.ModelTranslation = Vector3.Zero;
                mCamera.ModelRotation = Vector3.Zero;
            }
            Invalidate();
        }

        public void ClearSelection()
        {
            mSelectedMesh = null;
            mSelectedMaterial = null;
            Invalidate();
        }

        public void SetSelection( Mesh data )
        {
            ClearSelection();
            mSelectedMesh = data;
        }

        public void SetSelection( Material data )
        {
            ClearSelection();
            mSelectedMaterial = data;
        }
    }
}
