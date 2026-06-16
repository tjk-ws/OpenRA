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

using OpenRA.Graphics;
using OpenRA.Mods.Common.Projectiles;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Graphics
{
	public class RailgunHelixRenderable : IRenderable
	{
		readonly Railgun railgun;
		readonly RailgunInfo info;
		readonly WDist helixRadius;
		readonly int alpha;
		readonly int ticks;
		readonly WAngle angle;

		public RailgunHelixRenderable(WPos pos, int zOffset, Railgun railgun, RailgunInfo railgunInfo, int ticks)
		{
			Pos = pos;
			ZOffset = zOffset;
			this.railgun = railgun;
			info = railgunInfo;
			this.ticks = ticks;

			helixRadius = info.HelixRadius + new WDist(ticks * info.HelixRadiusDeltaPerTick);
			alpha = (railgun.HelixColor.A + ticks * info.HelixAlphaDeltaPerTick).Clamp(0, 255);
			angle = new WAngle(ticks * info.HelixAngleDeltaPerTick.Angle);
		}

		public WPos Pos { get; }
		public int ZOffset { get; }
		public bool IsDecoration => true;

		public IRenderable WithZOffset(int newOffset) { return new RailgunHelixRenderable(Pos, newOffset, railgun, info, ticks); }
		public IRenderable OffsetBy(in WVec vec) { return new RailgunHelixRenderable(Pos + vec, ZOffset, railgun, info, ticks); }
		public IRenderable AsDecoration() { return this; }

		// Decoupled rendering: the old Render dereferenced the live Railgun projectile (ForwardStep,
		// CycleCount, Left/Up vectors, AngleStep, HelixColor) and MUTATED this.angle, so the unlocked Render - and
		// the UI-only replay frames that re-run it - would read the live projectile and corrupt the shared angle.
		// Capture everything into an immutable finalized renderable here, under the locked PrepareRenderables pass.
		public IFinalizedRenderable PrepareRender(WorldRenderer wr)
		{
			return new FinalizedRailgunHelixRenderable(Pos, info, helixRadius, alpha, angle,
				railgun.ForwardStep, railgun.CycleCount, railgun.LeftVector, railgun.UpVector, railgun.AngleStep, railgun.HelixColor);
		}
	}

	public class FinalizedRailgunHelixRenderable : IFinalizedRenderable
	{
		readonly WPos pos;
		readonly RailgunInfo info;
		readonly WDist helixRadius;
		readonly int alpha;
		readonly WAngle startAngle;
		readonly WVec forwardStep;
		readonly int cycleCount;
		readonly WVec leftVector;
		readonly WVec upVector;
		readonly WAngle angleStep;
		readonly Color helixColor;

		public FinalizedRailgunHelixRenderable(WPos pos, RailgunInfo info, WDist helixRadius, int alpha, WAngle startAngle,
			WVec forwardStep, int cycleCount, WVec leftVector, WVec upVector, WAngle angleStep, Color helixColor)
		{
			this.pos = pos;
			this.info = info;
			this.helixRadius = helixRadius;
			this.alpha = alpha;
			this.startAngle = startAngle;
			this.forwardStep = forwardStep;
			this.cycleCount = cycleCount;
			this.leftVector = leftVector;
			this.upVector = upVector;
			this.angleStep = angleStep;
			this.helixColor = helixColor;
		}

		public void Render(WorldRenderer wr)
		{
			if (forwardStep == WVec.Zero)
				return;

			var screenWidth = wr.ScreenVector(new WVec(info.HelixThickness.Length, 0, 0))[0];

			// Move forward from self to target to draw helix
			var centerPos = pos;
			var angle = startAngle;
			var points = new float3[cycleCount * info.QuantizationCount];
			for (var i = points.Length - 1; i >= 0; i--)
			{
				// Make it narrower near the end.
				var rad = i < info.QuantizationCount ? helixRadius / 4 :
					i < 2 * info.QuantizationCount ? helixRadius / 2 :
					helixRadius;

				// Note: WAngle.Sin(x) = 1024 * Math.Sin(2pi/1024 * x)
				var u = rad.Length * angle.Cos() * leftVector / (1024 * 1024)
					+ rad.Length * angle.Sin() * upVector / (1024 * 1024);
				points[i] = wr.Screen3DPosition(centerPos + u);

				centerPos += forwardStep;
				angle += angleStep;
			}

			Game.Renderer.WorldRgbaColorRenderer.DrawLine(points, screenWidth, Color.FromArgb(alpha, helixColor));
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
	}
}
