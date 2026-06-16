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

namespace OpenRA.Mods.Common.Graphics
{
	public class IsometricSelectionBarsAnnotationRenderable : IRenderable
	{
		readonly Actor actor;
		readonly Polygon bounds;

		public IsometricSelectionBarsAnnotationRenderable(Actor actor, Polygon bounds, bool displayHealth, bool displayExtra)
			: this(actor.CenterPosition, actor, bounds)
		{
			DisplayHealth = displayHealth;
			DisplayExtra = displayExtra;
		}

		public IsometricSelectionBarsAnnotationRenderable(WPos pos, Actor actor, Polygon bounds)
		{
			Pos = pos;
			this.actor = actor;
			this.bounds = bounds;
		}

		public WPos Pos { get; }
		public bool DisplayHealth { get; }
		public bool DisplayExtra { get; }

		public int ZOffset => 0;
		public bool IsDecoration => true;

		public IRenderable WithZOffset(int newOffset) { return this; }
		public IRenderable OffsetBy(in WVec vec) { return new IsometricSelectionBarsAnnotationRenderable(Pos + vec, actor, bounds); }
		public IRenderable AsDecoration() { return this; }

		static Color GetHealthColor(IHealth health)
		{
			return health.DamageState == DamageState.Critical ? Color.Red :
				health.DamageState == DamageState.Heavy ? Color.Yellow : Color.LimeGreen;
		}

		// Decoupled rendering: capture all live actor/trait state HERE - PrepareRender runs during the
		// LOCKED PrepareRenderables pass. The returned finalized renderable is replayed by the UNLOCKED
		// DrawAnnotations (including UI-only frames, while the sim thread concurrently ticks and may dispose this
		// actor), so it must never dereference the Actor or its traits at Render time.
		public IFinalizedRenderable PrepareRender(WorldRenderer wr)
		{
			if (!actor.IsInWorld || actor.IsDead)
				return new FinalizedIsometricSelectionBarsAnnotationRenderable(bounds, null);

			List<(float Value, Color Color, int BarNum, float? SecondValue, Color? SecondColor)> bars = null;

			if (DisplayHealth)
			{
				var health = actor.TraitOrDefault<IHealth>();

				// Matches the original Render: when health is requested but absent/dead, draw nothing at all.
				if (health == null || health.IsDead)
					return new FinalizedIsometricSelectionBarsAnnotationRenderable(bounds, null);

				var displayValue = health.DisplayHP != health.HP ? (float?)health.DisplayHP / health.MaxHP : null;
				(bars ??= []).Add(((float)health.HP / health.MaxHP, GetHealthColor(health), 0, displayValue, Color.OrangeRed));
			}

			if (DisplayExtra)
			{
				var i = 1;
				foreach (var extraBar in actor.TraitsImplementing<ISelectionBar>())
				{
					var value = extraBar.GetValue();
					if (value != 0 || extraBar.DisplayWhenEmpty)
						(bars ??= []).Add((value, extraBar.GetColor(), i++, null, null));
				}
			}

			return new FinalizedIsometricSelectionBarsAnnotationRenderable(bounds, bars);
		}
	}

	public class FinalizedIsometricSelectionBarsAnnotationRenderable : IFinalizedRenderable
	{
		const int BarWidth = 3;
		const int BarHeight = 4;
		const int BarStride = 5;

		static readonly Color EmptyColor = Color.FromArgb(160, 30, 30, 30);
		static readonly Color DarkEmptyColor = Color.FromArgb(160, 15, 15, 15);
		static readonly Color DarkenColor = Color.FromArgb(24, 0, 0, 0);
		static readonly Color LightenColor = Color.FromArgb(24, 255, 255, 255);

		readonly Polygon bounds;
		readonly List<(float Value, Color Color, int BarNum, float? SecondValue, Color? SecondColor)> bars;

		public FinalizedIsometricSelectionBarsAnnotationRenderable(Polygon bounds,
			List<(float Value, Color Color, int BarNum, float? SecondValue, Color? SecondColor)> bars)
		{
			this.bounds = bounds;
			this.bars = bars;
		}

		void DrawBar(WorldRenderer wr, float value, Color barColor, int barNum, float? secondValue = null, Color? secondColor = null)
		{
			var darkColor = Color.FromArgb(barColor.A, barColor.R / 2, barColor.G / 2, barColor.B / 2);
			var barAspect = new float2(1f, 0.5f);
			var stepAspect = new float2(1f, -0.5f);

			var offset = barNum * BarStride * barAspect - new float2(0, BarHeight + 1);
			var start = wr.Viewport.WorldToViewPx(bounds.Vertices[1]).ToFloat2() + offset;
			var end = wr.Viewport.WorldToViewPx(bounds.Vertices[0]).ToFloat2() + offset;

			// HACK: Work around rounding errors that may cause a few-px offset in the end relative to the start
			// Force the bar to take a 45 degree angle
			end = new float2(end.X, start.Y - (end.X - start.X) / 2);

			// Round the cut point to the nearest pixel to avoid potential off-by-one pixel offsets distorting the bar
			var cutX = (int)(float2.Lerp(start.X, end.X, value) + 0.5f);
			var cut = new float2(cutX, start.Y - (cutX - start.X) / 2);

			var cr = Game.Renderer.RgbaColorRenderer;
			var da = BarWidth * barAspect;
			var db = new int2(0, BarHeight);
			var dc = da + db;

			// Filled bar
			cr.FillRect(start + da, start + dc, cut + dc, cut + da, darkColor);
			cr.FillRect(start, start + da, start + dc, start + db, darkColor);
			cr.FillRect(start, start + da, cut + da, cut, barColor);

			// Faint marks to break the monotony of the solid bar
			var dx = BarWidth;
			while (dx < cut.X - start.X)
			{
				var step = start + dx * stepAspect;
				cr.DrawLine(step, step + da, 1, DarkenColor);
				cr.DrawLine(step + da, step + dc, 1, LightenColor);
				dx += BarWidth;
			}

			// Second bar (e.g. applied damage)
			if (secondValue.HasValue && secondColor.HasValue)
			{
				var secondCutX = (int)(float2.Lerp(start.X, end.X, secondValue.Value) + 0.5f);
				var secondCut = new float2(secondCutX, start.Y - (secondCutX - start.X) / 2);
				var darkSecond = Color.FromArgb(secondColor.Value.A, secondColor.Value.R / 2, secondColor.Value.G / 2, secondColor.Value.B / 2);

				cr.FillRect(cut + da, cut + dc, secondCut + dc, secondCut + da, darkSecond);
				cr.FillRect(cut, cut + da, secondCut + da, secondCut, secondColor.Value);

				value = secondValue.Value;
				cut = secondCut;
			}

			// Empty bar
			if (value < 1)
			{
				cr.FillRect(cut + da, cut + dc, end + dc, end + da, DarkEmptyColor);
				cr.FillRect(cut, cut + da, end + da, end, EmptyColor);
			}
		}

		public void Render(WorldRenderer wr)
		{
			if (bars == null)
				return;

			foreach (var (value, color, barNum, secondValue, secondColor) in bars)
				DrawBar(wr, value, color, barNum, secondValue, secondColor);
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
	}
}
