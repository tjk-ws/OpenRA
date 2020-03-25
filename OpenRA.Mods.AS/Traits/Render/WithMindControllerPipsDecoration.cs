﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits.Render
{
	public class WithMindControllerPipsDecorationInfo : WithDecorationBaseInfo, Requires<MindControllerInfo>
	{
		[Desc("If non-zero, override the spacing between adjacent pips.")]
		public readonly int2 PipStride = int2.Zero;

		[Desc("Image that defines the pip sequences.")]
		public readonly string Image = "pips";

		[SequenceReference("Image")]
		[Desc("Sequence used for pips marking controlled actors.")]
		public readonly string FullSequence = "pip-green";

		[SequenceReference("Image")]
		[Desc("Sequence used for empty pips.")]
		public readonly string EmptySequence = "pip-empty";

		[PaletteReference]
		public readonly string Palette = "chrome";

		public override object Create(ActorInitializer init) { return new WithMindControllerPipsDecoration(init.Self, this); }
	}

	public class WithMindControllerPipsDecoration : WithDecorationBase<WithMindControllerPipsDecorationInfo>
	{
		readonly MindController mindController;
		readonly Animation pips;
		readonly int pipCount;

		public WithMindControllerPipsDecoration(Actor self, WithMindControllerPipsDecorationInfo info)
			: base(self, info)
		{
			mindController = self.Trait<MindController>();
			pipCount = mindController.Info.Capacity;
			pips = new Animation(self.World, info.Image);
		}

		protected override IEnumerable<IRenderable> RenderDecoration(Actor self, WorldRenderer wr, int2 screenPos)
		{
			pips.PlayRepeating(Info.EmptySequence);

			var palette = wr.Palette(Info.Palette);
			var pipSize = pips.Image.Size.XY.ToInt2();
			var pipStride = Info.PipStride != int2.Zero ? Info.PipStride : new int2(pipSize.X, 0);

			screenPos -= pipSize / 2;
			for (var i = 0; i < pipCount; i++)
			{
				pips.PlayRepeating(i < mindController.Slaves.Count() ? Info.FullSequence : Info.EmptySequence);
				yield return new UISpriteRenderable(pips.Image, self.CenterPosition, screenPos, 0, palette, 1f);

				screenPos += pipStride;
			}
		}
	}
}
