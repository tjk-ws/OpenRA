#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Graphics
{
	public class ContrailRenderable : IRenderable, IFinalizedRenderable
	{
		const int MaxSmoothLength = 4;

		public int Length => trail.Length;

		readonly World world;
		readonly Color color;
		readonly Color endcolor;
		readonly int zOffset;
		readonly bool fadeWithColor;
		readonly bool fadeWithWidth;
		readonly float fadeWithWidthRate;

		// Store trail positions in a circular buffer
		readonly WPos[] trail;
		readonly WDist width;
		int next;
		int length;
		readonly int skip;

		public ContrailRenderable(World world, Color color, WDist width, int length, int skip, int zOffset)
			: this(world, new WPos[length], width, 0, 0, skip, color, color, zOffset, true, false, 1f) { } // General OpenRA application

		public ContrailRenderable(World world, Color color, Color fadecolor, WDist width, int length, int skip, int zOffset, bool fadeWithColor, bool fadeWithWidth, float fadeWithWidthRate)
			: this(world, new WPos[length], width, 0, 0, skip, color, fadecolor, zOffset, fadeWithColor, fadeWithWidth, fadeWithWidthRate) { } // Third party mods require

		public ContrailRenderable(World world, WPos[] trail, WDist width, int next, int length, int skip, Color color, Color endcolor, int zOffset, bool fadeWithColor, bool fadeWithWidth, float fadeWithWidthRate)
		{
			this.world = world;
			this.trail = trail;
			this.width = width;
			this.next = next;
			this.length = length;
			this.skip = skip;
			this.color = color;
			this.zOffset = zOffset;
			this.fadeWithColor = fadeWithColor;
			this.fadeWithWidth = fadeWithWidth;
			this.fadeWithWidthRate = fadeWithWidthRate;

			if (fadeWithColor)
				this.endcolor = Color.FromArgb(0, endcolor);
			else
				this.endcolor = endcolor;
		}

		public WPos Pos => trail[Index(next - 1)];
		public int ZOffset => zOffset;
		public bool IsDecoration => true;

		public IRenderable WithZOffset(int newOffset) { return new ContrailRenderable(world, (WPos[])trail.Clone(), width, next, length, skip, color, endcolor, newOffset, fadeWithColor, fadeWithWidth, fadeWithWidthRate); }
		public IRenderable OffsetBy(in WVec vec)
		{
			// Lambdas can't use 'in' variables, so capture a copy for later
			var offset = vec;
			return new ContrailRenderable(world, trail.Select(pos => pos + offset).ToArray(), width, next, length, skip, color, endcolor, zOffset, fadeWithColor, fadeWithWidth, fadeWithWidthRate);
		}

		public IRenderable AsDecoration() { return this; }

		public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }
		public void Render(WorldRenderer wr)
		{
			// Note: The length of contrail is now actually the number of the points to draw the contrail
			// and we require at least two points to draw a tail
			var renderLength = length - skip;
			if (renderLength <= 1)
				return;

			var screenWidth = wr.ScreenVector(new WVec(width, WDist.Zero, WDist.Zero))[0];
			var wcr = Game.Renderer.WorldRgbaColorRenderer;

			// Start of the first line segment is the tail of the list - don't smooth it.
			var curPos = trail[Index(next - skip - 1)];
			var curColor = color;

			for (var i = 1; i < renderLength; i++)
			{
				var j = next - skip - 1 - i;
				var nextColor = Exts.ColorLerp(i * 1f / (renderLength - 1), color, endcolor);

				var nextX = 0L;
				var nextY = 0L;
				var nextZ = 0L;
				var k = 0;
				for (; k < renderLength - i && k < MaxSmoothLength; k++)
				{
					var prepos = trail[Index(j - k)];
					nextX += prepos.X;
					nextY += prepos.Y;
					nextZ += prepos.Z;
				}

				var nextPos = new WPos((int)(nextX / k), (int)(nextY / k), (int)(nextZ / k));

				if (!world.FogObscures(curPos) && !world.FogObscures(nextPos))
					if (!fadeWithWidth)
						wcr.DrawLine(wr.Screen3DPosition(curPos), wr.Screen3DPosition(nextPos), screenWidth, curColor, nextColor);
					else
					{
						var wfade = (renderLength * 1f - i * fadeWithWidthRate) / (renderLength - 1);
						if (wfade > 0)
							wcr.DrawLine(wr.Screen3DPosition(curPos), wr.Screen3DPosition(nextPos), screenWidth * wfade, curColor, nextColor);
					}

				curPos = nextPos;
				curColor = nextColor;
			}
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }

		// Array index modulo length
		int Index(int i)
		{
			var j = i % trail.Length;
			return j < 0 ? j + trail.Length : j;
		}

		public void Update(WPos pos)
		{
			trail[next] = pos;
			next = Index(next + 1);

			if (length < trail.Length)
				length++;
		}

		public static Color ChooseColor(Actor self)
		{
			var ownerColor = Color.FromArgb(255, self.Owner.Color);
			return Exts.ColorLerp(0.5f, ownerColor, Color.White);
		}
	}
}
