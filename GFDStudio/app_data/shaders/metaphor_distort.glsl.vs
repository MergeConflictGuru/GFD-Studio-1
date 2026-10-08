#version 330

// in
layout ( location = 0 ) in vec3 vPosition;

layout(location = 1) in vec3 vNormal;

// out
out vec3 fPosition;

// uniforms
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;


layout(location = 3) in vec2 vSkinRange;
uniform bool uSkinning;
uniform samplerBuffer uSkinPalette;
uniform samplerBuffer uSkinInfluences;
uniform mat4 uSkinModelInverse;
uniform int uSkinRigidNode;
uniform int uSkinFrame;
uniform int uSkinNextFrame;
uniform float uSkinFrameBlend;
void skinVertex(out vec3 position, out vec3 normal)
{
    position=vPosition; normal=vNormal;
    if(!uSkinning)
    {
        if(uSkinRigidNode>=0)
        {
            int index=uSkinRigidNode*4+(uSkinFrameBlend<0.5?uSkinFrame:uSkinNextFrame);
            int nextIndex=uSkinRigidNode*4+uSkinNextFrame;
            mat4 a=mat4(texelFetch(uSkinPalette,index),texelFetch(uSkinPalette,index+1),texelFetch(uSkinPalette,index+2),texelFetch(uSkinPalette,index+3));

            mat4 transform=a;
            position=(transform*vec4(position,1.0)).xyz;normal=mat3(transform)*normal;
        }
        return;
    }
    vec3 p=vec3(0.0),n=vec3(0.0);
    for(int j=0;j<int(vSkinRange.y);j++)
    {
        vec2 influence=texelFetch(uSkinInfluences,int(vSkinRange.x)+j).xy;
        int index=int(influence.x)*4;
        int nextIndex=index+uSkinNextFrame;
        index+=uSkinFrameBlend<0.5?uSkinFrame:uSkinNextFrame;
        mat4 bone=mat4(texelFetch(uSkinPalette,index),texelFetch(uSkinPalette,index+1),texelFetch(uSkinPalette,index+2),texelFetch(uSkinPalette,index+3));


        p+=(bone*vec4(vPosition,1.0)).xyz*influence.y;
        n+=mat3(bone)*vNormal*influence.y;
    }
    position=(uSkinModelInverse*vec4(p,1.0)).xyz;
    normal=mat3(uSkinModelInverse)*n;
    if(dot(normal,normal)>0.0)normal=normalize(normal);
}
void main()
{
    vec3 skinPosition,skinNormal; skinVertex(skinPosition,skinNormal);
	fPosition = ( uView * vec4( skinPosition, 1.0 ) ).xyz;
	gl_Position = uProjection * uView * vec4( skinPosition, 1.0 );
}


