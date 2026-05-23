#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Renders a screen-space additive glow effect for beams and impacts. Add to the world actor.")]
	public class GlowRendererInfo : TraitInfo
	{
		[Desc("Gaussian falloff radius of the glow in screen pixels.")]
		public readonly float GlowRadius = 60f;

		[Desc("Peak additive intensity of the glow (0-1).")]
		public readonly float GlowIntensity = 0.7f;

		public override object Create(ActorInitializer init) { return new GlowRenderer(this); }
	}

	public sealed class GlowRenderer : IRenderPostProcessPass, INotifyActorDisposing
	{
		readonly GlowRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly IVertexBuffer<RenderPostProcessPassVertex> buffer;

		readonly List<(WPos Source, WPos Target, Color Color, float Scale)> pendingGlows = new();
		readonly List<(WPos Source, WPos Target, Color Color, float Scale, int FramesRemaining, int TotalFrames, int FadeInFrames)> fadingGlows = new();

		public GlowRenderer(GlowRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			shader = renderer.CreateShader(new RenderPostProcessPassShaderBindings("glow"));
			buffer = renderer.CreateVertexBuffer(new RenderPostProcessPassVertex[]
			{
				new(-1, -1), new(1, -1), new(1, 1),
				new(1, 1), new(-1, 1), new(-1, -1)
			}, false);
		}

		public void RegisterGlow(WPos source, WPos target, Color color, float scale = 1f, int fadeFrames = 0, int fadeInFrames = 0)
		{
			if (fadeFrames > 0)
				fadingGlows.Add((source, target, color, scale, fadeFrames, fadeFrames, fadeInFrames));
			else
				pendingGlows.Add((source, target, color, scale));
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterActors;
		bool IRenderPostProcessPass.Enabled => pendingGlows.Count > 0 || fadingGlows.Count > 0;

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			var downscale = renderer.WorldDownscaleFactor;
			var topLeft = wr.Viewport.TopLeft;

			float2 ToFb(WPos pos)
			{
				var screenPx = wr.ScreenPxPosition(pos);
				return new float2(
					(screenPx.X - topLeft.X) * downscale,
					(screenPx.Y - topLeft.Y) * downscale);
			}

			void DrawGlow(WPos source, WPos target, Color color, float scale)
			{
				var p1 = ToFb(source);
				var p2 = ToFb(target);
				shader.SetTexture("WorldTexture", Game.Renderer.WorldBufferSnapshot());
				shader.SetVec("BeamStart", p1.X, p1.Y);
				shader.SetVec("BeamEnd", p2.X, p2.Y);
				shader.SetVec("GlowColor", color.R / 255f, color.G / 255f, color.B / 255f);
				shader.SetVec("GlowIntensity", info.GlowIntensity * scale * (color.A / 255f));
				shader.SetVec("GlowRadius", info.GlowRadius * scale);
				shader.PrepareRender();
				renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
			}

			foreach (var glow in pendingGlows)
				DrawGlow(glow.Source, glow.Target, glow.Color, glow.Scale);
			pendingGlows.Clear();

			for (var i = fadingGlows.Count - 1; i >= 0; i--)
			{
				var glow = fadingGlows[i];
				var framesPassed = glow.TotalFrames - glow.FramesRemaining;
				float fadeScale;
				if (glow.FadeInFrames > 0 && framesPassed < glow.FadeInFrames)
					fadeScale = (float)framesPassed / glow.FadeInFrames;
				else
				{
					var fadeOutTotal = glow.TotalFrames - glow.FadeInFrames;
					fadeScale = fadeOutTotal > 0 ? (float)glow.FramesRemaining / fadeOutTotal : 1f;
				}

				DrawGlow(glow.Source, glow.Target, glow.Color, glow.Scale * fadeScale);

				if (glow.FramesRemaining <= 1)
					fadingGlows.RemoveAt(i);
				else
					fadingGlows[i] = (glow.Source, glow.Target, glow.Color, glow.Scale, glow.FramesRemaining - 1, glow.TotalFrames, glow.FadeInFrames);
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer.Dispose();
		}
	}
}
