#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D SourceTexture;
uniform vec2 LightPosition;
uniform vec3 LightColor;
uniform float LightRadius;
uniform float Brightness;
uniform float Darkness;

out vec4 fragColor;

void main()
{
	vec4 source = texelFetch(SourceTexture, ivec2(gl_FragCoord.xy), 0);
	float distanceFromLight = length(gl_FragCoord.xy - LightPosition);
	float lightFalloff = exp(-pow(distanceFromLight / max(LightRadius, 0.0001), 2.0));

	// Simulate camera underexposure away from the fireball, then lift the exposed side toward a
	// warm white. Screen blending preserves highlight detail instead of adding unclamped color.
	vec3 exposed = source.rgb * (1.0 - Darkness);
	vec3 contribution = LightColor * (Brightness * lightFalloff);
	exposed = exposed + contribution * (1.0 - exposed);

	fragColor = vec4(exposed, source.a);
}
