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
	public class SelectionBarsAnnotationRenderable : IRenderable
	{
		readonly Actor actor;
		readonly Rectangle decorationBounds;

		public SelectionBarsAnnotationRenderable(Actor actor, Rectangle decorationBounds, bool displayHealth, bool displayExtra)
			: this(actor.CenterPosition, actor, decorationBounds)
		{
			DisplayHealth = displayHealth;
			DisplayExtra = displayExtra;
		}

		public SelectionBarsAnnotationRenderable(WPos pos, Actor actor, Rectangle decorationBounds)
		{
			Pos = pos;
			this.actor = actor;
			this.decorationBounds = decorationBounds;
		}

		public WPos Pos { get; }
		public bool DisplayHealth { get; }
		public bool DisplayExtra { get; }

		public int ZOffset => 0;
		public bool IsDecoration => true;

		public IRenderable WithZOffset(int newOffset) { return this; }
		public IRenderable OffsetBy(in WVec vec) { return new SelectionBarsAnnotationRenderable(Pos + vec, actor, decorationBounds); }
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
				return new FinalizedSelectionBarsAnnotationRenderable(decorationBounds, false, 0, default, false, 0, null);

			var drawHealth = false;
			var healthValue = 0f;
			var healthColor = default(Color);
			var drawDelta = false;
			var deltaValue = 0f;

			if (DisplayHealth)
			{
				var health = actor.TraitOrDefault<IHealth>();
				if (health != null && !health.IsDead)
				{
					drawHealth = true;
					healthValue = (float)health.HP / health.MaxHP;
					healthColor = GetHealthColor(health);
					drawDelta = health.DisplayHP != health.HP;
					deltaValue = (float)health.DisplayHP / health.MaxHP;
				}
			}

			List<(float Value, Color Color)> extraBars = null;
			if (DisplayExtra)
			{
				foreach (var extraBar in actor.TraitsImplementing<ISelectionBar>())
				{
					var value = extraBar.GetValue();
					if (value != 0 || extraBar.DisplayWhenEmpty)
						(extraBars ??= []).Add((value, extraBar.GetColor()));
				}
			}

			return new FinalizedSelectionBarsAnnotationRenderable(decorationBounds, drawHealth, healthValue, healthColor, drawDelta, deltaValue, extraBars);
		}
	}

	public class FinalizedSelectionBarsAnnotationRenderable : IFinalizedRenderable
	{
		readonly Rectangle decorationBounds;
		readonly bool drawHealth;
		readonly float healthValue;
		readonly Color healthColor;
		readonly bool drawDelta;
		readonly float deltaValue;
		readonly List<(float Value, Color Color)> extraBars;

		public FinalizedSelectionBarsAnnotationRenderable(Rectangle decorationBounds, bool drawHealth, float healthValue,
			Color healthColor, bool drawDelta, float deltaValue, List<(float Value, Color Color)> extraBars)
		{
			this.decorationBounds = decorationBounds;
			this.drawHealth = drawHealth;
			this.healthValue = healthValue;
			this.healthColor = healthColor;
			this.drawDelta = drawDelta;
			this.deltaValue = deltaValue;
			this.extraBars = extraBars;
		}

		static void DrawSelectionBar(float2 start, float2 end, float value, Color barColor)
		{
			var c = Color.FromArgb(128, 30, 30, 30);
			var c2 = Color.FromArgb(128, 10, 10, 10);
			var p = new float2(0, -4);
			var q = new float2(0, -3);
			var r = new float2(0, -2);

			var barColor2 = Color.FromArgb(255, barColor.R / 2, barColor.G / 2, barColor.B / 2);

			var z = float3.Lerp(start, end, value);
			var cr = Game.Renderer.RgbaColorRenderer;
			cr.DrawLine(start + p, end + p, 1, c);
			cr.DrawLine(start + q, end + q, 1, c2);
			cr.DrawLine(start + r, end + r, 1, c);

			cr.DrawLine(start + p, z + p, 1, barColor2);
			cr.DrawLine(start + q, z + q, 1, barColor);
			cr.DrawLine(start + r, z + r, 1, barColor2);
		}

		void DrawHealthBar(float2 start, float2 end)
		{
			var c = Color.FromArgb(128, 30, 30, 30);
			var c2 = Color.FromArgb(128, 10, 10, 10);
			var p = new float2(0, -4);
			var q = new float2(0, -3);
			var r = new float2(0, -2);

			var healthColor2 = Color.FromArgb(
				255,
				healthColor.R / 2,
				healthColor.G / 2,
				healthColor.B / 2);

			var z = float3.Lerp(start, end, healthValue);

			var cr = Game.Renderer.RgbaColorRenderer;
			cr.DrawLine(start + p, end + p, 1, c);
			cr.DrawLine(start + q, end + q, 1, c2);
			cr.DrawLine(start + r, end + r, 1, c);

			cr.DrawLine(start + p, z + p, 1, healthColor2);
			cr.DrawLine(start + q, z + q, 1, healthColor);
			cr.DrawLine(start + r, z + r, 1, healthColor2);

			if (drawDelta)
			{
				var deltaColor = Color.OrangeRed;
				var deltaColor2 = Color.FromArgb(
					255,
					deltaColor.R / 2,
					deltaColor.G / 2,
					deltaColor.B / 2);
				var zz = float3.Lerp(start, end, deltaValue);

				cr.DrawLine(z + p, zz + p, 1, deltaColor2);
				cr.DrawLine(z + q, zz + q, 1, deltaColor);
				cr.DrawLine(z + r, zz + r, 1, deltaColor2);
			}
		}

		public void Render(WorldRenderer wr)
		{
			if (!drawHealth && extraBars == null)
				return;

			var start = wr.Viewport.WorldToViewPx(new float2(decorationBounds.Left + 1, decorationBounds.Top)).ToFloat2();
			var end = wr.Viewport.WorldToViewPx(new float2(decorationBounds.Right - 1, decorationBounds.Top)).ToFloat2();

			if (drawHealth)
				DrawHealthBar(start, end);

			if (extraBars != null)
			{
				foreach (var (value, color) in extraBars)
				{
					var offset = new float2(0, 4);
					start += offset;
					end += offset;
					DrawSelectionBar(start, end, value, color);
				}
			}
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
	}
}
