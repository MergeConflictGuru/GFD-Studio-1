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
    internal readonly struct AnimationThumbnailRenderRequest
    {
        public AnimationThumbnailRenderRequest( Animation animation, double animationTime, int width, int height )
        {
            Animation = animation ?? throw new ArgumentNullException( nameof( animation ) );
            AnimationTime = animationTime;
            Width = Math.Max( 32, width );
            Height = Math.Max( 32, height );
        }

        public Animation Animation { get; }
        public double AnimationTime { get; }
        public int Width { get; }
        public int Height { get; }
    }

    internal sealed class AnimationThumbnailRenderBatch
    {
        public AnimationThumbnailRenderBatch( Bitmap atlas, IReadOnlyList<Rectangle> sourceRectangles )
        {
            Atlas = atlas ?? throw new ArgumentNullException( nameof( atlas ) );
            SourceRectangles = sourceRectangles ?? throw new ArgumentNullException( nameof( sourceRectangles ) );
        }

        public Bitmap Atlas { get; }
        public IReadOnlyList<Rectangle> SourceRectangles { get; }
    }

    public partial class ModelViewControl : GLControl
    {
        private static ModelViewControl sInstance;

        public static ModelViewControl Instance => sInstance ?? ( sInstance = new ModelViewControl() );

        
        private ShaderRegistry mShaderRegistry;
        private GLPerspectiveCamera mCamera;
        private readonly bool mCanRender = true;
        private readonly bool mThumbnailMode;
        private Point mLastMouseLocation;
        private Vector3 mRaypickStart;
        private Vector3 mRaypickEnd;
        private bool mOrbitAroundModel;
        private Vector3 mOrbitPivot;
        private Vector3 mOrbitPivotView;

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
        private double? mAnimationLoopStart;
        private double? mAnimationLoopEnd;
        private float mGuideArrowOpacity;
        private double mGuideArrowLastUpdateTime = -1.0;
        private int mThumbnailFramebuffer;
        private int mThumbnailColorTexture;
        private int mThumbnailDepthBuffer;
        private int mThumbnailTargetWidth;
        private int mThumbnailTargetHeight;
        private readonly Dictionary<Animation, ThumbnailCameraState> mThumbnailCameraStates = new();

        private readonly struct ThumbnailCameraState
        {
            public ThumbnailCameraState( int width, int height, GLPerspectiveCamera camera )
            {
                Width = width;
                Height = height;
                Translation = camera.Translation;
                Offset = camera.Offset;
                ModelTranslation = camera.ModelTranslation;
                ModelRotation = camera.ModelRotation;
            }

            public int Width { get; }
            public int Height { get; }
            public Vector3 Translation { get; }
            public Vector3 Offset { get; }
            public Vector3 ModelTranslation { get; }
            public Vector3 ModelRotation { get; }
        }

        private const float GuideArrowFadeTime = 0.24f;
        private const float GuideArrowFocusMargin = 1.12f;
        private const double GuideArrowFutureFrameSeconds = 3.0;
        private const int GuideArrowFutureSampleCount = 90;

        public Animation Animation { get; private set; }

        public Animation AnimationOverlay { get; private set; }

        public bool IsAnimationLoaded => Animation != null;

        public double? AnimationLoopStart => mAnimationLoopStart;
        public double? AnimationLoopEnd => mAnimationLoopEnd;

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
                        AnimationTime = mAnimationLoopStart ?? 0;
                        mModel?.UnloadAnimation();
                        ResetAnimationClock();
                        break;
                    case AnimationPlaybackState.Paused:
                        ResetAnimationClock();
                        break;
                    case AnimationPlaybackState.Playing:
                        if (mAnimationLoopStart.HasValue &&
                            (!double.IsFinite(AnimationTime) || AnimationTime < mAnimationLoopStart.Value ||
                             AnimationTime >= mAnimationLoopEnd.Value))
                        {
                            AnimationTime = mAnimationLoopStart.Value;
                        }
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

        private ModelViewControl() : this( false )
        {
        }

        internal ModelViewControl( bool thumbnailMode ) : base( new GLControlSettings
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
            mThumbnailMode = thumbnailMode;
            if ( thumbnailMode )
                ClearColor = System.Drawing.Color.FromArgb( 24, 24, 24 );

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

            MakeCurrent();

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

            mThumbnailCameraStates.Clear();

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

            if ( !mThumbnailMode )
                Invalidate();
        }

        public void LoadAnimation( Animation animation, bool reset = true )
        {
            ClearAnimationLoop();
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

        /// <summary>
        /// Renders a batch of live thumbnail poses through this one OpenGL context and one loaded
        /// target GLModel. The caller owns the returned bitmaps and should replace/dispose them on
        /// the next refresh. This is intentionally a batch API: result cards never create their own
        /// GL contexts or duplicate the target model's GPU resources.
        /// </summary>
        internal AnimationThumbnailRenderBatch RenderAnimationThumbnailBatch(
            IReadOnlyList<AnimationThumbnailRenderRequest> requests )
        {
            if ( requests == null )
                throw new ArgumentNullException( nameof( requests ) );
            if ( requests.Count == 0 )
                throw new ArgumentException( "At least one thumbnail is required.", nameof( requests ) );
            if ( !mCanRender || !mIsModelLoaded || mModel == null || mCamera == null )
                throw new InvalidOperationException( "The thumbnail renderer is not ready." );

            MakeCurrent();

            var oldAnimation = Animation;
            var oldAnimationOverlay = AnimationOverlay;
            var oldAnimationTime = mAnimationTime;
            var oldAspectRatio = mCamera.AspectRatio;
            var oldCameraTranslation = mCamera.Translation;
            var oldCameraOffset = mCamera.Offset;
            var oldModelTranslation = mCamera.ModelTranslation;
            var oldModelRotation = mCamera.ModelRotation;
            var oldViewport = new int[4];
            var oldFramebuffer = new int[1];
            GL.GetInteger( GetPName.Viewport, oldViewport );
            GL.GetInteger( GetPName.FramebufferBinding, oldFramebuffer );

            var cellWidth = requests.Max( request => request.Width );
            var cellHeight = requests.Max( request => request.Height );
            var columns = Math.Min( 8, requests.Count );
            var rows = ( requests.Count + columns - 1 ) / columns;
            var atlasWidth = cellWidth * columns;
            var atlasHeight = cellHeight * rows;
            var sourceRectangles = new Rectangle[requests.Count];
            for ( var index = 0; index < requests.Count; index++ )
            {
                var column = index % columns;
                var row = index / columns;
                sourceRectangles[index] = new Rectangle(
                    column * cellWidth,
                    row * cellHeight,
                    requests[index].Width,
                    requests[index].Height );
            }

            Bitmap atlas = null;
            try
            {
                EnsureThumbnailRenderTarget( atlasWidth, atlasHeight );
                GL.BindFramebuffer( FramebufferTarget.Framebuffer, mThumbnailFramebuffer );
                GL.ClearColor( ClearColor );
                GL.Enable( EnableCap.DepthTest );
                GL.DepthMask( true );
                GL.Disable( EnableCap.Blend );
                GL.Enable( EnableCap.ScissorTest );

                for ( var index = 0; index < requests.Count; index++ )
                {
                    var request = requests[index];
                    var sourceRectangle = sourceRectangles[index];
                    var row = index / columns;
                    var cellX = sourceRectangle.X;
                    var cellY = atlasHeight - ( row + 1 ) * cellHeight;

                    // Every card starts from the same neutral camera state. FocusOnGuideArrow then
                    // applies the same arrow-to-subject direction, motion bounds, and fit margin
                    // that a real click applies in the main viewer. The result is cached because
                    // recalculating 90 future poses for every card on every tick is CPU-expensive.
                    mCamera.Translation = oldCameraTranslation;
                    mCamera.Offset = oldCameraOffset;
                    mCamera.ModelTranslation = oldModelTranslation;
                    mCamera.ModelRotation = oldModelRotation;
                    mCamera.AspectRatio = (float)request.Width / request.Height;
                    GL.Scissor( cellX, cellY, cellWidth, cellHeight );
                    GL.Viewport(
                        sourceRectangle.X,
                        atlasHeight - sourceRectangle.Y - sourceRectangle.Height,
                        sourceRectangle.Width,
                        sourceRectangle.Height );
                    GL.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );

                    Animation = request.Animation;
                    AnimationOverlay = null;
                    mModel.LoadAnimation( request.Animation );
                    mAnimationTime = Math.Max( 0.0, request.AnimationTime );

                    if ( !mThumbnailCameraStates.TryGetValue( request.Animation, out var cameraState ) ||
                         cameraState.Width != request.Width || cameraState.Height != request.Height )
                    {
                        FocusOnGuideArrow( ResolveGuideArrowAnchor(), tight: true );
                        cameraState = new ThumbnailCameraState( request.Width, request.Height, mCamera );
                        mThumbnailCameraStates[request.Animation] = cameraState;
                    }
                    else
                    {
                        mCamera.Translation = cameraState.Translation;
                        mCamera.Offset = cameraState.Offset;
                        mCamera.ModelTranslation = cameraState.ModelTranslation;
                        mCamera.ModelRotation = cameraState.ModelRotation;
                    }

                    mModel.Draw( new DrawContext
                    {
                        ShaderRegistry = mShaderRegistry,
                        Camera = mCamera,
                        AnimationTime = mAnimationTime,
                        SelectedMaterial = null,
                        SelectedMesh = null
                    } );
                }

                GL.Disable( EnableCap.ScissorTest );
                GL.Flush();
                atlas = ReadThumbnailPixels( atlasWidth, atlasHeight );
                return new AnimationThumbnailRenderBatch( atlas, sourceRectangles );
            }
            catch
            {
                atlas?.Dispose();
                throw;
            }
            finally
            {
                Animation = oldAnimation;
                AnimationOverlay = oldAnimationOverlay;
                mAnimationTime = oldAnimationTime;
                mCamera.AspectRatio = oldAspectRatio;
                mCamera.Translation = oldCameraTranslation;
                mCamera.Offset = oldCameraOffset;
                mCamera.ModelTranslation = oldModelTranslation;
                mCamera.ModelRotation = oldModelRotation;
                if ( oldAnimation != null )
                    mModel.LoadAnimation( oldAnimation );
                else
                    mModel.UnloadAnimation();
                if ( oldAnimationOverlay != null )
                    mModel.LoadBlendAnimation( oldAnimationOverlay );

                GL.BindFramebuffer( FramebufferTarget.Framebuffer, oldFramebuffer[0] );
                GL.Viewport( oldViewport[0], oldViewport[1], oldViewport[2], oldViewport[3] );
                GL.Disable( EnableCap.ScissorTest );
                GL.ClearColor( ClearColor );
                if ( !mThumbnailMode )
                    Invalidate();
            }
        }

        private void EnsureThumbnailRenderTarget( int width, int height )
        {
            if ( mThumbnailFramebuffer != 0 &&
                 mThumbnailTargetWidth >= width && mThumbnailTargetHeight >= height )
                return;

            DisposeThumbnailRenderTarget();
            mThumbnailFramebuffer = GL.GenFramebuffer();
            mThumbnailColorTexture = GL.GenTexture();
            mThumbnailDepthBuffer = GL.GenRenderbuffer();
            mThumbnailTargetWidth = width;
            mThumbnailTargetHeight = height;

            GL.BindFramebuffer( FramebufferTarget.Framebuffer, mThumbnailFramebuffer );
            GL.BindTexture( TextureTarget.Texture2D, mThumbnailColorTexture );
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero );
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                             (int)TextureMinFilter.Nearest );
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                             (int)TextureMagFilter.Nearest );
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                mThumbnailColorTexture,
                0 );

            GL.BindRenderbuffer( RenderbufferTarget.Renderbuffer, mThumbnailDepthBuffer );
            GL.RenderbufferStorage(
                RenderbufferTarget.Renderbuffer,
                RenderbufferStorage.DepthComponent24,
                width,
                height );
            GL.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer,
                mThumbnailDepthBuffer );

            var status = GL.CheckFramebufferStatus( FramebufferTarget.Framebuffer );
            GL.BindTexture( TextureTarget.Texture2D, 0 );
            GL.BindRenderbuffer( RenderbufferTarget.Renderbuffer, 0 );
            if ( status != FramebufferErrorCode.FramebufferComplete )
            {
                DisposeThumbnailRenderTarget();
                throw new InvalidOperationException( "Could not create the live thumbnail render target." );
            }
        }

        private void DisposeThumbnailRenderTarget()
        {
            if ( mThumbnailColorTexture != 0 )
                GL.DeleteTexture( mThumbnailColorTexture );
            if ( mThumbnailDepthBuffer != 0 )
                GL.DeleteRenderbuffer( mThumbnailDepthBuffer );
            if ( mThumbnailFramebuffer != 0 )
                GL.DeleteFramebuffer( mThumbnailFramebuffer );

            mThumbnailColorTexture = 0;
            mThumbnailDepthBuffer = 0;
            mThumbnailFramebuffer = 0;
            mThumbnailTargetWidth = 0;
            mThumbnailTargetHeight = 0;
        }

        private Bitmap ReadThumbnailPixels( int width, int height )
        {
            var pixels = new byte[width * height * 4];
            GL.ReadPixels( 0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels );
            var bitmap = new Bitmap( width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb );
            var data = bitmap.LockBits(
                new Rectangle( 0, 0, width, height ),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb );
            try
            {
                for ( var y = 0; y < height; y++ )
                {
                    var sourceOffset = y * width * 4;
                    var destination = IntPtr.Add( data.Scan0, ( height - 1 - y ) * data.Stride );
                    Marshal.Copy( pixels, sourceOffset, destination, width * 4 );
                }
            }
            finally
            {
                bitmap.UnlockBits( data );
            }

            return bitmap;
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

                if ( mThumbnailFramebuffer != 0 )
                {
                    MakeCurrent();
                    DisposeThumbnailRenderTarget();
                }

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
                if (mAnimationLoopStart.HasValue && nextAnimationTime >= mAnimationLoopEnd.Value)
                    AnimationTime = mAnimationLoopStart.Value;
                else
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

            // The character-browser thumbnails use a second ModelViewControl on the
            // same UI thread. Rendering that control changes the thread's current GL
            // context, so never assume that this control is still current when WinForms
            // asks it to paint. Without this, the main surface can be cleared/swapped
            // through the hidden thumbnail context and appear as a solid gray viewport.
            MakeCurrent();
            UpdateViewport();

            if ( mThumbnailMode )
                RenderThumbnailFrame();
            else
                RenderFrame();
        }

        private void RenderThumbnailFrame()
        {
            GL.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );
            if ( mIsModelLoaded )
            {
                mModel.Draw( new DrawContext
                {
                    ShaderRegistry = mShaderRegistry,
                    Camera = mCamera,
                    AnimationTime = mAnimationTime,
                    SelectedMaterial = null,
                    SelectedMesh = null
                } );
            }
            SwapBuffers();
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

            GetGuideArrowTargetBounds( out var target, out var targetExtents, out var hasTargetGeometry );
            if ( !IsFinite( target ) )
            {
                // A malformed animated pose must not be allowed to turn the
                // navigation aid into NaNs. Keep the marker usable even when
                // the model itself cannot produce a meaningful target point.
                target = Vector3.Zero;
                targetExtents = new Vector3( 1.0f );
                hasTargetGeometry = false;
            }

            var targetClip = ProjectGuideArrowPoint( target, out var targetInFrontOfCamera );
            var targetInView = hasTargetGeometry && IsGuideArrowTargetInView( target, targetExtents );
            var desiredOpacity = CalculateGuideArrowOpacity( targetClip, targetInFrontOfCamera, targetInView );
            var opacity = UpdateGuideArrowOpacity( desiredOpacity );
            if ( opacity <= 0.001f )
                return;

            var anchor = ResolveGuideArrowAnchor();
            var direction = target - anchor;
            if ( !IsFinite( anchor ) || !IsFinite( direction ) )
                return;

            if ( direction.LengthSquared < 0.0001f )
            {
                // No geometry (or a zero-sized model bound) still deserves a
                // visible locator. Pick a stable camera-facing direction rather
                // than silently dropping the arrow.
                var inverseView = Matrix4.Invert( mCamera.View );
                var cameraDirection = Vector4.TransformRow(
                    new Vector4( 0.0f, 0.0f, 1.0f, 0.0f ), inverseView );
                direction = new Vector3( cameraDirection.X, cameraDirection.Y, cameraDirection.Z );
                if ( !IsFinite( direction ) || direction.LengthSquared < 0.0001f )
                    direction = Vector3.UnitZ;
            }

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

        private void GetGuideArrowTargetBounds( out Vector3 center, out Vector3 extents,
                                                out bool hasTargetGeometry )
        {
            var model = mModel?.ModelPack?.Model;
            var minimum = new System.Numerics.Vector3( float.PositiveInfinity );
            var maximum = new System.Numerics.Vector3( float.NegativeInfinity );
            hasTargetGeometry = false;

            // Use each mesh's eight local bound corners instead of walking all
            // vertices. GLModel.Draw has already rebuilt animated GLMeshes for
            // the current frame, so VertexBounds follows skinning and root
            // motion while this path remains cheap enough for every paint.
            if ( mModel != null )
            {
                foreach ( var node in mModel.Nodes )
                {
                    if ( !node.IsVisible )
                        continue;

                    foreach ( var mesh in node.Meshes )
                    {
                        if ( !mesh.IsVisible || ( mesh.VertexBounds ?? mesh.Mesh?.BoundingBox ) is not { } meshBounds ||
                             !IsFinite( meshBounds.Min ) || !IsFinite( meshBounds.Max ) )
                            continue;

                        for ( var x = -1; x <= 1; x += 2 )
                        for ( var y = -1; y <= 1; y += 2 )
                        for ( var z = -1; z <= 1; z += 2 )
                        {
                            var localPoint = new System.Numerics.Vector3(
                                x < 0 ? meshBounds.Min.X : meshBounds.Max.X,
                                y < 0 ? meshBounds.Min.Y : meshBounds.Max.Y,
                                z < 0 ? meshBounds.Min.Z : meshBounds.Max.Z );
                            var worldPoint = System.Numerics.Vector3.Transform(
                                localPoint, node.WorldTransform );
                            if ( !IsFinite( worldPoint ) )
                                continue;

                            minimum = System.Numerics.Vector3.Min( minimum, worldPoint );
                            maximum = System.Numerics.Vector3.Max( maximum, worldPoint );
                            hasTargetGeometry = true;
                        }
                    }
                }
            }

            if ( hasTargetGeometry )
            {
                center = ( ( minimum + maximum ) * 0.5f ).ToOpenTK();
                extents = ( ( maximum - minimum ) * 0.5f ).ToOpenTK();
                extents.X = MathF.Max( 0.001f, MathF.Abs( extents.X ) );
                extents.Y = MathF.Max( 0.001f, MathF.Abs( extents.Y ) );
                extents.Z = MathF.Max( 0.001f, MathF.Abs( extents.Z ) );
                return;
            }

            if ( model?.BoundingBox is { } bounds &&
                 IsFinite( bounds.Min ) && IsFinite( bounds.Max ) )
            {
                center = new Vector3(
                    ( bounds.Min.X + bounds.Max.X ) * 0.5f,
                    ( bounds.Min.Y + bounds.Max.Y ) * 0.5f,
                    ( bounds.Min.Z + bounds.Max.Z ) * 0.5f );
                extents = new Vector3(
                    MathF.Max( 0.001f, MathF.Abs( bounds.Max.X - bounds.Min.X ) * 0.5f ),
                    MathF.Max( 0.001f, MathF.Abs( bounds.Max.Y - bounds.Min.Y ) * 0.5f ),
                    MathF.Max( 0.001f, MathF.Abs( bounds.Max.Z - bounds.Min.Z ) * 0.5f ) );
                hasTargetGeometry = true;
                return;
            }

            if ( model?.BoundingSphere is { } sphere &&
                 IsFinite( sphere.Center ) && float.IsFinite( sphere.Radius ) )
            {
                center = new Vector3( sphere.Center.X, sphere.Center.Y, sphere.Center.Z );
                extents = new Vector3( MathF.Max( 0.001f, sphere.Radius ) );
                hasTargetGeometry = true;
                return;
            }

            center = Vector3.Zero;
            extents = new Vector3( 1.0f );
        }

        private void GetGuideArrowFramingBounds( out Vector3 center, out Vector3 minimum,
                                                 out Vector3 maximum )
        {
            GetGuideArrowTargetBounds( out center, out var currentExtents, out var hasTargetGeometry );
            minimum = center - currentExtents;
            maximum = center + currentExtents;

            if ( !hasTargetGeometry || !IsFinite( center ) || !IsFinite( currentExtents ) ||
                 mModel?.Animation is not { Duration: > 0 } animation )
                return;

            var currentTime = Math.Clamp( mAnimationTime, 0.0, (double)animation.Duration );
            var playbackSpeed = animation.Speed.GetValueOrDefault( 1.0f );
            if ( !float.IsFinite( playbackSpeed ) )
                playbackSpeed = 1.0f;
            playbackSpeed = MathF.Max( 0.0f, playbackSpeed );
            var futureAnimationDuration = GuideArrowFutureFrameSeconds * playbackSpeed;
            var averageCenter = center;
            var centerSampleCount = 1;

            try
            {
                // Evaluate the current pose through the same CPU path used for
                // future poses. This avoids relying on a stale GPU mesh bound if
                // the click happens between two paint callbacks.
                mModel.UpdateAnimationPose( currentTime );
                if ( mModel.TryGetWorldBounds( out var currentBounds ) &&
                     IsFinite( currentBounds.Min ) && IsFinite( currentBounds.Max ) )
                {
                    var currentMinimum = currentBounds.Min.ToOpenTK();
                    var currentMaximum = currentBounds.Max.ToOpenTK();
                    if ( IsFinite( currentMinimum ) && IsFinite( currentMaximum ) )
                    {
                        minimum = currentMinimum;
                        maximum = currentMaximum;
                        center = ( minimum + maximum ) * 0.5f;
                        averageCenter = center;
                        centerSampleCount = 1;
                    }
                }

                if ( futureAnimationDuration > 0.0001 )
                {
                    for ( var sample = 1; sample <= GuideArrowFutureSampleCount; sample++ )
                    {
                        var sampleTime = currentTime + futureAnimationDuration * sample / GuideArrowFutureSampleCount;
                        sampleTime %= animation.Duration;
                        mModel.UpdateAnimationPose( sampleTime );

                        if ( !mModel.TryGetWorldBounds( out var sampleBounds ) ||
                             !IsFinite( sampleBounds.Min ) || !IsFinite( sampleBounds.Max ) )
                            continue;

                        var sampleMinimum = sampleBounds.Min.ToOpenTK();
                        var sampleMaximum = sampleBounds.Max.ToOpenTK();
                        if ( !IsFinite( sampleMinimum ) || !IsFinite( sampleMaximum ) )
                            continue;

                        averageCenter += ( sampleMinimum + sampleMaximum ) * 0.5f;
                        centerSampleCount++;

                        minimum = new Vector3(
                            MathF.Min( minimum.X, sampleMinimum.X ),
                            MathF.Min( minimum.Y, sampleMinimum.Y ),
                            MathF.Min( minimum.Z, sampleMinimum.Z ) );
                        maximum = new Vector3(
                            MathF.Max( maximum.X, sampleMaximum.X ),
                            MathF.Max( maximum.Y, sampleMaximum.Y ),
                            MathF.Max( maximum.Z, sampleMaximum.Z ) );
                    }
                }
            }
            catch ( Exception exception )
            {
                // Keep the click action usable for malformed animation data;
                // the current-pose bounds are still a safe framing fallback.
                Trace.TraceWarning( $"Could not pre-calculate guide-arrow framing bounds: {exception.Message}" );
            }
            finally
            {
                // Center on the average body position across all valid poses,
                // while the min/max bounds above still cover the full motion.
                if ( centerSampleCount > 0 )
                    center = averageCenter / centerSampleCount;

                // Sampling only changes the in-memory pose. Restore the pose
                // that is actually displayed before changing the camera.
                mModel.UpdateAnimationPose( currentTime );
            }
        }

        public void SetAnimationLoop(double? start, double? end)
        {
            if (Animation == null || !start.HasValue || !end.HasValue ||
                !double.IsFinite(start.Value) || !double.IsFinite(end.Value))
            {
                ClearAnimationLoop();
                return;
            }

            var duration = Math.Max(0d, (double)Animation.Duration);
            var loopStart = Math.Clamp(start.Value, 0d, duration);
            var loopEnd = Math.Clamp(end.Value, loopStart, duration);
            if (loopEnd <= loopStart)
            {
                ClearAnimationLoop();
                return;
            }

            mAnimationLoopStart = loopStart;
            mAnimationLoopEnd = loopEnd;
            if (!double.IsFinite(AnimationTime) || AnimationTime < loopStart || AnimationTime >= loopEnd)
                AnimationTime = loopStart;
            else
                Invalidate();
        }

        public void ClearAnimationLoop()
        {
            mAnimationLoopStart = null;
            mAnimationLoopEnd = null;
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
                if ( !IsFinite( point ) )
                    continue;

                var viewPosition = Vector4.TransformRow( new Vector4( point, 1.0f ), mCamera.View );
                if ( !IsFinite( viewPosition ) )
                    continue;

                if ( viewPosition.Z >= 0.0f || viewPosition.W <= 0.0001f )
                    continue;

                hasFrontPoint = true;
                var clipPosition = Vector4.TransformRow( viewPosition, mCamera.Projection );
                if ( !IsFinite( clipPosition ) )
                    continue;

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
            if ( mModel?.ModelPack?.Model?.BoundingSphere is { } sphere && float.IsFinite( sphere.Radius ) )
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
            var clipPosition = Vector4.TransformRow( viewPosition, mCamera.Projection );
            return IsFinite( clipPosition ) ? clipPosition : Vector4.Zero;
        }

        private static bool IsFinite( System.Numerics.Vector3 value ) =>
            float.IsFinite( value.X ) && float.IsFinite( value.Y ) && float.IsFinite( value.Z );

        private static bool IsFinite( Vector3 value ) =>
            float.IsFinite( value.X ) && float.IsFinite( value.Y ) && float.IsFinite( value.Z );

        private static bool IsFinite( Vector4 value ) =>
            float.IsFinite( value.X ) && float.IsFinite( value.Y ) &&
            float.IsFinite( value.Z ) && float.IsFinite( value.W );

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
            if ( !inFrontOfCamera || !IsFinite( clipPosition ) || clipPosition.W <= 0.0001f )
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

        private bool TryHitGuideArrow( Point location, out Vector3 anchor )
        {
            anchor = Vector3.Zero;
            if ( !mIsModelLoaded || mGuideArrow == null || mShaderRegistry?.mGuideArrowShader == null ||
                 Width <= 0 || Height <= 0 )
                return false;

            GetGuideArrowTargetBounds( out var target, out var targetExtents, out var hasTargetGeometry );
            if ( !IsFinite( target ) )
                return false;

            var targetClip = ProjectGuideArrowPoint( target, out var targetInFrontOfCamera );
            var targetInView = hasTargetGeometry && IsGuideArrowTargetInView( target, targetExtents );
            var desiredOpacity = CalculateGuideArrowOpacity( targetClip, targetInFrontOfCamera, targetInView );
            if ( desiredOpacity <= 0.001f && mGuideArrowOpacity <= 0.02f )
                return false;

            anchor = ResolveGuideArrowAnchor();
            var anchorClip = ProjectGuideArrowPoint( anchor, out var anchorInFrontOfCamera );
            if ( !anchorInFrontOfCamera || !IsFinite( anchorClip ) || anchorClip.W <= 0.0001f )
                return false;

            var normalizedX = anchorClip.X / anchorClip.W;
            var normalizedY = anchorClip.Y / anchorClip.W;
            if ( !float.IsFinite( normalizedX ) || !float.IsFinite( normalizedY ) )
                return false;

            var anchorX = ( normalizedX + 1.0f ) * 0.5f * Width;
            var anchorY = ( 1.0f - normalizedY ) * 0.5f * Height;

            // The arrow is deliberately large enough to be useful at the edge of
            // the viewport. Derive the hit ellipse from the same depth-scaled size
            // used for rendering, then add a small forgiving screen-space margin.
            var viewAnchor = Vector4.TransformRow( new Vector4( anchor, 1.0f ), mCamera.View );
            var viewDepth = MathF.Max( 0.25f, -viewAnchor.Z );
            var scale = GetGuideArrowScale( anchor );
            var radiusX = scale * 1.10f * MathF.Abs( mCamera.Projection.M11 ) / viewDepth * Width * 0.5f + 10.0f;
            var radiusY = scale * 1.10f * MathF.Abs( mCamera.Projection.M22 ) / viewDepth * Height * 0.5f + 10.0f;
            radiusX = MathF.Max( 24.0f, radiusX );
            radiusY = MathF.Max( 24.0f, radiusY );

            var deltaX = (float)location.X - anchorX;
            var deltaY = (float)location.Y - anchorY;
            return ( deltaX * deltaX ) / ( radiusX * radiusX ) +
                   ( deltaY * deltaY ) / ( radiusY * radiusY ) <= 1.0f;
        }

        internal void FocusOnGuideArrow( Vector3 anchor, bool tight = false )
        {
            // The hit anchor identifies which arrow was clicked, but framing itself is
            // shared with double-click and thumbnail rendering. Keep the current camera
            // position, then look from it at the sampled motion center.
            _ = anchor;
            FocusModelMotionFromCurrentCamera( tight );
        }

        private float CalculateGuideArrowFitDistance( Vector3 targetCenter, Vector3 targetMinimum,
                                                      Vector3 targetMaximum, Matrix4 rotation,
                                                      float focusMargin = GuideArrowFocusMargin,
                                                      float extraDistance = 0.15f )
        {
            var verticalFov = MathHelper.DegreesToRadians( mCamera.FieldOfView ) * 0.5f;
            var tangentVertical = MathF.Max( 0.0001f, MathF.Tan( verticalFov ) );
            var tangentHorizontal = MathF.Max( 0.0001f, tangentVertical * MathF.Max( 0.1f, mCamera.AspectRatio ) );
            var requiredDistance = mCamera.ZNear + 0.1f;

            for ( var x = -1; x <= 1; x += 2 )
            for ( var y = -1; y <= 1; y += 2 )
            for ( var z = -1; z <= 1; z += 2 )
            {
                var relative = new Vector4(
                    ( x < 0 ? targetMinimum.X : targetMaximum.X ) - targetCenter.X,
                    ( y < 0 ? targetMinimum.Y : targetMaximum.Y ) - targetCenter.Y,
                    ( z < 0 ? targetMinimum.Z : targetMaximum.Z ) - targetCenter.Z,
                    0.0f );
                var viewRelative = Vector4.TransformRow( relative, rotation );

                // The target centre will be at -distance on camera Z. Account for
                // the corner's depth before solving the horizontal/vertical FOV
                // inequalities, so elongated poses fit from oblique viewpoints too.
                requiredDistance = MathF.Max( requiredDistance,
                    viewRelative.Z + MathF.Abs( viewRelative.X ) / tangentHorizontal );
                requiredDistance = MathF.Max( requiredDistance,
                    viewRelative.Z + MathF.Abs( viewRelative.Y ) / tangentVertical );
                requiredDistance = MathF.Max( requiredDistance, viewRelative.Z + mCamera.ZNear + 0.1f );
            }

            return MathF.Max( mCamera.ZNear + 0.5f,
                requiredDistance * focusMargin + extraDistance );
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

        private bool TryCreateMouseRay( int mouseX, int mouseY, out Vector3 rayOrigin, out Vector3 rayDirection )
        {
            rayOrigin = Vector3.Zero;
            rayDirection = Vector3.Zero;
            if ( mCamera == null || ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0 )
                return false;

            var x = ( 2.0f * mouseX ) / ClientRectangle.Width - 1.0f;
            var y = 1.0f - ( 2.0f * mouseY ) / ClientRectangle.Height;
            var rayNDC = new Vector4( x, y, -1.0f, 1.0f );
            var invProjectionMatrix = Matrix4.Invert( mCamera.Projection );
            var invViewMatrix = Matrix4.Invert( mCamera.View );
            // The renderer uses row-vector transforms on the C# side. The
            // matrices are uploaded without transposition, which makes the
            // shader's column-vector multiplication equivalent to
            // world * view * projection here. Unproject in that same order;
            // multiplying the matrix on the left mirrors the ray and causes
            // character hits to miss even though projected guide-arrow hits
            // still work.
            var rayCamera = Vector4.TransformRow( rayNDC, invProjectionMatrix );
            rayCamera.Z = -1.0f;
            rayCamera.W = 0.0f;

            var rayWorld4 = Vector4.TransformRow( rayCamera, invViewMatrix );
            rayDirection = new Vector3( rayWorld4.X, rayWorld4.Y, rayWorld4.Z );
            if ( !IsFinite( rayDirection ) || rayDirection.LengthSquared < 0.0001f )
                return false;
            rayDirection.Normalize();
            var rayOrigin4 = Vector4.TransformRow( Vector4.UnitW, invViewMatrix );
            rayOrigin = new Vector3( rayOrigin4.X, rayOrigin4.Y, rayOrigin4.Z );
            return IsFinite( rayOrigin );
        }

        private bool TryGetModelOrbitPivot( int mouseX, int mouseY, out Vector3 pivot )
        {
            pivot = Vector3.Zero;
            return mIsModelLoaded &&
                   TryCreateMouseRay( mouseX, mouseY, out var rayOrigin, out var rayDirection ) &&
                   TryIntersectCurrentModel( rayOrigin, rayDirection, out pivot );
        }

        private bool TryIntersectCurrentModel( Vector3 rayOrigin, Vector3 rayDirection,
                                               out Vector3 hitPoint )
        {
            hitPoint = Vector3.Zero;
            if ( !mIsModelLoaded || mModel == null || !IsFinite( rayOrigin ) ||
                 !IsFinite( rayDirection ) || rayDirection.LengthSquared < 0.0001f )
                return false;

            var closestDistance = float.PositiveInfinity;
            var foundHit = false;
            var rayOriginNumerics = new System.Numerics.Vector3( rayOrigin.X, rayOrigin.Y, rayOrigin.Z );
            var rayDirectionNumerics = new System.Numerics.Vector3( rayDirection.X, rayDirection.Y, rayDirection.Z );

            foreach ( var glNode in mModel.Nodes )
            {
                if ( !glNode.IsVisible || !System.Numerics.Matrix4x4.Invert( glNode.WorldTransform, out var inverseWorld ) )
                    continue;

                // Intersect in the node's local space so the exact vertex
                // positions can be used directly. VertexPositions is the data
                // uploaded by GLMesh, so animated meshes are tested in the
                // pose currently displayed rather than in their bind pose.
                var localOriginNumerics = System.Numerics.Vector3.Transform( rayOriginNumerics, inverseWorld );
                var localDirectionNumerics = System.Numerics.Vector3.TransformNormal( rayDirectionNumerics, inverseWorld );
                if ( !IsFinite( localOriginNumerics ) || !IsFinite( localDirectionNumerics ) ||
                     localDirectionNumerics.LengthSquared() < 0.0000001f )
                    continue;
                localDirectionNumerics = System.Numerics.Vector3.Normalize( localDirectionNumerics );

                var localOrigin = localOriginNumerics.ToOpenTK();
                var localDirection = localDirectionNumerics.ToOpenTK();
                foreach ( var glMesh in glNode.Meshes )
                {
                    var mesh = glMesh.Mesh;
                    var positions = glMesh.VertexPositions;
                    var triangles = mesh?.Triangles;
                    if ( !glMesh.IsVisible || positions == null || triangles == null || triangles.Length == 0 )
                        continue;

                    // This is only a broad-phase rejection. The final answer
                    // below always comes from a triangle intersection.
                    if ( glMesh.VertexBounds is not { } vertexBounds ||
                         !IsFinite( vertexBounds.Min ) || !IsFinite( vertexBounds.Max ) ||
                         !RayIntersectsBox( localOrigin, localDirection,
                                            vertexBounds.Min.ToOpenTK(), vertexBounds.Max.ToOpenTK(), out _ ) )
                        continue;

                    foreach ( var triangle in triangles )
                    {
                        if ( triangle.A >= (uint)positions.Length || triangle.B >= (uint)positions.Length ||
                             triangle.C >= (uint)positions.Length )
                            continue;

                        if ( !TryIntersectTriangle( localOriginNumerics, localDirectionNumerics,
                                                    positions[(int)triangle.A], positions[(int)triangle.B],
                                                    positions[(int)triangle.C], out var localDistance ) )
                            continue;

                        var localHit = localOriginNumerics + localDirectionNumerics * localDistance;
                        var worldHitNumerics = System.Numerics.Vector3.Transform( localHit, glNode.WorldTransform );
                        if ( !IsFinite( worldHitNumerics ) )
                            continue;

                        var worldHit = worldHitNumerics.ToOpenTK();
                        var worldDistance = Vector3.Dot( worldHit - rayOrigin, rayDirection );
                        if ( !float.IsFinite( worldDistance ) || worldDistance < 0.0f ||
                             worldDistance >= closestDistance )
                            continue;

                        closestDistance = worldDistance;
                        hitPoint = worldHit;
                        foundHit = true;
                    }
                }
            }

            return foundHit && IsFinite( hitPoint );
        }

        private static bool TryIntersectTriangle( System.Numerics.Vector3 rayOrigin,
                                                   System.Numerics.Vector3 rayDirection,
                                                   System.Numerics.Vector3 vertex0,
                                                   System.Numerics.Vector3 vertex1,
                                                   System.Numerics.Vector3 vertex2,
                                                   out float distance )
        {
            distance = 0.0f;
            const float epsilon = 0.000001f;
            var edge1 = vertex1 - vertex0;
            var edge2 = vertex2 - vertex0;
            var pVector = System.Numerics.Vector3.Cross( rayDirection, edge2 );
            var determinant = System.Numerics.Vector3.Dot( edge1, pVector );
            if ( MathF.Abs( determinant ) < epsilon )
                return false;

            var inverseDeterminant = 1.0f / determinant;
            var tVector = rayOrigin - vertex0;
            var u = System.Numerics.Vector3.Dot( tVector, pVector ) * inverseDeterminant;
            if ( u < 0.0f || u > 1.0f )
                return false;

            var qVector = System.Numerics.Vector3.Cross( tVector, edge1 );
            var v = System.Numerics.Vector3.Dot( rayDirection, qVector ) * inverseDeterminant;
            if ( v < 0.0f || u + v > 1.0f )
                return false;

            distance = System.Numerics.Vector3.Dot( edge2, qVector ) * inverseDeterminant;
            return float.IsFinite( distance ) && distance >= 0.0f;
        }

        private Matrix4 GetModelOrbitRotation()
        {
            return Matrix4.CreateRotationY( mCamera.ModelRotation.Y ) *
                   Matrix4.CreateRotationX( mCamera.ModelRotation.X ) *
                   Matrix4.CreateRotationZ( mCamera.ModelRotation.Z );
        }

        private void BeginModelOrbit( Vector3 pivot )
        {
            mOrbitAroundModel = true;
            mOrbitPivot = pivot;

            var transformedPivot = Vector4.TransformRow(
                new Vector4( pivot + mCamera.Offset, 1.0f ),
                GetModelOrbitRotation() );
            mOrbitPivotView = new Vector3(
                transformedPivot.X + mCamera.ModelTranslation.X - mCamera.Translation.X,
                transformedPivot.Y + mCamera.ModelTranslation.Y - mCamera.Translation.Y,
                transformedPivot.Z + mCamera.ModelTranslation.Z - mCamera.Translation.Z );
        }

        private void KeepModelOrbitPivotInPlace()
        {
            if ( !mOrbitAroundModel )
                return;

            var transformedPivot = Vector4.TransformRow(
                new Vector4( mOrbitPivot + mCamera.Offset, 1.0f ),
                GetModelOrbitRotation() );
            mCamera.ModelTranslation = new Vector3(
                mOrbitPivotView.X - transformedPivot.X + mCamera.Translation.X,
                mOrbitPivotView.Y - transformedPivot.Y + mCamera.Translation.Y,
                mOrbitPivotView.Z - transformedPivot.Z + mCamera.Translation.Z );
        }

        private void FocusModelFromDoubleClick()
        {
            if ( !mIsModelLoaded )
                return;

            FocusModelMotionFromCurrentCamera();
        }

        private void FocusModelMotionFromCurrentCamera( bool tight = false )
        {
            GetGuideArrowFramingBounds( out var target, out var targetMinimum, out var targetMaximum );
            if ( !IsFinite( target ) || !IsFinite( targetMinimum ) || !IsFinite( targetMaximum ) )
                return;

            // The camera position is the invariant. In this renderer the model transform is
            // applied around a fixed camera Translation, so recover the camera position in
            // model space before choosing a new look direction.
            var currentRotation = GetModelOrbitRotation();
            var inverseCurrentRotation = Matrix4.Invert( currentRotation );
            var cameraPositionWithOffset = Vector4.TransformRow(
                new Vector4(
                    mCamera.Translation.X - mCamera.ModelTranslation.X,
                    mCamera.Translation.Y - mCamera.ModelTranslation.Y,
                    mCamera.Translation.Z - mCamera.ModelTranslation.Z,
                    1.0f ),
                inverseCurrentRotation );
            var currentCameraPosition = new Vector3(
                cameraPositionWithOffset.X - mCamera.Offset.X,
                cameraPositionWithOffset.Y - mCamera.Offset.Y,
                cameraPositionWithOffset.Z - mCamera.Offset.Z );

            var forward = target - currentCameraPosition;
            var currentDistance = forward.Length;
            if ( !IsFinite( forward ) || currentDistance < 0.0001f )
                return;
            forward /= currentDistance;

            // Build the new view direction from the current camera position to the future
            // motion center. This deliberately changes yaw/pitch; preserving rotation here
            // would look at the old point rather than looking at the newly calculated center.
            var horizontalLength = MathF.Sqrt( forward.X * forward.X + forward.Z * forward.Z );
            var pitch = MathF.Atan2( -forward.Y, horizontalLength );
            var yaw = horizontalLength > 0.0001f
                ? MathF.Atan2( forward.X, -forward.Z )
                : 0.0f;
            var rotation = Matrix4.CreateRotationY( yaw ) * Matrix4.CreateRotationX( pitch );

            var fitDistance = CalculateGuideArrowFitDistance(
                target, targetMinimum, targetMaximum, rotation,
                tight ? 1.0f : GuideArrowFocusMargin,
                tight ? 0.0f : 0.15f );
            var distance = tight ? fitDistance : MathF.Max( currentDistance, fitDistance );
            var focusedCameraPosition = distance > currentDistance
                ? target - forward * distance
                : currentCameraPosition;

            // Keep the selected camera position while changing its look direction. Solving
            // ModelTranslation from that position avoids the old grid-anchor origin jump.
            var transformedCameraPosition = Vector4.TransformRow(
                new Vector4( focusedCameraPosition + mCamera.Offset, 1.0f ), rotation );
            mCamera.ModelRotation = new Vector3( pitch, yaw, 0.0f );
            mCamera.ModelTranslation = new Vector3(
                mCamera.Translation.X - transformedCameraPosition.X,
                mCamera.Translation.Y - transformedCameraPosition.Y,
                mCamera.Translation.Z - transformedCameraPosition.Z );
            Invalidate();
        }

        public bool RayIntersectsBox( Vector3 rayOrigin, Vector3 rayDir, Vector3 boxMin, Vector3 boxMax, out float distance )
        {
            var tMin = 0.0f;
            var tMax = float.PositiveInfinity;

            bool UpdateInterval( float origin, float direction, float minimum, float maximum )
            {
                if ( MathF.Abs( direction ) < 0.000001f )
                    return origin >= minimum && origin <= maximum;

                var near = ( minimum - origin ) / direction;
                var far = ( maximum - origin ) / direction;
                if ( near > far ) (near, far) = (far, near);
                tMin = MathF.Max( tMin, near );
                tMax = MathF.Min( tMax, far );
                return tMin <= tMax;
            }

            if ( !UpdateInterval( rayOrigin.X, rayDir.X, boxMin.X, boxMax.X ) ||
                 !UpdateInterval( rayOrigin.Y, rayDir.Y, boxMin.Y, boxMax.Y ) ||
                 !UpdateInterval( rayOrigin.Z, rayDir.Z, boxMin.Z, boxMax.Z ) )
            {
                distance = 0.0f;
                return false;
            }

            distance = tMin;
            return float.IsFinite( distance ) && tMax >= 0.0f;
        }

        private bool Raypick(int mouseX, int mouseY)
        {
            if ( !TryCreateMouseRay( mouseX, mouseY, out var rayOrigin, out var rayDirection ) )
                return false;

            mRaypickStart = rayOrigin;
            mRaypickEnd = rayOrigin + rayDirection * 10000f;
            return TryIntersectCurrentModel( rayOrigin, rayDirection, out _ );
        }

        protected override void OnMouseUp( System.Windows.Forms.MouseEventArgs e )
        {
            if ( e.Button == MouseButtons.Left )
            {
                if ( TryHitGuideArrow( e.Location, out var anchor ) )
                    FocusOnGuideArrow( anchor );
                else
                    Raypick( e.X, e.Y );

                mOrbitAroundModel = false;
            }
        }

        protected override void OnMouseDown( System.Windows.Forms.MouseEventArgs e )
        {
            mLastMouseLocation = e.Location;
            mOrbitAroundModel = false;
            if ( e.Button == MouseButtons.Left && TryGetModelOrbitPivot( e.X, e.Y, out var pivot ) )
                BeginModelOrbit( pivot );
            base.OnMouseDown( e );
        }

        protected override void OnMouseDoubleClick( System.Windows.Forms.MouseEventArgs e )
        {
            if ( e.Button == MouseButtons.Left && TryGetModelOrbitPivot( e.X, e.Y, out _ ) )
            {
                FocusModelFromDoubleClick();
                mOrbitAroundModel = false;
                Invalidate();
                return;
            }

            base.OnMouseDoubleClick( e );
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
                    if ( !mOrbitAroundModel && TryGetModelOrbitPivot( e.X, e.Y, out var pivot ) )
                        BeginModelOrbit( pivot );

                    float multiplier = CalculateMultiplier();
                    mCamera.ModelRotation = new Vector3(
                        mCamera.ModelRotation.X + locationDelta.Y * 0.01f * multiplier,
                        mCamera.ModelRotation.Y + locationDelta.X * 0.01f * multiplier,
                        mCamera.ModelRotation.Z );
                    KeepModelOrbitPivotInPlace();
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
