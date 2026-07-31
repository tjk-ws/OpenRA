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
uniform float MinimumExposure;

out vec4 fragColor;

void main()
{
	vec4 source = texelFetch(SourceTexture, ivec2(gl_FragCoord.xy), 0);
	float distanceFromLight = length(gl_FragCoord.xy - LightPosition);
	float lightFalloff = exp(-pow(distanceFromLight / max(LightRadius, 0.0001), 2.0));

	// Simulate camera underexposure across the whole world while preserving enough of the source
	// frame to keep units and structures readable. The directional contribution then indicates the
	// blast position without saturating into an opaque yellow disk.
	float retainedExposure = max(1.0 - Darkness, MinimumExposure);
	float flashStrength = 1.0 - exp(-Brightness * lightFalloff);
	vec3 exposed = source.rgb * retainedExposure;
	vec3 contribution = LightColor * flashStrength;
	exposed = exposed + contribution * (1.0 - exposed);

	fragColor = vec4(exposed, source.a);
}
