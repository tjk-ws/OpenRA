#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D WorldTexture;
uniform vec2 BeamStart;
uniform vec2 BeamEnd;
uniform vec3 GlowColor;
uniform float GlowIntensity;
uniform float GlowRadius;

out vec4 fragColor;

float segmentDist(vec2 p, vec2 a, vec2 b)
{
	vec2 ab = b - a;
	float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 0.0001), 0.0, 1.0);
	return length(p - (a + t * ab));
}

void main()
{
	vec4 c = texelFetch(WorldTexture, ivec2(gl_FragCoord.xy), 0);
	float d = segmentDist(gl_FragCoord.xy, BeamStart, BeamEnd);
	float glow = GlowIntensity * exp(-d * d / (GlowRadius * GlowRadius));
	vec3 glow_contrib = GlowColor * glow;
	fragColor = vec4(c.rgb + glow_contrib * (1.0 - c.rgb), c.a);
}
