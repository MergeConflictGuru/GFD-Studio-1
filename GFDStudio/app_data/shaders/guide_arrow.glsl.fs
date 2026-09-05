#version 330 core

// Keep the guide arrow delta path covered by the build/fetch smoke test.

in vec3 fViewPosition;
in vec3 fNormal;

uniform vec4 uBaseColor;
uniform vec3 uGlowColor;
uniform float uGlowStrength;
uniform float uOpacity;

out vec4 oColor;

void main()
{
    vec3 normal = normalize(fNormal);
    vec3 lightDirection = normalize(vec3(-0.45, 0.9, 0.55));
    vec3 viewDirection = normalize(-fViewPosition);

    float diffuse = max(dot(normal, lightDirection), 0.0);
    float softShade = 0.28 + diffuse * 0.72;
    vec3 halfDirection = normalize(lightDirection + viewDirection);
    float specular = pow(max(dot(normal, halfDirection), 0.0), 48.0);

    vec3 litColor = uBaseColor.rgb * softShade;
    litColor += vec3(0.7, 0.75, 0.8) * specular * 0.32;
    litColor += uGlowColor * uGlowStrength;

    oColor = vec4(litColor, uBaseColor.a * uOpacity);
}
