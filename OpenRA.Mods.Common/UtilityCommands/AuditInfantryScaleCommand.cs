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

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.UtilityCommands
{
	// Measures the tight opaque bounding box (in source pixels) of a chosen frame of each listed sprite and
	// exports that frame as a trimmed PNG. Used to normalise infantry on-screen size: a unit's visible height
	// is opaqueHeight * sequenceScale, so a reference unit's visible height divided by another's opaqueHeight
	// gives the Scale that makes them read at the same size. Reuses the engine sprite loaders so SHP(TD),
	// SHP(TS) and PNG art are all handled identically.
	sealed class AuditInfantryScaleCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--audit-infantry-scale";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 4;
		}

		// Shadow indices excluded from the body measurement (same convention as --png --noshadow).
		static readonly int[] ShadowIndices = { 1, 3, 4 };

		[Desc("LISTFILE PALETTE OUTDIR",
			"Measure the opaque body bbox of an infantry stand frame and export a trimmed PNG per unit.",
			"LISTFILE: lines `image,shpfile,frameindex,scale`. Prints CSV: image,shp,frameindex,scale,opaqueW,opaqueH.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			var listFile = args[1];
			var palettePath = args[2];
			var outDir = args[3];
			Directory.CreateDirectory(outDir);

			// True colours (index 0 transparent, no shadow remap) - we decide opacity ourselves below.
			var palette = new ImmutablePalette(palettePath, ImmutableArray.Create(0), ImmutableArray<int>.Empty);
			var palColors = new Color[Palette.Size];
			for (var i = 0; i < Palette.Size; i++)
				palColors[i] = palette.GetColor(i);

			var shadow = ShadowIndices.ToHashSet();

			Console.WriteLine("image,shp,frameindex,scale,opaqueW,opaqueH");
			foreach (var raw in File.ReadAllLines(listFile))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line.StartsWith('#'))
					continue;

				var parts = line.Split(',');
				var image = parts[0];
				var shp = parts[1];
				var frameIndex = int.Parse(parts[2], CultureInfo.InvariantCulture);
				var scale = parts.Length > 3 ? parts[3] : "1";

				ISpriteFrame[] frames;
				try
				{
					frames = FrameLoader.GetFrames(modData.DefaultFileSystem, shp, modData.SpriteLoaders, out _);
				}
				catch (Exception e)
				{
					Console.Error.WriteLine($"SKIP {image} ({shp}): {e.Message}");
					continue;
				}

				if (frames.Length == 0)
				{
					Console.Error.WriteLine($"SKIP {image} ({shp}): no frames");
					continue;
				}

				var idx = frameIndex;
				if (idx < 0 || idx >= frames.Length)
					idx = 0;
				var frame = frames[idx];
				var w = frame.Size.Width;
				var h = frame.Size.Height;
				var data = frame.Data;
				var indexed = frame.Type == SpriteFrameType.Indexed8;
				var bpp = indexed ? 1 : 4; // truecolor frames are 32bpp here (Bgra32/Rgba32)

				// Per-pixel opacity + colour.
				bool Opaque(int x, int y)
				{
					var i = (y * w + x) * bpp;
					if (indexed)
					{
						var ix = data[i];
						return ix != 0 && !shadow.Contains(ix);
					}

					return data[i + 3] != 0; // alpha
				}

				int minX = w, minY = h, maxX = -1, maxY = -1;
				for (var y = 0; y < h; y++)
					for (var x = 0; x < w; x++)
						if (Opaque(x, y))
						{
							if (x < minX) minX = x;
							if (y < minY) minY = y;
							if (x > maxX) maxX = x;
							if (y > maxY) maxY = y;
						}

				if (maxX < 0)
				{
					Console.Error.WriteLine($"SKIP {image} ({shp}) frame {idx}: fully transparent");
					continue;
				}

				var bw = maxX - minX + 1;
				var bh = maxY - minY + 1;

				// Export trimmed frame as BGRA PNG.
				var rgbaType = frame.Type;
				var outBgra = new byte[bw * bh * 4];
				for (var y = 0; y < bh; y++)
					for (var x = 0; x < bw; x++)
					{
						var sx = minX + x;
						var sy = minY + y;
						if (!Opaque(sx, sy))
							continue;

						Color c;
						if (indexed)
							c = palColors[data[(sy * w + sx)]];
						else
						{
							var si = (sy * w + sx) * 4;
							if (rgbaType == SpriteFrameType.Rgba32)
								c = Color.FromArgb(data[si + 3], data[si], data[si + 1], data[si + 2]);
							else // Bgra32
								c = Color.FromArgb(data[si + 3], data[si + 2], data[si + 1], data[si]);
						}

						var di = (y * bw + x) * 4;
						outBgra[di] = c.B;
						outBgra[di + 1] = c.G;
						outBgra[di + 2] = c.R;
						outBgra[di + 3] = c.A;
					}

				new Png(outBgra, SpriteFrameType.Bgra32, bw, bh).Save(Path.Combine(outDir, image + ".png"));

				Console.WriteLine($"{image},{shp},{idx},{scale},{bw},{bh}");
			}
		}
	}
}
