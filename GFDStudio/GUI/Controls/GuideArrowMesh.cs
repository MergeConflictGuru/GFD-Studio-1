using System;
using System.Collections.Generic;
using System.Numerics;
using GFDLibrary.Rendering.OpenGL;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Matrix4 = OpenTK.Matrix4;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = OpenTK.Vector4;

namespace GFDStudio.GUI.Controls
{
    /// <summary>
    /// A small, high-poly, rounded 3D arrow used by the model viewer as an
    /// off-screen character locator. It is deliberately geometry rather than
    /// a screen-space icon so the lighting and depth of the viewer still read.
    /// </summary>
    internal sealed class GuideArrowMesh : IDisposable
    {
        private readonly GLMesh mBody;
        private readonly GLMesh mInset;
        private readonly GLMesh mGlow;

        public GuideArrowMesh()
        {
            mBody = CreateMesh( CreateArrowGeometry( 1.0f, 0.0f, 0.28f, 0.035f, 0.075f ) );
            mInset = CreateMesh( CreateArrowGeometry( 0.80f, 0.135f, 0.075f, 0.016f, 0.055f ) );
            mGlow = CreateMesh( CreateGlowGeometry() );
        }

        public void Draw( GLShaderProgram shader, Matrix4 view, Matrix4 projection, Matrix4 model, float opacity )
        {
            if ( opacity <= 0.001f )
                return;

            shader.Use();
            shader.SetUniform( "uView", view );
            shader.SetUniform( "uProjection", projection );
            shader.SetUniform( "uOpacity", opacity );

            DrawPart( shader, mBody, model,
                new Vector4( 0.90f, 0.92f, 0.94f, 1.0f ),
                OpenTK.Vector3.Zero, 0.0f );

            DrawPart( shader, mInset, model,
                new Vector4( 0.18f, 0.21f, 0.25f, 1.0f ),
                OpenTK.Vector3.Zero, 0.0f );

            DrawPart( shader, mGlow, model,
                new Vector4( 0.18f, 0.70f, 0.94f, 1.0f ),
                new OpenTK.Vector3( 0.16f, 0.72f, 1.0f ), 0.72f );
        }

        private static void DrawPart( GLShaderProgram shader, GLMesh mesh, Matrix4 model,
                                      Vector4 baseColor, OpenTK.Vector3 glowColor, float glowStrength )
        {
            shader.SetUniform( "uBaseColor", baseColor );
            shader.SetUniform( "uGlowColor", glowColor );
            shader.SetUniform( "uGlowStrength", glowStrength );
            mesh.Draw( model, shader );
        }

        private static GLMesh CreateMesh( MeshData data )
        {
            return new GLMesh(
                new GLVertexArray( data.Positions, data.Normals, null, null, data.Indices, PrimitiveType.Triangles ),
                new GuideArrowMaterial(),
                true );
        }

        private static MeshData CreateArrowGeometry( float scale, float centerY, float thickness,
                                                     float bevel, float cornerRadius )
        {
            var outline = CreateRoundedOutline( scale, cornerRadius );
            var builder = new MeshBuilder();
            var halfThickness = thickness * 0.5f;

            var rings = new[]
            {
                ( y: centerY - halfThickness, scale: 0.975f ),
                ( y: centerY - halfThickness + bevel, scale: 1.0f ),
                ( y: centerY + halfThickness - bevel, scale: 1.0f ),
                ( y: centerY + halfThickness, scale: 0.975f ),
            };

            var ringIndices = new int[rings.Length][];
            for ( var ringIndex = 0; ringIndex < rings.Length; ringIndex++ )
            {
                ringIndices[ringIndex] = new int[outline.Count];
                for ( var pointIndex = 0; pointIndex < outline.Count; pointIndex++ )
                {
                    var point = outline[pointIndex] * rings[ringIndex].scale;
                    ringIndices[ringIndex][pointIndex] = builder.AddVertex(
                        new Vector3( point.X, rings[ringIndex].y, point.Y ) );
                }
            }

            for ( var ringIndex = 0; ringIndex < rings.Length - 1; ringIndex++ )
            {
                for ( var pointIndex = 0; pointIndex < outline.Count; pointIndex++ )
                {
                    var nextPointIndex = ( pointIndex + 1 ) % outline.Count;
                    var a = ringIndices[ringIndex][pointIndex];
                    var b = ringIndices[ringIndex][nextPointIndex];
                    var c = ringIndices[ringIndex + 1][nextPointIndex];
                    var d = ringIndices[ringIndex + 1][pointIndex];
                    var center = ( builder.Positions[a] + builder.Positions[b] +
                                   builder.Positions[c] + builder.Positions[d] ) * 0.25f;
                    var outward = new Vector3( center.X, 0.0f, center.Z );

                    builder.AddOrientedTriangle( a, b, c, outward );
                    builder.AddOrientedTriangle( a, c, d, outward );
                }
            }

            var triangles = Triangulate( outline );
            foreach ( var triangle in triangles )
            {
                builder.AddOrientedTriangle(
                    ringIndices[3][triangle.A],
                    ringIndices[3][triangle.B],
                    ringIndices[3][triangle.C],
                    Vector3.UnitY );
                builder.AddOrientedTriangle(
                    ringIndices[0][triangle.C],
                    ringIndices[0][triangle.B],
                    ringIndices[0][triangle.A],
                    -Vector3.UnitY );
            }

            return builder.ToMeshData();
        }

