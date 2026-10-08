using System;
using System.Collections.Generic;
using System.Numerics;
using GFDLibrary.Models;
using OpenTK.Graphics.OpenGL4;

namespace GFDLibrary.Rendering.OpenGL;

public sealed class GLSkinningPalette : IDisposable
{
    private readonly int _buffer;
    internal int Buffer => _buffer;
    public int Texture {get;}
    public Matrix4x4[] Matrices {get;}
    public int FrameOffset {get;private set;}
    public int NextFrameOffset {get;private set;}
    public float FrameBlend {get;private set;}
    public GLSkinningPalette(int count)
    {
        Matrices=new Matrix4x4[count];_buffer=GL.GenBuffer();Texture=GL.GenTexture();
        GL.BindBuffer(BufferTarget.TextureBuffer,_buffer);
        GL.BufferData(BufferTarget.TextureBuffer,Math.Max(1,count)*64,IntPtr.Zero,BufferUsageHint.StreamDraw);
        GL.BindTexture(TextureTarget.TextureBuffer,Texture);
        GL.TexBuffer(TextureBufferTarget.TextureBuffer,SizedInternalFormat.Rgba32f,_buffer);
    }
    public void UploadFrames(Matrix4x4[] frames)
    {
        GL.BindBuffer(BufferTarget.TextureBuffer,_buffer);
        GL.BufferData(BufferTarget.TextureBuffer,frames.Length*64,frames,BufferUsageHint.StaticDraw);
    }
    public void SelectFrames(int first,int second,float blend)
    {
        FrameOffset=first*Matrices.Length*4;NextFrameOffset=second*Matrices.Length*4;FrameBlend=blend;
    }
    public void Update(List<Bone> bones,List<GLNode> nodes)
    {
        FrameOffset=0;NextFrameOffset=0;FrameBlend=0;
        for(int i=0;i<bones.Count;i++)Matrices[i]=bones[i].InverseBindMatrix*nodes[bones[i].NodeIndex].WorldTransform;
        GL.BindBuffer(BufferTarget.TextureBuffer,_buffer);
        if(Matrices.Length>0)GL.BufferData(BufferTarget.TextureBuffer,Matrices.Length*64,Matrices,BufferUsageHint.StreamDraw);
    }
    public void Dispose(){GL.DeleteTexture(Texture);GL.DeleteBuffer(_buffer);}
}

