#region Copyright & License Information
/*
 * Copyright 2007-2021 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	// Activity to move to a location and construct a building there.
	public class BuildOnSite : Activity
	{
		readonly World world;
		readonly Target centerBuildingTarget;
		readonly CPos centerTarget;
		readonly Order order;
		readonly string faction;
		readonly BuildingInfo buildingInfo;
		readonly ActorInfo buildingActor;
		readonly WDist minRange;
		readonly IMove move;
		readonly IMoveInfo moveInfo;
		readonly PlaceBuildingInfo placeBuildingInfo;
		readonly ProductionQueue queue;
		readonly ProductionItem item;

		public BuildOnSite(World world, Actor self, Order order, string faction, BuildingInfo buildingInfo, ProductionQueue queue, ProductionItem item)
		{
			this.buildingInfo = buildingInfo;
			this.world = world;
			this.order = order;
			this.faction = faction;
			centerBuildingTarget = order.Target;
			centerTarget = world.Map.CellContaining(centerBuildingTarget.CenterPosition);
			minRange = buildingInfo.BuilderRange;
			buildingActor = world.Map.Rules.Actors.FirstOrDefault(x => x.Key == order.TargetString).Value;
			move = self.Trait<IMove>();
			moveInfo = self.Info.TraitInfo<IMoveInfo>();
			placeBuildingInfo = self.Owner.PlayerActor.Info.TraitInfo<PlaceBuildingInfo>();
			this.queue = queue;
			this.item = item;
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || self.IsDead) return true;

			// Move towards the target cell
			if (!centerBuildingTarget.IsInRange(self.CenterPosition, minRange))
			{
				QueueChild(move.MoveWithinRange(centerBuildingTarget, minRange, targetLineColor: moveInfo.GetTargetLineColor()));
				return false;
			}

			if (!world.CanPlaceBuilding(centerTarget, buildingActor, buildingInfo, self))
			{
				// Game.Debug("Attempting to clear site");

				// Try clear the area
				foreach (var ord in ClearBlockersOrders(self))
					world.IssueOrder(ord);
					// Game.Debug("Issued 1 order to clear site");

				Game.Sound.PlayNotification(world.Map.Rules, self.Owner, "Speech", placeBuildingInfo.CannotPlaceNotification, faction);

				return true;
			}

			self.World.AddFrameEndTask(w =>
			{
				if (!order.Queued)
					self.CancelActivity();

				var building = w.CreateActor(true, order.TargetString, new TypeDictionary
				{
					new LocationInit(centerTarget),
					new OwnerInit(order.Player),
					new FactionInit(faction),
					new PlaceBuildingInit()
				});

				foreach (var s in buildingInfo.BuildSounds)
						Game.Sound.PlayToPlayer(SoundType.World, order.Player, s, building.CenterPosition);

				Game.Sound.PlayNotification(world.Map.Rules, self.Owner, "Speech", buildingInfo.PlacedNotification, faction);
			});
			
			if (buildingInfo.RemoveBuilder) {
				self.QueueActivity(new RemoveSelf());
			}

			if (self != null)
				foreach (var nbp in self.TraitsImplementing<INotifyBuildingPlaced>())
					nbp.BuildingPlaced(self);

			queue.EndProduction(item);

			return true;
		}

		// Copied from PlaceBuildingOrderGenerator, triplicated in BuildOnSite and BuilderUnitBuildingOrderGenerator
		IEnumerable<Order> ClearBlockersOrders(Actor ownerActor)
		{
			var allTiles = buildingInfo.Tiles(centerTarget).ToArray();
			var adjacentTiles = Util.ExpandFootprint(allTiles, true).Except(allTiles)
				.Where(world.Map.Contains).ToList();

			var blockers = allTiles.SelectMany(world.ActorMap.GetActorsAt)
				.Where(a => a.Owner == ownerActor.Owner && a.IsIdle && a != ownerActor)
				.Select(a => new TraitPair<IMove>(a, a.TraitOrDefault<IMove>()))
				.Where(x => x.Trait != null);

			foreach (var blocker in blockers)
			{
				CPos moveCell;
				if (blocker.Trait is Mobile mobile)
				{
					var availableCells = adjacentTiles.Where(t => mobile.CanEnterCell(t)).ToList();
					if (availableCells.Count == 0)
						continue;

					moveCell = blocker.Actor.ClosestCell(availableCells);
				}
				else if (blocker.Trait is Aircraft)
					moveCell = blocker.Actor.Location;
				else
					continue;

				yield return new Order("Move", blocker.Actor, Target.FromCell(world, moveCell), false)
				{
					SuppressVisualFeedback = true
				};
			}
		}
	}
}