        private static MeshData CreateGlowGeometry()
        {
            var builder = new MeshBuilder();
            const float bottomY = 0.178f;
            const float topY = 0.194f;
            const float width = 0.035f;

            AddRibbon( builder, new Vector2( 0.0f, -0.58f ), new Vector2( 0.0f, 0.60f ), width, bottomY, topY );
            AddRibbon( builder, new Vector2( 0.0f, 0.57f ), new Vector2( 0.44f, 0.02f ), width, bottomY, topY );
            AddRibbon( builder, new Vector2( 0.0f, 0.57f ), new Vector2( -0.44f, 0.02f ), width, bottomY, topY );

            return builder.ToMeshData();
        }

        private static void AddRibbon( MeshBuilder builder, Vector2 start, Vector2 end,
                                       float width, float bottomY, float topY )
        {
            var direction = Vector2.Normalize( end - start );
            var perpendicular = new Vector2( -direction.Y, direction.X ) * ( width * 0.5f );
            var points = new[]
            {
                start + perpendicular,
                start - perpendicular,
                end - perpendicular,
                end + perpendicular,
            };

            var bottom = new int[4];
            var top = new int[4];
            for ( var i = 0; i < points.Length; i++ )
            {
                bottom[i] = builder.AddVertex( new Vector3( points[i].X, bottomY, points[i].Y ) );
                top[i] = builder.AddVertex( new Vector3( points[i].X, topY, points[i].Y ) );
            }

            builder.AddOrientedTriangle( top[0], top[1], top[2], Vector3.UnitY );
            builder.AddOrientedTriangle( top[0], top[2], top[3], Vector3.UnitY );
            builder.AddOrientedTriangle( bottom[2], bottom[1], bottom[0], -Vector3.UnitY );
            builder.AddOrientedTriangle( bottom[3], bottom[2], bottom[0], -Vector3.UnitY );

            for ( var i = 0; i < 4; i++ )
            {
                var next = ( i + 1 ) % 4;
                var center = ( builder.Positions[bottom[i]] + builder.Positions[bottom[next]] +
                               builder.Positions[top[next]] + builder.Positions[top[i]] ) * 0.25f;
                var outward = new Vector3( center.X, 0.0f, center.Z );
                builder.AddOrientedTriangle( bottom[i], bottom[next], top[next], outward );
                builder.AddOrientedTriangle( bottom[i], top[next], top[i], outward );
            }
        }

        private static List<Vector2> CreateRoundedOutline( float scale, float cornerRadius )
        {
            // Counter-clockwise arrow outline, pointing along local +Z.
            var corners = new[]
            {
                new Vector2( -0.22f, -0.78f ),
                new Vector2(  0.22f, -0.78f ),
                new Vector2(  0.30f, -0.70f ),
                new Vector2(  0.30f, -0.15f ),
                new Vector2(  0.58f, -0.15f ),
                new Vector2(  0.72f,  0.00f ),
                new Vector2(  0.06f,  0.78f ),
                new Vector2( -0.06f,  0.78f ),
                new Vector2( -0.72f,  0.00f ),
                new Vector2( -0.58f, -0.15f ),
                new Vector2( -0.30f, -0.15f ),
                new Vector2( -0.30f, -0.70f ),
            };

            var outline = new List<Vector2>( corners.Length * 5 );
            for ( var i = 0; i < corners.Length; i++ )
            {
                var previous = corners[( i + corners.Length - 1 ) % corners.Length];
                var current = corners[i];
                var next = corners[( i + 1 ) % corners.Length];

                var previousDirection = Vector2.Normalize( previous - current );
                var nextDirection = Vector2.Normalize( next - current );
                var radius = MathF.Min( cornerRadius,
                    MathF.Min( ( previous - current ).Length(), ( next - current ).Length() ) * 0.28f );
                var start = current + previousDirection * radius;
                var end = current + nextDirection * radius;

                for ( var step = 0; step <= 4; step++ )
                {
                    var t = step / 4.0f;
                    var first = Vector2.Lerp( start, current, t );
                    var second = Vector2.Lerp( current, end, t );
                    outline.Add( Vector2.Lerp( first, second, t ) * scale );
                }
            }

            return outline;
        }

