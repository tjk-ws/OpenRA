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
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Lint;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.UtilityCommands
{
	// Measures the visible sprite components used by an actor without creating a World or renderer.
	// This deliberately excludes decorations and idle overlays, so shield art cannot inflate the model bounds.
	sealed class MeasureActorSpriteBoundsCommand : IUtilityCommand
	{
		const string Marker = "Measure visible actor sprite bounds for shield fitting";
		static readonly int[] ChannelMasks = [2, 1, 0, 3];

		sealed class Bounds
		{
			public bool HasPixels { get; set; }
			public float Left { get; set; }
			public float Top { get; set; }
			public float Right { get; set; }
			public float Bottom { get; set; }
			public float Width => HasPixels ? Right - Left : 0;
			public float Height => HasPixels ? Bottom - Top : 0;
			public float CenterX => HasPixels ? (Left + Right) / 2 : 0;
			public float CenterY => HasPixels ? (Top + Bottom) / 2 : 0;

			public void Include(float left, float top, float right, float bottom)
			{
				if (!HasPixels)
				{
					HasPixels = true;
					Left = left;
					Top = top;
					Right = right;
					Bottom = bottom;
					return;
				}

				Left = Math.Min(Left, left);
				Top = Math.Min(Top, top);
				Right = Math.Max(Right, right);
				Bottom = Math.Max(Bottom, bottom);
			}
		}

		sealed class ComponentResult
		{
			public string Trait { get; set; }
			public string Sequence { get; set; }
			public int Frames { get; set; }
			public int Facings { get; set; }
			public Bounds Bounds { get; set; }
			public List<Bounds> FacingBounds { get; set; } = [];
		}

		sealed class ActorResult
		{
			public string Actor { get; set; }
			public string Image { get; set; }
			public string Method { get; set; } = "sprite-visible-pixels";
			public string Warning { get; set; }
			public string Error { get; set; }
			public Bounds Bounds { get; set; } = new();
			public List<ComponentResult> Components { get; set; } = [];
		}

		sealed class Output
		{
			public string Description { get; set; } = Marker;
			public List<ActorResult> Actors { get; set; } = [];
		}

		readonly record struct Component(
			string Trait,
			string Sequence,
			int OffsetFacings,
			WDist MaxRecoil,
			Func<WAngle, WAngle, WDist, WVec> Offset);

		string IUtilityCommand.Name => "--measure-actor-sprite-bounds";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 2;
		}

		[Desc("ACTOR [ACTOR ...] [--actors-file FILE.txt] [--out FILE.json]",
			Marker + ". Enumerates every frame and facing of the actor's body, infantry, turret, and barrel sequences. ",
			"Sprite PNG/SHP pixels are measured through the engine SpriteCache; voxel actors are reported as unsupported.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var modData = Game.ModData = utility.ModData;
			var actors = new List<string>();
			var outFile = "actor-sprite-bounds.json";
			for (var i = 1; i < args.Length; i++)
			{
				if (args[i] == "--out" && i + 1 < args.Length)
					outFile = args[++i];
				else if (args[i] == "--actors-file" && i + 1 < args.Length)
					actors.AddRange(File.ReadLines(args[++i])
						.Select(line => line.Trim())
						.Where(line => line.Length > 0 && !line.StartsWith('#')));
				else
					actors.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			}

			if (actors.Count == 0)
				throw new InvalidOperationException("At least one actor name is required.");

			var tileset = modData.DefaultTerrainInfo.Keys.First();
			var terrain = modData.DefaultTerrainInfo[tileset];
			var grid = modData.GetOrCreate<MapGrid>();
			var fx = (float)terrain.TileSize.Width / grid.TileScale;
			var fy = (float)terrain.TileSize.Height / grid.TileScale;
			var sequenceNodes = MiniYaml.Load(modData.DefaultFileSystem, modData.Manifest.Sequences, null);
			var output = new Output();

			var rc = modData.Manifest.RendererConstants;
			using var cache = new SpriteCache(
				modData.DefaultFileSystem, modData.SpriteLoaders,
				rc.SequenceBgraSheetSize, rc.SequenceIndexedSheetSize, modData.SpriteCachePool);

			var parsed = new Dictionary<string, IReadOnlyDictionary<string, ISpriteSequence>>(StringComparer.OrdinalIgnoreCase);
			var actorComponents = new Dictionary<string, List<Component>>(StringComparer.OrdinalIgnoreCase);
			foreach (var requested in actors.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var name = requested.ToLowerInvariant();
				var result = new ActorResult { Actor = name };
				output.Actors.Add(result);

				if (!modData.DefaultRules.Actors.TryGetValue(name, out var actorInfo))
				{
					result.Error = "actor does not exist";
					continue;
				}

				if (actorInfo.TraitInfos<TraitInfo>().Any(t => t.GetType().Name == "RenderVoxelsInfo"))
				{
					result.Method = "sprite-visible-pixels-partial";
					result.Warning = "voxel/model components excluded; combine sprite bounds with conservative model bounds";
				}

				var rsi = actorInfo.TraitInfoOrDefault<RenderSpritesInfo>();
				if (rsi == null)
				{
					result.Method = "unsupported";
					result.Error = "no RenderSprites trait; voxel/model actors require conservative model bounds";
					continue;
				}

				if (rsi.FactionImages != null && rsi.FactionImages.Count > 0)
				{
					result.Method = "unsupported";
					result.Error = "faction-specific sprite images require conservative bounds";
					continue;
				}

				var image = (rsi.Image ?? name).ToLowerInvariant();
				result.Image = image;
				var node = sequenceNodes.FirstOrDefault(n =>
					!n.Key.StartsWith(ActorInfo.AbstractActorPrefix) &&
					string.Equals(n.Key, image, StringComparison.OrdinalIgnoreCase));
				if (node == null)
				{
					result.Error = $"image `{image}` has no sequences";
					continue;
				}

				if (!parsed.ContainsKey(image))
					parsed.Add(image, modData.SpriteSequenceLoader.ParseSequences(modData, tileset, cache, node));

				// Preserve duplicate sequence names when separate turret/barrel instances use
				// different offsets. Re-measuring a duplicate body sequence is harmless, while
				// collapsing an offset component can understate the protected silhouette.
				var components = Components(actorInfo, parsed[image]).ToList();
				if (components.Count == 0)
				{
					result.Error = "no supported body, infantry, turret, or barrel sequence";
					continue;
				}

				actorComponents.Add(name, components);
			}

			cache.LoadReservations(modData);
			foreach (var result in output.Actors.Where(r => r.Error == null))
			{
				var sequences = parsed[result.Image];
				foreach (var component in actorComponents[result.Actor])
				{
					if (!sequences.TryGetValue(component.Sequence, out var sequence))
						continue;

					sequence.ResolveSprites(cache);
					var componentBounds = new Bounds();
					var recoilStates = component.MaxRecoil == WDist.Zero ?
						new[] { WDist.Zero } : [WDist.Zero, component.MaxRecoil];
					var facingBounds = new List<Bounds>();
					for (var facing = 0; facing < Math.Max(1, sequence.Facings); facing++)
					{
						var currentFacingBounds = new Bounds();
						var angle = new WAngle(facing * 1024 / Math.Max(1, sequence.Facings));
						for (var frame = 0; frame < sequence.Length; frame++)
						{
							var (sprite, rotation) = sequence.GetSpriteWithRotation(frame, angle);
							if (!TryOpaqueBounds(sprite, out var minX, out var minY, out var maxX, out var maxY))
								continue;

							var scale = sequence.Scale;
							var sw = Math.Abs(sprite.Bounds.Width);
							var sh = Math.Abs(sprite.Bounds.Height);
							var offsetFacings = Math.Max(1, component.OffsetFacings);
							for (var offsetFacing = 0; offsetFacing < offsetFacings; offsetFacing++)
							{
								var offsetAngle = new WAngle(offsetFacing * 1024 / offsetFacings);
								foreach (var recoil in recoilStates)
								{
									var worldOffset = component.Offset(angle, offsetAngle, recoil);
									var offsetX = fx * worldOffset.X;
									var offsetY = fy * (worldOffset.Y - worldOffset.Z);
									var left = scale * (sprite.Offset.X - sw / 2f + minX) + offsetX;
									var top = scale * (sprite.Offset.Y - sh / 2f + minY) + offsetY;
									var right = scale * (sprite.Offset.X - sw / 2f + maxX + 1) + offsetX;
									var bottom = scale * (sprite.Offset.Y - sh / 2f + maxY + 1) + offsetY;

									if (rotation != WAngle.Zero)
										RotateBounds(ref left, ref top, ref right, ref bottom,
											offsetX + scale * sprite.Offset.X, offsetY + scale * sprite.Offset.Y,
											rotation.RendererRadians());

									componentBounds.Include(left, top, right, bottom);
									currentFacingBounds.Include(left, top, right, bottom);
									result.Bounds.Include(left, top, right, bottom);
								}
							}
						}
						facingBounds.Add(currentFacingBounds);
					}

					result.Components.Add(new ComponentResult
					{
						Trait = component.Trait,
						Sequence = component.Sequence,
						Frames = sequence.Length,
						Facings = sequence.Facings,
						Bounds = componentBounds,
						FacingBounds = facingBounds
					});
				}

				if (!result.Bounds.HasPixels)
					result.Error = "supported sequences contained no visible pixels";
			}

			var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(outFile, json);
			Console.WriteLine($"Measured {output.Actors.Count(a => a.Error == null)} actor(s); " +
				$"{output.Actors.Count(a => a.Error != null)} error(s). Saved: {outFile}");
		}

		static IEnumerable<Component> Components(
			ActorInfo actorInfo,
			IReadOnlyDictionary<string, ISpriteSequence> sequences)
		{
			foreach (var info in actorInfo.TraitInfos<WithSpriteBodyInfo>())
				yield return new Component(info.GetType().Name, info.Sequence, 1, WDist.Zero, (_, _, _) => WVec.Zero);

			foreach (var info in actorInfo.TraitInfos<WithInfantryBodyInfo>())
			{
				foreach (var sequence in info.StandSequences.Concat([info.MoveSequence])
					.Concat(info.IdleSequences)
					.Concat(info.DefaultAttackSequence != null ? [info.DefaultAttackSequence] : []))
					yield return new Component(info.GetType().Name, sequence, 1, WDist.Zero, (_, _, _) => WVec.Zero);
			}

			var body = actorInfo.TraitInfoOrDefault<BodyOrientationInfo>();
			var bodyFacings = BodyFacings(actorInfo, sequences);
			foreach (var info in actorInfo.TraitInfos<WithSpriteTurretInfo>())
			{
				var turret = actorInfo.TraitInfos<TurretedInfo>().FirstOrDefault(t => t.Turret == info.Turret);
				if (turret == null || body == null)
					continue;

				var recoil = WDist.Zero;
				if (info.Recoils)
					recoil = new WDist(actorInfo.TraitInfos<ArmamentInfo>()
						.Where(a => a.Turret == info.Turret).Sum(a => a.Recoil.Length));

				yield return new Component(info.GetType().Name, info.Sequence, bodyFacings, recoil,
					(turretFacing, bodyFacing, recoilDistance) =>
						body.LocalToWorld(turret.Offset.Rotate(WRot.FromYaw(bodyFacing))) +
						body.LocalToWorld(new WVec(-recoilDistance, WDist.Zero, WDist.Zero)
							.Rotate(WRot.FromYaw(turretFacing))));
			}

			foreach (var info in actorInfo.TraitInfos<WithSpriteBarrelInfo>())
			{
				var armament = actorInfo.TraitInfos<ArmamentInfo>().FirstOrDefault(a => a.Name == info.Armament);
				var turret = armament == null ? null :
					actorInfo.TraitInfos<TurretedInfo>().FirstOrDefault(t => t.Turret == armament.Turret);
				if (turret == null || body == null)
					continue;

				yield return new Component(info.GetType().Name, info.Sequence, 1, armament.Recoil,
					(turretFacing, _, recoilDistance) => body.LocalToWorld(turret.Offset +
						(info.LocalOffset + new WVec(-recoilDistance, WDist.Zero, WDist.Zero))
							.Rotate(WRot.FromYaw(turretFacing))));
			}

			// Traits such as popup attacks switch the primary body to additional sequences without
			// implementing a render-preview interface. Reuse the same metadata as CheckSequences,
			// but exclude auxiliary art that should not enlarge the protected model silhouette.
			foreach (var traitInfo in actorInfo.TraitInfos<TraitInfo>())
			{
				var typeName = traitInfo.GetType().Name;
				if (ExcludedSequenceTrait(typeName))
					continue;

				foreach (var field in Utility.GetFields(traitInfo.GetType()))
				{
					var reference = Utility.GetCustomAttributes<SequenceReferenceAttribute>(field, true).FirstOrDefault();
					if (reference == null || reference.Prefix || !string.IsNullOrEmpty(reference.ImageReference))
						continue;

					foreach (var sequence in LintExts.GetFieldValues(traitInfo, field, reference.DictionaryReference))
						if (!string.IsNullOrEmpty(sequence))
							yield return new Component(typeName, sequence, 1, WDist.Zero, (_, _, _) => WVec.Zero);
				}
			}
		}

		static int BodyFacings(ActorInfo actorInfo, IReadOnlyDictionary<string, ISpriteSequence> sequences)
		{
			var body = actorInfo.TraitInfoOrDefault<BodyOrientationInfo>();
			if (body == null)
				return 1;

			// Continuous body orientation uses the complete WAngle domain. Otherwise match
			// the same explicit or sequence-derived quantization used by the renderer.
			if (body.QuantizedFacings >= 0)
				return body.QuantizedFacings == 0 ? 1024 : body.QuantizedFacings;

			var quantizer = actorInfo.TraitInfoOrDefault<QuantizeFacingsFromSequenceInfo>();
			if (quantizer != null && sequences.TryGetValue(quantizer.Sequence, out var sequence))
				return Math.Max(1, sequence.Facings);

			var spriteBody = actorInfo.TraitInfos<WithSpriteBodyInfo>().FirstOrDefault();
			if (spriteBody != null && sequences.TryGetValue(spriteBody.Sequence, out sequence))
				return Math.Max(1, sequence.Facings);

			return 1;
		}

		static bool ExcludedSequenceTrait(string typeName)
		{
			if (typeName is "BuildableInfo" or "ArmamentInfo" or "RenderSpritesInfo" or
				"WithSpriteTurretInfo" or "WithSpriteBarrelInfo")
				return true;

			var excluded = new[]
			{
				"IdleOverlay", "Death", "Make", "Shadow", "Muzzle", "Decoration", "Parachute",
				"Crate", "Production", "Smoke", "Fire", "Bib", "Selection"
			};
			return excluded.Any(typeName.Contains);
		}

		static bool TryOpaqueBounds(Sprite sprite, out int minX, out int minY, out int maxX, out int maxY)
		{
			minX = minY = int.MaxValue;
			maxX = maxY = int.MinValue;
			if (sprite == null)
				return false;

			var sw = Math.Abs(sprite.Bounds.Width);
			var sh = Math.Abs(sprite.Bounds.Height);
			var srcLeft = Math.Min(sprite.Bounds.Left, sprite.Bounds.Right);
			var srcTop = Math.Min(sprite.Bounds.Top, sprite.Bounds.Bottom);
			var data = sprite.Sheet.GetData();
			var stride = 4 * sprite.Sheet.Size.Width;

			for (var y = 0; y < sh; y++)
			{
				for (var x = 0; x < sw; x++)
				{
					var o = (srcTop + y) * stride + 4 * (srcLeft + x);
					var opaque = sprite.Channel == TextureChannel.RGBA ? data[o + 3] != 0 :
						data[o + ChannelMasks[(int)sprite.Channel]] != 0;
					if (!opaque)
						continue;

					minX = Math.Min(minX, x);
					minY = Math.Min(minY, y);
					maxX = Math.Max(maxX, x);
					maxY = Math.Max(maxY, y);
				}
			}

			return maxX >= minX && maxY >= minY;
		}

		static void RotateBounds(ref float left, ref float top, ref float right, ref float bottom,
			float centerX, float centerY, float radians)
		{
			var cos = MathF.Cos(radians);
			var sin = MathF.Sin(radians);
			var corners = new[] { (left, top), (right, top), (right, bottom), (left, bottom) };
			left = top = float.MaxValue;
			right = bottom = float.MinValue;
			foreach (var (x, y) in corners)
			{
				var dx = x - centerX;
				var dy = y - centerY;
				var rx = centerX + cos * dx - sin * dy;
				var ry = centerY + sin * dx + cos * dy;
				left = Math.Min(left, rx);
				top = Math.Min(top, ry);
				right = Math.Max(right, rx);
				bottom = Math.Max(bottom, ry);
			}
		}
	}
}
