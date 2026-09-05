#version 330 core

layout(location = 0) in vec3 vPosition;
layout(location = 1) in vec3 vNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 fViewPosition;
out vec3 fNormal;

void main()
{
    vec4 viewPosition = uView * uModel * vec4(vPosition, 1.0);
    fViewPosition = viewPosition.xyz;
    fNormal = normalize(mat3(transpose(inverse(uView * uModel))) * vNormal);
    gl_Position = uProjection * viewPosition;
}
