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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Adds the hard-coded shroud palette to the game")]
	sealed class ShroudPaletteInfo : TraitInfo
	{
		[PaletteDefinition]
		[FieldLoader.Require]
		[Desc("Internal palette name")]
		public readonly string Name = "shroud";

		[Desc("Palette type")]
		public readonly bool Fog = false;

		public override object Create(ActorInitializer init) { return new ShroudPalette(this); }
	}

	sealed class ShroudPalette : ILoadsPalettes, IProvidesAssetBrowserPalettes
	{
		readonly ShroudPaletteInfo info;

		public ShroudPalette(ShroudPaletteInfo info) { this.info = info; }

		// Indices at or above this carry a smooth black-alpha ramp instead of the
		// 8-colour lookup, giving the shroud/fog edge a finely graduated fade.
		const int RampStart = 224;

		public void LoadPalettes(WorldRenderer wr)
		{
			var c = info.Fog ? Fog : Shroud;
			var maxAlpha = info.Fog ? 116 : 255;
			var colors = new uint[Palette.Size];
			for (var i = 0; i < Palette.Size; i++)
			{
				if (i >= RampStart)
				{
					// Eased (smoothstep) alpha ramp over indices 225..255: 31 graduated
					// steps so the shroud boundary fades without visible banding.
					var t = (i - RampStart) / (float)(Palette.Size - 1 - RampStart);
					var a = (int)(maxAlpha * t * t * (3 - 2 * t) + 0.5f);
					colors[i] = (uint)(a << 24);
				}
				else
					colors[i] = c[i % 8].ToArgb();
			}

			wr.AddPalette(info.Name, new ImmutablePalette(colors));
		}

		static readonly Color[] Fog =
		[
			Color.FromArgb(0, 0, 0, 0),
			Color.Green, Color.Blue, Color.Yellow,
			Color.FromArgb(116, 0, 0, 0),
			Color.FromArgb(87, 0, 0, 0),
			Color.FromArgb(58, 0, 0, 0),
			Color.FromArgb(29, 0, 0, 0)
		];

		static readonly Color[] Shroud =
		[
			Color.FromArgb(0, 0, 0, 0),
			Color.Green, Color.Blue, Color.Yellow,
			Color.Black,
			Color.FromArgb(190, 0, 0, 0),
			Color.FromArgb(128, 0, 0, 0),
			Color.FromArgb(64, 0, 0, 0)
		];

		public IEnumerable<string> PaletteNames { get { yield return info.Name; } }
	}
}
