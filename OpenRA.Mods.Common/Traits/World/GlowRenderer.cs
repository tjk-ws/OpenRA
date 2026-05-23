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

		public void RegisterGlow(WPos source, WPos target, Color color, float scale = 1f)
		{
			pendingGlows.Add((source, target, color, scale));
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterActors;
		bool IRenderPostProcessPass.Enabled => pendingGlows.Count > 0;

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

			foreach (var glow in pendingGlows)
			{
				var p1 = ToFb(glow.Source);
				var p2 = ToFb(glow.Target);

				shader.SetTexture("WorldTexture", Game.Renderer.WorldBufferSnapshot());
				shader.SetVec("BeamStart", p1.X, p1.Y);
				shader.SetVec("BeamEnd", p2.X, p2.Y);
				shader.SetVec("GlowColor", glow.Color.R / 255f, glow.Color.G / 255f, glow.Color.B / 255f);
				shader.SetVec("GlowIntensity", info.GlowIntensity * glow.Scale * (glow.Color.A / 255f));
				shader.SetVec("GlowRadius", info.GlowRadius * glow.Scale);
				shader.PrepareRender();
				renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
			}

			pendingGlows.Clear();
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer.Dispose();
		}
	}
}
