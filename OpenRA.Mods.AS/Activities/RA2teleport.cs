#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Activities
{
	public class RA2teleport : Activity
	{
		readonly Actor chronoProvider;
		readonly int? maximumDistance;
		readonly int? chronoProviderRangeLimit;
		readonly Dictionary<HashSet<string>, BitSet<DamageType>> terrainsAndDeathTypes = new();
		readonly List<CPos> chronoCellsOfProvider;
		readonly ActorMap actorMap;
		readonly string teleportType;
		CPos directDestination;

		public RA2teleport(Actor chronoProvider, string teleportType, CPos directDestination, List<CPos> chronoCellsOfProvider, int? maximumDistance, int? chronoProviderRangeLimit,
			bool interruptable = true, Dictionary<HashSet<string>, BitSet<DamageType>> terrainsAndDeathTypes = default)
		{
			ActivityType = ActivityType.Move;
			actorMap = chronoProvider.World.WorldActor.TraitOrDefault<ActorMap>();
			var max = chronoProvider.World.Map.Grid.MaximumTileSearchRange;
			if (maximumDistance > max)
				throw new InvalidOperationException($"Teleport distance cannot exceed the value of MaximumTileSearchRange ({max}).");

			this.chronoProvider = chronoProvider;
			this.teleportType = teleportType;
			this.directDestination = directDestination;
			this.maximumDistance = maximumDistance;
			this.chronoProviderRangeLimit = chronoProviderRangeLimit;
			this.terrainsAndDeathTypes = terrainsAndDeathTypes;
			this.chronoCellsOfProvider = chronoCellsOfProvider;

			if (!interruptable)
				IsInterruptible = false;
		}

		public override bool Tick(Actor self)
		{
			// 1. Check if we can teleport, and has a cell to teleport.
			(var bestCell, var damage) = ChooseBestDestinationCell(self, directDestination);
			if (bestCell == null)
				return true;

			// 2. Teleport and trigger teleport effect.
			directDestination = bestCell.Value;
			var oldPos = self.CenterPosition;

			self.Trait<IPositionable>().SetPosition(self, directDestination);
			self.Generation++;

			foreach (var ost in self.TraitsImplementing<IOnSuccessfulTeleportRA2>())
				ost.OnSuccessfulTeleport(teleportType, oldPos, self.World.Map.CenterOfCell(directDestination));

			// 3. Kill the unit being put on a deadly cell intendedly.
			if (damage != null)
				self.Kill(chronoProvider.Owner.PlayerActor, damage.Value);

			return true;
		}

		(CPos? Dest, BitSet<DamageType>? Damage) ChooseBestDestinationCell(Actor self, CPos destination)
		{
			if (chronoProvider == null)
				return (null, null);

			var pos = self.Trait<IPositionable>();
			var map = chronoProvider.World.Map;
			var max = maximumDistance ?? map.Grid.MaximumTileSearchRange;

			// If we teleport a hostile unit, we are going to make it killed if possible within teleport cells
			if (self.Owner.RelationshipWith(chronoProvider.Owner).HasRelationship(PlayerRelationship.Enemy))
			{
				if (!pos.CanEnterCell(destination) && !actorMap.AnyActorsAt(destination) && chronoProvider.Owner.Shroud.IsExplored(destination) && TryGetDamage(map.GetTerrainInfo(destination).Type, out var damage))
					return (destination, damage);

				foreach (var tile in chronoCellsOfProvider)
				{
					if (chronoProvider.Owner.Shroud.IsExplored(tile)
						&& !pos.CanEnterCell(tile) && !actorMap.AnyActorsAt(tile)
						&& TryGetDamage(map.GetTerrainInfo(tile).Type, out var damage2))
						return (tile, damage2);
				}
			}

			// When we cannot find a place to kill it or this is an ally, we make it into somewhere can enter.
			if (pos.CanEnterCell(destination) && chronoProvider.Owner.Shroud.IsExplored(destination))
				return (destination, null);

			foreach (var tile in self.World.Map.FindTilesInCircle(destination, max).Where(c => (c - chronoProvider.Location).LengthSquared < chronoProviderRangeLimit * chronoProviderRangeLimit))
			{
				if (chronoProvider.Owner.Shroud.IsExplored(tile)
					&& pos.CanEnterCell(tile))
					return (tile, null);
			}

			return (null, null);
		}

		bool TryGetDamage(string terrainType, out BitSet<DamageType>? damage)
		{
			foreach (var terrains in terrainsAndDeathTypes.Keys)
				if (terrains.Contains(terrainType))
				{
					damage = terrainsAndDeathTypes[terrains];
					return true;
				}

			damage = null;
			return false;
		}
	}
}