        private static List<Triangle> Triangulate( IReadOnlyList<Vector2> polygon )
        {
            var remaining = new List<int>( polygon.Count );
            for ( var i = 0; i < polygon.Count; i++ )
                remaining.Add( i );

            if ( SignedArea( polygon ) < 0.0f )
                remaining.Reverse();

            var triangles = new List<Triangle>( polygon.Count - 2 );
            var guard = polygon.Count * polygon.Count;
            while ( remaining.Count > 3 && guard-- > 0 )
            {
                var earFound = false;
                for ( var i = 0; i < remaining.Count; i++ )
                {
                    var previous = remaining[( i + remaining.Count - 1 ) % remaining.Count];
                    var current = remaining[i];
                    var next = remaining[( i + 1 ) % remaining.Count];
                    var a = polygon[previous];
                    var b = polygon[current];
                    var c = polygon[next];

                    if ( Cross( b - a, c - b ) <= 0.00001f )
                        continue;

                    var containsPoint = false;
                    for ( var j = 0; j < remaining.Count; j++ )
                    {
                        var candidate = remaining[j];
                        if ( candidate == previous || candidate == current || candidate == next )
                            continue;
                        if ( PointInTriangle( polygon[candidate], a, b, c ) )
                        {
                            containsPoint = true;
                            break;
                        }
                    }

                    if ( containsPoint )
                        continue;

                    triangles.Add( new Triangle( previous, current, next ) );
                    remaining.RemoveAt( i );
                    earFound = true;
                    break;
                }

                if ( !earFound )
                    break;
            }

            if ( remaining.Count == 3 )
                triangles.Add( new Triangle( remaining[0], remaining[1], remaining[2] ) );

            return triangles;
        }

        private static bool PointInTriangle( Vector2 point, Vector2 a, Vector2 b, Vector2 c )
        {
            var first = Cross( b - a, point - a );
            var second = Cross( c - b, point - b );
            var third = Cross( a - c, point - c );
            return first >= 0.0f && second >= 0.0f && third >= 0.0f;
        }

        private static float SignedArea( IReadOnlyList<Vector2> polygon )
        {
            var area = 0.0f;
            for ( var i = 0; i < polygon.Count; i++ )
            {
                var next = ( i + 1 ) % polygon.Count;
                area += polygon[i].X * polygon[next].Y - polygon[next].X * polygon[i].Y;
            }
            return area * 0.5f;
        }

        private static float Cross( Vector2 first, Vector2 second ) => first.X * second.Y - first.Y * second.X;

        public void Dispose()
        {
            mBody.Dispose();
            mInset.Dispose();
            mGlow.Dispose();
        }

        private readonly struct Triangle
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;

            public Triangle( int a, int b, int c )
            {
                A = a;
                B = b;
                C = c;
            }
        }

        private sealed class MeshBuilder
        {
            public List<Vector3> Positions { get; } = new List<Vector3>();
            private readonly List<uint> mIndices = new List<uint>();

            public int AddVertex( Vector3 position )
            {
                Positions.Add( position );
                return Positions.Count - 1;
            }

            public void AddOrientedTriangle( int a, int b, int c, Vector3 expectedNormal )
            {
                var first = Positions[b] - Positions[a];
                var second = Positions[c] - Positions[a];
                var normal = Vector3.Cross( first, second );
                if ( Vector3.Dot( normal, expectedNormal ) < 0.0f )
                    ( b, c ) = ( c, b );

                mIndices.Add( (uint)a );
                mIndices.Add( (uint)b );
                mIndices.Add( (uint)c );
            }

            public MeshData ToMeshData()
            {
                var normals = new Vector3[Positions.Count];
                for ( var i = 0; i < mIndices.Count; i += 3 )
                {
                    var a = (int)mIndices[i];
                    var b = (int)mIndices[i + 1];
                    var c = (int)mIndices[i + 2];
                    var normal = Vector3.Cross( Positions[b] - Positions[a], Positions[c] - Positions[a] );
                    if ( normal.LengthSquared() > 0.000001f )
                        normal = Vector3.Normalize( normal );
                    normals[a] += normal;
                    normals[b] += normal;
                    normals[c] += normal;
                }

                for ( var i = 0; i < normals.Length; i++ )
                    normals[i] = normals[i].LengthSquared() > 0.000001f ? Vector3.Normalize( normals[i] ) : Vector3.UnitY;

                return new MeshData( Positions.ToArray(), normals, mIndices.ToArray() );
            }
        }

        private readonly struct MeshData
        {
            public readonly Vector3[] Positions;
            public readonly Vector3[] Normals;
            public readonly uint[] Indices;

            public MeshData( Vector3[] positions, Vector3[] normals, uint[] indices )
            {
                Positions = positions;
                Normals = normals;
                Indices = indices;
            }
        }

        private sealed class GuideArrowMaterial : GLBaseMaterial
        {
            public GuideArrowMaterial()
            {
                // The indicator can be viewed from above, below, or edge-on while
                // the model camera is orbiting. Keep both sides available so the
                // real beveled geometry never disappears because of winding.
                EnableBackfaceCulling = false;
            }

            public override void Bind( GLShaderProgram shaderProgram )
            {
                // GLMesh calls the base non-virtual Unbind afterwards, so make the
                // state explicit for every part instead of relying on constructor
                // state left by the character material pass.
                GL.Disable( EnableCap.CullFace );
            }

            public override bool IsMaterialTransparent() => true;
        }
    }
}
