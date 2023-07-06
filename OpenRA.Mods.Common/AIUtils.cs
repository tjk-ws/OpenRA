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
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common
{
	public enum BuildingType { Building, Defense, Refinery }

	public enum WaterCheck { NotChecked, EnoughWater, NotEnoughWater, DontCheck }

	public static class AIUtils
	{
		public static bool CanPlaceBuildingWithSpaceAround(World world, CPos cell, ActorInfo ai, BuildingInfo bi, Actor toIgnore, int cellDist)
		{
			if (cellDist > 0)
			{
				var buildingInfluence = world.WorldActor.Trait<BuildingInfluence>();
				var left = -cellDist;
				var right = bi.Dimensions.X + cellDist - 1;
				var top = -cellDist;
				var bottom = bi.Dimensions.Y + cellDist - 1;
				(int RowStart, int RowEnd)[] rowProcessIndexPairs = { (left, left), (right, right), (left, right), (left, right) };
				(int ColStart, int ColEnd)[] colProcessIndexPairs = { (top, bottom), (top, bottom), (top, top), (bottom, bottom) };

				for (var i = 0; i < rowProcessIndexPairs.Length; i++)
					for (var rowIndex = rowProcessIndexPairs[i].RowStart; rowIndex <= rowProcessIndexPairs[i].RowEnd; rowIndex++)
						for (var colIndex = colProcessIndexPairs[i].ColStart; colIndex <= colProcessIndexPairs[i].ColEnd; colIndex++)
						{
							var cellchecking = cell + new CVec(rowIndex, colIndex);
							if (!world.Map.Contains(cellchecking))
								continue;

							if (buildingInfluence.AnyBuildingAt(cellchecking))
								return false;
						}
			}

			return world.CanPlaceBuilding(cell, ai, bi, toIgnore);
		}

		public static bool IsAreaAvailable<T>(World world, Player player, Map map, int radius, HashSet<string> terrainTypes)
		{
			var cells = world.ActorsHavingTrait<T>().Where(a => a.Owner == player);

			// TODO: Properly check building foundation rather than 3x3 area.
			return cells.Select(a => map.FindTilesInCircle(a.Location, radius)
				.Count(c => map.Contains(c) && terrainTypes.Contains(map.GetTerrainInfo(c).Type) &&
					Util.AdjacentCells(world, Target.FromCell(world, c))
						.All(ac => map.Contains(ac) && terrainTypes.Contains(map.GetTerrainInfo(ac).Type))))
							.Any(availableCells => availableCells > 0);
		}

		public static IEnumerable<ProductionQueue> FindQueues(Player player, string category)
		{
			return player.World.ActorsWithTrait<ProductionQueue>()
				.Where(a => a.Actor.Owner == player && a.Trait.Info.Type == category && a.Trait.Enabled)
				.Select(a => a.Trait);
		}

		public static IEnumerable<Actor> GetActorsWithTrait<T>(World world)
		{
			return world.ActorsHavingTrait<T>();
		}

		public static int CountActorsWithTrait<T>(string actorName, Player owner)
		{
			return GetActorsWithTrait<T>(owner.World).Count(a => a.Owner == owner && a.Info.Name == actorName);
		}

		public static int CountActorByCommonName(HashSet<string> commonNames, Player owner)
		{
			return owner.World.Actors.Count(a => !a.IsDead && a.Owner == owner &&
				commonNames.Contains(a.Info.Name));
		}

		public static int CountBuildingByCommonName(HashSet<string> buildings, Player owner)
		{
			return GetActorsWithTrait<Building>(owner.World)
				.Count(a => a.Owner == owner && buildings.Contains(a.Info.Name));
		}

		public static List<Actor> FindEnemiesByCommonName(HashSet<string> commonNames, Player player)
		{
			return player.World.Actors.Where(a => !a.IsDead && player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy &&
				commonNames.Contains(a.Info.Name)).ToList();
		}

		public static ActorInfo GetInfoByCommonName(HashSet<string> names, Player owner)
		{
			return owner.World.Map.Rules.Actors.Where(k => names.Contains(k.Key)).Random(owner.World.LocalRandom).Value;
		}

		public static void BotDebug(string format, params object[] args)
		{
			if (Game.Settings.Debug.BotDebug)
				TextNotificationsManager.Debug(format, args);
		}
	}
}
