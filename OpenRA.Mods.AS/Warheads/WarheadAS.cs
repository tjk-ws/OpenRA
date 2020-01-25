#region Copyright & License Information
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
using OpenRA.GameRules;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Warheads
{
	[Desc("AS warhead extension class." +
		"These warheads check for the Air TargetType when detonated inair!")]
	public abstract class WarheadAS : Warhead
	{
		[Desc("Explosions above this altitude that don't impact an actor will check target validity against the 'TargetTypeAir' target types.")]
		public readonly WDist AirThreshold = new WDist(128);

		[Desc("Target types to use when the warhead detonated at an altitude greater than 'AirThreshold'.")]
		static readonly BitSet<TargetableType> TargetTypeAir = new BitSet<TargetableType>("Air");

		[Desc("Check for direct hits against nearby actors for use in the target validity checks.")]
		public readonly bool ImpactActors = true;

		public bool GetDirectHit(World world, CPos cell, WPos pos, Actor firedBy, bool checkTargetType = false)
		{
			foreach (var victim in world.FindActorsOnCircle(pos, WDist.Zero))
			{
				if (checkTargetType && !IsValidAgainst(victim, firedBy))
					continue;

				var healthInfo = victim.Info.TraitInfoOrDefault<HealthInfo>();
				if (healthInfo == null)
					continue;

				// If the impact position is within any HitShape, we have a direct hit
				var activeShapes = victim.TraitsImplementing<HitShape>().Where(Exts.IsTraitEnabled);
				if (activeShapes.Any(i => i.DistanceFromEdge(victim, pos).Length <= 0))
					return true;
			}

			return false;
		}

		public bool IsValidImpact(WPos pos, Actor firedBy)
		{
			var world = firedBy.World;

			if (ImpactActors)
			{
				// Check whether the explosion overlaps with an actor's hitshape
				var potentialVictims = world.FindActorsOnCircle(pos, WDist.Zero);
				foreach (var victim in potentialVictims)
				{
					if (!AffectsParent && victim == firedBy)
						continue;

					var activeShapes = victim.TraitsImplementing<HitShape>().Where(Exts.IsTraitEnabled);
					if (!activeShapes.Any(i => i.Info.Type.DistanceFromEdge(pos, victim).Length <= 0))
						continue;

					if (IsValidAgainst(victim, firedBy))
						return true;
				}
			}

			var targetTile = world.Map.CellContaining(pos);
			if (!world.Map.Contains(targetTile))
				return false;

			var dat = world.Map.DistanceAboveTerrain(pos);
			var tileInfo = world.Map.GetTerrainInfo(targetTile);
			return IsValidTarget(dat > AirThreshold ? TargetTypeAir : tileInfo.TargetTypes);
		}
	}
}
