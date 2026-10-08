using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using OpenTK.Graphics.OpenGL4;

namespace GFDLibrary.Rendering.OpenGL;

// Authored local PRS keys only. No sampled poses or frame-rate conversion.
internal sealed class AuthoredAnimationKeys
{
    public ushort[] Values;
    public float[] Times;
    public int[] Nodes,Layers;
    public long Bytes => Values.LongLength*2+Times.LongLength*4+Nodes.LongLength*4+Layers.LongLength*4;
    public static AuthoredAnimationKeys Copy(Model model,Animation animation,CancellationToken cancellation)
    {
        var nodes=model.Nodes.ToArray();
        var nodeIndices=nodes.Select((node,index)=>(node,index)).ToDictionary(item=>item.node,item=>item.index);
        var controllers=animation.Controllers.Where(c=>c.TargetKind==TargetKind.Node).ToLookup(c=>c.TargetName);
        var values=new List<ushort>();var times=new List<float>();var metadata=new List<int>();var layers=new List<int>();
        void Vector(float x,float y,float z,float w)
        {
            foreach(float value in new[]{x,y,z,w})
            {
                var half=(Half)value;
                if(!Half.IsFinite(half))throw new InvalidOperationException("Animation value exceeds half-float range.");
                values.Add(BitConverter.HalfToUInt16Bits(half));
            }
        }
        void Pose(Vector3 position,Quaternion rotation,Vector3 scale,int flags)
        {
            Vector(position.X,position.Y,position.Z,flags);
            Vector(rotation.X,rotation.Y,rotation.Z,rotation.W);
            Vector(scale.X,scale.Y,scale.Z,0);
        }
        foreach(var node in nodes)Pose(node.Translation,node.Rotation,node.Scale,7);
        // Keys follow the node defaults. Layer scale vectors follow each layer's keys.
        foreach(var node in nodes)
        {
            cancellation.ThrowIfCancellationRequested();
            int firstLayer=layers.Count/4;
            foreach(var controller in controllers[node.Name])foreach(var layer in controller.Layers)
            {
                if(!layer.HasPRSKeyFrames)continue;
                var keys=layer.Keys.OfType<PRSKey>().OrderBy(key=>key.Time).ToArray();
                if(keys.Length==0)continue;
                int firstTime=times.Count,firstValue=values.Count/4;
                foreach(var key in keys)
                {
                    times.Add(key.Time);
                    Pose(key.Position,key.Rotation,key.Scale,(key.HasPosition?1:0)|(key.HasRotation?2:0)|(key.HasScale?4:0));
                }
                int scaleOffset=values.Count/4;
                Vector(layer.PositionScale.X,layer.PositionScale.Y,layer.PositionScale.Z,0);
                Vector(layer.ScaleScale.X,layer.ScaleScale.Y,layer.ScaleScale.Z,0);
                layers.Add(firstTime);layers.Add(keys.Length);layers.Add(firstValue);layers.Add(scaleOffset);
            }
            metadata.Add(node.Parent==null?-1:nodeIndices[node.Parent]);metadata.Add(firstLayer);
            metadata.Add(layers.Count/4-firstLayer);metadata.Add(0);
        }
        foreach(var bone in model.Bones){metadata.Add(bone.NodeIndex);metadata.Add(0);metadata.Add(0);metadata.Add(0);}
        return new AuthoredAnimationKeys {Values=values.ToArray(),Times=times.ToArray(),Nodes=metadata.ToArray(),Layers=layers.ToArray()};
    }
}

internal sealed class GLAuthoredAnimation : IDisposable
{
    private readonly int[] _buffers=new int[4],_textures=new int[4];
    public long Bytes {get;}
    public long LastUse;
    public GLAuthoredAnimation(AuthoredAnimationKeys keys)
    {
        Bytes=keys.Bytes;
        Upload(0,keys.Values,SizedInternalFormat.Rgba16f,2);
        Upload(1,keys.Times,SizedInternalFormat.R32f,4);
        Upload(2,keys.Nodes,SizedInternalFormat.Rgba32i,4);
        Upload(3,keys.Layers,SizedInternalFormat.Rgba32i,4);
    }
    private void Upload<T>(int slot,T[] data,SizedInternalFormat format,int elementBytes) where T:struct
    {
        _buffers[slot]=GL.GenBuffer();_textures[slot]=GL.GenTexture();
        GL.BindBuffer(BufferTarget.TextureBuffer,_buffers[slot]);
        if(data.Length>0)GL.BufferData(BufferTarget.TextureBuffer,data.Length*elementBytes,data,BufferUsageHint.StaticDraw);
        else GL.BufferData(BufferTarget.TextureBuffer,16,IntPtr.Zero,BufferUsageHint.StaticDraw);
        GL.BindTexture(TextureTarget.TextureBuffer,_textures[slot]);GL.TexBuffer(TextureBufferTarget.TextureBuffer,format,_buffers[slot]);
    }
    public void Bind()
    {
        for(int i=0;i<4;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.BindTexture(TextureTarget.TextureBuffer,_textures[i]);}
    }
    public void Dispose(){foreach(int texture in _textures)GL.DeleteTexture(texture);foreach(int buffer in _buffers)GL.DeleteBuffer(buffer);}
}

