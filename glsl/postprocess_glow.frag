#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

#define MAX_BEAMS 16

uniform sampler2D WorldTexture;
uniform vec2 BeamStarts[MAX_BEAMS];
uniform vec2 BeamEnds[MAX_BEAMS];
uniform vec3 GlowColors[MAX_BEAMS];
uniform float GlowIntensities[MAX_BEAMS];
uniform float GlowRadii[MAX_BEAMS];
uniform float GlowRadiiEnd[MAX_BEAMS];
uniform float EndpointBoosts[MAX_BEAMS];
uniform float SelfBrightens[MAX_BEAMS];
uniform float BeamCount;

out vec4 fragColor;

// Returns (distance, t) where t is the clamped projection parameter along [a, b].
vec2 segmentDistT(vec2 p, vec2 a, vec2 b)
{
	vec2 ab = b - a;
	float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 0.0001), 0.0, 1.0);
	return vec2(length(p - (a + t * ab)), t);
}

void main()
{
	vec4 c = texelFetch(WorldTexture, ivec2(gl_FragCoord.xy), 0);
	vec3 rgb = c.rgb;
	int count = int(BeamCount);

	for (int i = 0; i < MAX_BEAMS; ++i)
	{
		if (i >= count)
			break;

		// Radius tapers from GlowRadii (source) to GlowRadiiEnd (target) along the segment,
		// turning a uniform capsule into a cone. EndpointBoosts brightens the wide end into a pool.
		vec2 dt = segmentDistT(gl_FragCoord.xy, BeamStarts[i], BeamEnds[i]);
		float d = dt.x;
		float t = dt.y;
		float r = mix(GlowRadii[i], GlowRadiiEnd[i], t);
		float boost = 1.0 + EndpointBoosts[i] * smoothstep(0.55, 1.0, t);
		float falloff = boost * exp(-d * d / (r * r));

		// Colored additive glow (screen blend, asymptotic to white).
		vec3 contrib = GlowColors[i] * (GlowIntensities[i] * falloff);
		rgb = rgb + contrib * (1.0 - rgb);

		// Self-brighten: a radial gamma lift on the scene's own pixels (no added color). Gamma < 1
		// raises shadows and midtones strongly while leaving highlights at white (no blow-out) and
		// black at black, so the sprite under the muzzle reads as lit rather than washed out.
		float gamma = 1.0 + SelfBrightens[i] * falloff;
		rgb = pow(max(rgb, 0.0), vec3(1.0 / gamma));
	}

	fragColor = vec4(rgb, c.a);
}
