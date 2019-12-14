#region Copyright & License Information
/*
 * Copyright 2007-2019 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Manages AI Base Expansion Vehicles. Does not handle building.")]
	public class BevManagerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Actor types that are considered construction yards (base centers).")]
		public readonly HashSet<string> ConstructionYardTypes = new HashSet<string>();

		[Desc("Actor types that are considered BEVs (deploy into base expansions).")]
		public readonly HashSet<string> BevTypes = new HashSet<string>();

		[Desc("Delay (in ticks) between looking for and giving out orders to new BEVs.")]
		public readonly int ScanForNewBevInterval = 20;

		[Desc("Minimum distance in cells from center of the base when checking for BEV deployment location.")]
		public readonly int MinBaseRadius = 2;

		[Desc("Maximum distance in cells from center of the base when checking for BEV deployment location.",
			"Only applies if RestrictBevDeploymentFallbackToBase is enabled and there's at least one construction yard.")]
		public readonly int MaxBaseRadius = 20;

		[Desc("Should deployment of additional BEVs be restricted to MaxBaseRadius if explicit deploy locations are missing or occupied?")]
		public readonly bool RestrictBevDeploymentFallbackToBase = true;

		public override object Create(ActorInitializer init) { return new BevManagerBotModule(init.Self, this); }
	}

	public class BevManagerBotModule : ConditionalTrait<BevManagerBotModuleInfo>, IBotTick, IBotPositionsUpdated, IGameSaveTraitData
	{
		public CPos GetRandomBaseCenter()
		{
			var randomConstructionYard = world.Actors.Where(a => a.Owner == player &&
				Info.ConstructionYardTypes.Contains(a.Info.Name))
				.RandomOrDefault(world.LocalRandom);

			return randomConstructionYard != null ? randomConstructionYard.Location : initialBaseCenter;
		}

		readonly World world;
		readonly Player player;

		readonly Predicate<Actor> unitCannotBeOrdered;

		CPos initialBaseCenter;
		int scanInterval;
		bool firstTick = true;

		public BevManagerBotModule(Actor self, BevManagerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			unitCannotBeOrdered = a => a.Owner != player || a.IsDead || !a.IsInWorld;
		}

		protected override void TraitEnabled(Actor self)
		{
			// Avoid all AIs reevaluating assignments on the same tick, randomize their initial evaluation delay.
			scanInterval = world.LocalRandom.Next(Info.ScanForNewBevInterval, Info.ScanForNewBevInterval * 2);
		}

		void IBotPositionsUpdated.UpdatedBaseCenter(CPos newLocation)
		{
			initialBaseCenter = newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation) { }

		void IBotTick.BotTick(IBot bot)
		{
			if (firstTick)
			{
				DeployMcvs(bot, false);
				firstTick = false;
			}

			if (--scanInterval <= 0)
			{
				scanInterval = Info.ScanForNewBevInterval;
				DeployMcvs(bot, true);
			}
		}

		void DeployMcvs(IBot bot, bool chooseLocation)
		{
			var newBEVs = world.ActorsHavingTrait<Transforms>()
				.Where(a => a.Owner == player && a.IsIdle && Info.BevTypes.Contains(a.Info.Name));

			foreach (var bev in newBEVs)
				DeployBev(bot, bev, chooseLocation);
		}

		// Find any BEV and deploy them at a sensible location.
		void DeployBev(IBot bot, Actor bev, bool move)
		{
			if (move)
			{
				// If we lack a base, we need to make sure we don't restrict deployment of the MCV to the base!
				var restrictToBase = Info.RestrictBevDeploymentFallbackToBase && AIUtils.CountBuildingByCommonName(Info.ConstructionYardTypes, player) > 0;

				var transformsInfo = bev.Info.TraitInfo<TransformsInfo>();
				var desiredLocation = ChooseBevDeployLocation(transformsInfo.IntoActor, transformsInfo.Offset, restrictToBase);
				if (desiredLocation == null)
					return;

				bot.QueueOrder(new Order("Move", bev, Target.FromCell(world, desiredLocation.Value), true));
			}

			bot.QueueOrder(new Order("DeployTransform", bev, true));
		}

		CPos? ChooseBevDeployLocation(string actorType, CVec offset, bool distanceToBaseIsImportant)
		{
			var actorInfo = world.Map.Rules.Actors[actorType];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return null;

			// Find the buildable cell that is closest to pos and centered around center
			Func<CPos, CPos, int, int, CPos?> findPos = (center, target, minRange, maxRange) =>
			{
				var cells = world.Map.FindTilesInAnnulus(center, minRange, maxRange);

				// Sort by distance to target if we have one
				if (center != target)
					cells = cells.OrderBy(c => (c - target).LengthSquared);
				else
					cells = cells.Shuffle(world.LocalRandom);

				foreach (var cell in cells)
					if (world.CanPlaceBuilding(cell + offset, actorInfo, bi, null))
						return cell;

				return null;
			};

			var baseCenter = GetRandomBaseCenter();

			return findPos(baseCenter, baseCenter, Info.MinBaseRadius,
				distanceToBaseIsImportant ? Info.MaxBaseRadius : world.Map.Grid.MaximumTileSearchRange);
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			return new List<MiniYamlNode>()
			{
				new MiniYamlNode("InitialBaseCenter", FieldSaver.FormatValue(initialBaseCenter))
			};
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, List<MiniYamlNode> data)
		{
			if (self.World.IsReplay)
				return;

			var initialBaseCenterNode = data.FirstOrDefault(n => n.Key == "InitialBaseCenter");
			if (initialBaseCenterNode != null)
				initialBaseCenter = FieldLoader.GetValue<CPos>("InitialBaseCenter", initialBaseCenterNode.Value.Value);
		}
	}
}
