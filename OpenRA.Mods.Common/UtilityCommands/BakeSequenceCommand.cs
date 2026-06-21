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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.UtilityCommands
{
	// Bakes an animated sprite sequence into a uniform-cell PNG spritesheet with the scale applied offline.
	//
	// Why: imported art (e.g. Dune 2000 R16) is enlarged at render time with a non-integer Scale (1.5). For a
	// rotating overlay whose frames each carry a different offset, multiplying that offset by 1.5 lands every
	// frame on a slightly different sub-pixel, so the part visibly shakes as it animates. Baking the frames to
	// a PNG (scale folded into the pixels, each frame composited at its offset into one common-size cell) lets
	// the sequence run at Scale 1 with no per-frame offset variation - the shake disappears and the size is kept.
	//
	// Only truecolor (Bgra32) source frames are supported, which covers 16-bit R16 art (the sprites that need it).
	sealed class BakeSequenceCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--bake-sequence";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 3;
		}

		[Desc("IMAGE SEQUENCE [SCALE] [--out FILE.png]",
			"Bake an animated sequence into a uniform PNG spritesheet with SCALE folded in, so it can run at " +
			"Scale: 1 without the sub-pixel shake that non-integer runtime scaling causes. SCALE defaults to the " +
			"sequence's own Scale. Prints the sequence YAML to use the baked sheet.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			var image = args[1].ToLowerInvariant();
			var sequenceName = args[2];

			float? scaleArg = null;
			var outFile = $"{image}_{sequenceName}.png".Replace('-', '_');
			for (var i = 3; i < args.Length; i++)
			{
				if (args[i] == "--out" && i + 1 < args.Length)
					outFile = args[++i];
				else if (float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
					scaleArg = s;
			}

			var tileset = modData.DefaultTerrainInfo.Keys.First();
			var nodes = MiniYaml.Load(modData.DefaultFileSystem, modData.Manifest.Sequences, null);
			var node = nodes.FirstOrDefault(n =>
				!n.Key.StartsWith(ActorInfo.AbstractActorPrefix) &&
				string.Equals(n.Key, image, StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException($"Image `{image}` has no sequences.");

			var rc = modData.Manifest.RendererConstants;
			using var cache = new SpriteCache(
				modData.DefaultFileSystem, modData.SpriteLoaders,
				rc.SequenceBgraSheetSize, rc.SequenceIndexedSheetSize, modData.SpriteCachePool);

			var seqs = modData.SpriteSequenceLoader.ParseSequences(modData, tileset, cache, node);
			if (!seqs.TryGetValue(sequenceName, out var seq))
				throw new InvalidOperationException(
					$"Image `{image}` has no sequence `{sequenceName}`. Defined: {string.Join(", ", seqs.Keys)}");

			cache.LoadReservations(modData);
			seq.ResolveSprites(cache);

			var count = seq.Length;
			var scale = scaleArg ?? seq.Scale;

			// Default: BOTTOM-CENTRE anchor every frame in a common box. The source per-frame offsets of trimmed
			// rotating art jitter horizontally (that IS the shake), while the bottom edge (the mount) is roughly
			// constant. So we lock the horizontal position to the average and pin the bottom edge - the art spins
			// above a fixed mount, matching how a clean fixed-box rotation (e.g. the Ordos dish) reads.
			// --preserve-offsets instead keeps each frame at its own offset (faithful; for anchored/non-rotating
			// effects like the windtrap zaps, where the per-frame offset is meaningful and must not be flattened).
			var center = !args.Contains("--preserve-offsets");

			var sprites = new Sprite[count];
			float minL = float.MaxValue, minT = float.MaxValue, maxR = float.MinValue, maxB = float.MinValue;
			int maxW = 0, maxH = 0, nonEmpty = 0;
			double sumX = 0, sumY = 0, sumBottom = 0;
			for (var f = 0; f < count; f++)
			{
				var sprite = seq.GetSprite(f);
				sprites[f] = sprite;
				if (sprite == null || sprite.Bounds.Width == 0 || sprite.Bounds.Height == 0)
					continue;

				if (sprite.Channel != TextureChannel.RGBA)
					throw new InvalidOperationException(
						$"Frame {f} is not truecolor (channel {sprite.Channel}); only Bgra32/R16 art is supported.");

				var w = sprite.Bounds.Width;
				var h = sprite.Bounds.Height;
				if (Environment.GetEnvironmentVariable("BAKE_DUMP") != null)
					Console.Error.WriteLine($"frame {f,2}: offset {sprite.Offset.X,6:0.#},{sprite.Offset.Y,6:0.#}  size {w,3}x{h,-3}  topleft {sprite.Offset.X - w / 2f,7:0.#},{sprite.Offset.Y - h / 2f,7:0.#}");
				var l = sprite.Offset.X - w / 2f;
				var t = sprite.Offset.Y - h / 2f;
				minL = Math.Min(minL, l);
				minT = Math.Min(minT, t);
				maxR = Math.Max(maxR, l + w);
				maxB = Math.Max(maxB, t + h);
				maxW = Math.Max(maxW, w);
				maxH = Math.Max(maxH, h);
				sumX += sprite.Offset.X;
				sumY += sprite.Offset.Y;
				sumBottom += sprite.Offset.Y + h / 2f; // bottom edge of the frame (mount line)
				nonEmpty++;
			}

			if (nonEmpty == 0)
				throw new InvalidOperationException("Sequence has no non-empty frames to bake.");

			var cellW = center ? (int)Math.Ceiling(maxW * scale) : (int)Math.Ceiling((maxR - minL) * scale);
			var cellH = center ? (int)Math.Ceiling(maxH * scale) : (int)Math.Ceiling((maxB - minT) * scale);
			var sheetW = cellW * count;
			var sheet = new byte[4 * sheetW * cellH];

			for (var f = 0; f < count; f++)
			{
				var sprite = sprites[f];
				if (sprite == null || sprite.Bounds.Width == 0 || sprite.Bounds.Height == 0)
					continue;

				var data = sprite.Sheet.GetData();
				var srcStride = 4 * sprite.Sheet.Size.Width;
				var sx0 = Math.Min(sprite.Bounds.Left, sprite.Bounds.Right);
				var sy0 = Math.Min(sprite.Bounds.Top, sprite.Bounds.Bottom);
				var w = sprite.Bounds.Width;
				var h = sprite.Bounds.Height;

				// Where this frame's art starts within its cell, in scaled pixels.
				// Anchored mode: centre horizontally, pin the bottom edge (mount) to the cell bottom.
				var dx0 = center ? (cellW - w * scale) / 2f : (sprite.Offset.X - w / 2f - minL) * scale;
				var dy0 = center ? cellH - h * scale : (sprite.Offset.Y - h / 2f - minT) * scale;

				var dxStart = (int)Math.Floor(dx0);
				var dyStart = (int)Math.Floor(dy0);
				var dxEnd = (int)Math.Ceiling(dx0 + w * scale);
				var dyEnd = (int)Math.Ceiling(dy0 + h * scale);

				for (var dy = dyStart; dy < dyEnd; dy++)
				{
					if (dy < 0 || dy >= cellH)
						continue;
					var syf = (int)Math.Floor((dy + 0.5f - dy0) / scale);
					if (syf < 0 || syf >= h)
						continue;

					for (var dx = dxStart; dx < dxEnd; dx++)
					{
						if (dx < 0 || dx >= cellW)
							continue;
						var sxf = (int)Math.Floor((dx + 0.5f - dx0) / scale);
						if (sxf < 0 || sxf >= w)
							continue;

						var si = (sy0 + syf) * srcStride + 4 * (sx0 + sxf);
						if (data[si + 3] == 0)
							continue; // transparent

						var di = 4 * (dy * sheetW + f * cellW + dx);
						sheet[di] = data[si];
						sheet[di + 1] = data[si + 1];
						sheet[di + 2] = data[si + 2];
						sheet[di + 3] = data[si + 3];
					}
				}
			}

			var embedded = new Dictionary<string, string>
			{
				{ "FrameSize", $"{cellW},{cellH}" },
				{ "FrameAmount", count.ToString(CultureInfo.InvariantCulture) }
			};
			new Png(sheet, SpriteFrameType.Bgra32, sheetW, cellH, embeddedData: embedded).Save(outFile);

			// New sequence Offset so the baked cell lands where the original animation sat. Anchored mode places
			// the cell's bottom-centre at the average source bottom-centre; preserve mode uses the union centre.
			var offX = (int)Math.Round(scale * (center ? sumX / nonEmpty : (minL + maxR) / 2));
			var offY = center
				? (int)Math.Round(scale * sumBottom / nonEmpty - cellH / 2f)
				: (int)Math.Round(scale * (minT + maxB) / 2);

			Console.WriteLine($"Baked {count} frames at scale {scale:0.###} ({(center ? "bottom-centre anchored" : "offset-preserving")}) -> {outFile}  (cell {cellW}x{cellH})");
			Console.WriteLine("Sequence YAML:");
			Console.WriteLine($"\t{sequenceName}:");
			Console.WriteLine($"\t\tFilename: {outFile}");
			Console.WriteLine($"\t\tLength: {count}");
			Console.WriteLine($"\t\tScale: 1");
			Console.WriteLine($"\t\tOffset: {offX},{offY}");
		}
	}
}