// One transform-feedback pass evaluates each bone/node once per displayed pose.
// Mesh shaders consume its palette, rather than evaluating ancestors per vertex.
internal sealed class GLAuthoredPoseRenderer : IDisposable
{
    private readonly int _program,_vertexArray,_bindBuffer,_bindTexture,_timeLocation;
    private readonly int _nodeCount,_boneCount;
    public GLSkinningPalette Palette {get;}
    public GLAuthoredPoseRenderer(Model model)
    {
        _nodeCount=model.Nodes.Count();_boneCount=model.Bones.Count;
        Palette=new GLSkinningPalette(_nodeCount+_boneCount);
        _vertexArray=GL.GenVertexArray();
        int shader=GL.CreateShader(ShaderType.VertexShader);GL.ShaderSource(shader,Source);GL.CompileShader(shader);
        GL.GetShader(shader,ShaderParameter.CompileStatus,out int compiled);
        if(compiled==0)throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
        _program=GL.CreateProgram();GL.AttachShader(_program,shader);
        GL.TransformFeedbackVaryings(_program,4,new[]{"pose0","pose1","pose2","pose3"},TransformFeedbackMode.InterleavedAttribs);
        GL.LinkProgram(_program);GL.DeleteShader(shader);
        GL.GetProgram(_program,GetProgramParameterName.LinkStatus,out int linked);
        if(linked==0)throw new InvalidOperationException(GL.GetProgramInfoLog(_program));
        GL.UseProgram(_program);
        foreach(var item in new[]{("keyValues",0),("keyTimes",1),("nodeInfo",2),("layerInfo",3),("inverseBind",4)})
            GL.Uniform1(GL.GetUniformLocation(_program,item.Item1),item.Item2);
        GL.Uniform1(GL.GetUniformLocation(_program,"nodeCount"),_nodeCount);
        GL.Uniform1(GL.GetUniformLocation(_program,"boneCount"),_boneCount);
        _timeLocation=GL.GetUniformLocation(_program,"animationTime");
        _bindBuffer=GL.GenBuffer();_bindTexture=GL.GenTexture();
        var inverses=model.Bones.Select(bone=>bone.InverseBindMatrix).ToArray();
        GL.BindBuffer(BufferTarget.TextureBuffer,_bindBuffer);
        if(inverses.Length>0)GL.BufferData(BufferTarget.TextureBuffer,inverses.Length*64,inverses,BufferUsageHint.StaticDraw);
        else GL.BufferData(BufferTarget.TextureBuffer,64,IntPtr.Zero,BufferUsageHint.StaticDraw);
        GL.BindTexture(TextureTarget.TextureBuffer,_bindTexture);GL.TexBuffer(TextureBufferTarget.TextureBuffer,SizedInternalFormat.Rgba32f,_bindBuffer);
    }
    public void Draw(GLAuthoredAnimation animation,double time)
    {
        GL.GetInteger(GetPName.VertexArrayBinding,out int previousArray);
        GL.UseProgram(_program);GL.Uniform1(_timeLocation,(float)time);animation.Bind();
        GL.ActiveTexture(TextureUnit.Texture4);GL.BindTexture(TextureTarget.TextureBuffer,_bindTexture);
        GL.BindVertexArray(_vertexArray);
        GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer,0,Palette.Buffer);
        GL.Enable(EnableCap.RasterizerDiscard);
        GL.BeginTransformFeedback(TransformFeedbackPrimitiveType.Points);
        GL.DrawArrays(PrimitiveType.Points,0,_nodeCount+_boneCount);
        GL.EndTransformFeedback();GL.Disable(EnableCap.RasterizerDiscard);
        GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer,0,0);
        GL.BindVertexArray(previousArray);GL.ActiveTexture(TextureUnit.Texture0);
        Palette.SelectFrames(0,0,0);
    }
    public void Dispose(){Palette.Dispose();GL.DeleteBuffer(_bindBuffer);GL.DeleteTexture(_bindTexture);GL.DeleteVertexArray(_vertexArray);GL.DeleteProgram(_program);}
    private const string Source="""
#version 330 core
uniform samplerBuffer keyValues,keyTimes,inverseBind;
uniform isamplerBuffer nodeInfo,layerInfo;
uniform int nodeCount,boneCount;
uniform float animationTime;
out vec4 pose0,pose1,pose2,pose3;
vec4 slerp(vec4 a,vec4 b,float t)
{
    a=normalize(a);b=normalize(b);float d=dot(a,b);
    if(d<0.0){b=-b;d=-d;}
    if(d>0.9995)return normalize(mix(a,b,t));
    float angle=acos(clamp(d,-1.0,1.0));
    return (sin((1.0-t)*angle)*a+sin(t*angle)*b)/sin(angle);
}
mat4 localPose(int node)
{
    vec3 p=texelFetch(keyValues,node*3).xyz;
    vec4 q=texelFetch(keyValues,node*3+1);
    vec3 s=texelFetch(keyValues,node*3+2).xyz;
    ivec4 info=texelFetch(nodeInfo,node);
    for(int j=0;j<info.z;j++)
    {
        ivec4 layer=texelFetch(layerInfo,info.y+j);
        int low=0,high=layer.y;
        while(low<high)
        {
            int middle=low+(high-low)/2;
            if(texelFetch(keyTimes,layer.x+middle).x<=animationTime)low=middle+1;else high=middle;
        }
        int first=max(0,low-1),next=min(low,layer.y-1);
        float a=texelFetch(keyTimes,layer.x+first).x,b=texelFetch(keyTimes,layer.x+next).x;
        float t=b>a?clamp((animationTime-a)/(b-a),0.0,1.0):0.0;
        int index=layer.z+first*3,nextIndex=layer.z+next*3;
        vec4 pa=texelFetch(keyValues,index),pb=texelFetch(keyValues,nextIndex);
        int flags=int(pa.w);
        if((flags&1)!=0)p=mix(pa.xyz,pb.xyz,t)*texelFetch(keyValues,layer.w).xyz;
        if((flags&2)!=0)q=slerp(texelFetch(keyValues,index+1),texelFetch(keyValues,nextIndex+1),t);
        if((flags&4)!=0)s=mix(texelFetch(keyValues,index+2).xyz,texelFetch(keyValues,nextIndex+2).xyz,t)*texelFetch(keyValues,layer.w+1).xyz;
    }
    q=normalize(q);float x=q.x,y=q.y,z=q.z,w=q.w;
    mat4 r=mat4(vec4(1.0-2.0*(y*y+z*z),2.0*(x*y+w*z),2.0*(x*z-w*y),0),
                vec4(2.0*(x*y-w*z),1.0-2.0*(x*x+z*z),2.0*(y*z+w*x),0),
                vec4(2.0*(x*z+w*y),2.0*(y*z-w*x),1.0-2.0*(x*x+y*y),0),vec4(0,0,0,1));
    mat4 scale=mat4(vec4(s.x,0,0,0),vec4(0,s.y,0,0),vec4(0,0,s.z,0),vec4(0,0,0,1));
    mat4 local=scale*r;local[3]=vec4(p,1);return local;
}
void main()
{
    int index=gl_VertexID;
    int node=index<boneCount?texelFetch(nodeInfo,nodeCount+index).x:index-boneCount;
    mat4 world=mat4(1);
    for(int depth=0;node>=0 && depth<nodeCount;depth++)
    {
        world=localPose(node)*world;node=texelFetch(nodeInfo,node).x;
    }
    if(index<boneCount)
    {
        int i=index*4;
        world=world*mat4(texelFetch(inverseBind,i),texelFetch(inverseBind,i+1),texelFetch(inverseBind,i+2),texelFetch(inverseBind,i+3));
    }
    pose0=world[0];pose1=world[1];pose2=world[2];pose3=world[3];gl_Position=vec4(0,0,0,1);
}
""";
}
