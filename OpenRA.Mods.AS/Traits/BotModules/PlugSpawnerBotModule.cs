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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Allows the AI to have a single plug type.", "Plugs are all spawned.", "Use multiple variants of this trait to support more kind.")]
	public class PlugSpawnerBotModuleInfo : ConditionalTraitInfo
	{
		[ActorReference(typeof(PlugInfo))]
		[FieldLoader.Require]
		[Desc("What plug the AI can spawn.")]
		public readonly string Plug = null;

		[ActorReference(typeof(PluggableInfo))]
		[FieldLoader.Require]
		[Desc("What actors the AI can spawn this plug on.")]
		public readonly HashSet<string> Pluggables = new HashSet<string> { };

		[Desc("Plug spawning interval.")]
		public readonly int Interval = 50;

		public override object Create(ActorInitializer init) { return new PlugSpawnerBotModule(init.Self, this); }
	}

	public class PlugSpawnerBotModule : ConditionalTrait<PlugSpawnerBotModuleInfo>, IBotTick, IResolveOrder, INotifyCreated
	{
		readonly World world;

		string plugType;
		int ticks;

		public PlugSpawnerBotModule(Actor self, PlugSpawnerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			ticks = Info.Interval;
		}

		protected override void Created(Actor self)
		{
			plugType = world.Map.Rules.Actors[Info.Plug].TraitInfo<PlugInfo>().Type;

			base.Created(self);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--ticks > 0)
				return;

			var player = bot.Player;

			var targetActors = world.Actors.Where(x => x.IsInWorld && !x.IsDead && x.Owner == player && Info.Pluggables.Contains(x.Info.Name));

			if (!targetActors.Any())
				return;

			var target = targetActors
				.Select(x => (x, x.TraitsImplementing<Pluggable>().FirstOrDefault(p => p.AcceptsPlug(x, plugType))))
				.FirstOrDefault(x => x.Item2 != null);

			if (target.Item1 != null)
			{
				var building = target.Item1.TraitOrDefault<Building>();

				var offset = building != null
					? building.TopLeft + target.Item2.Info.Offset
					: world.Map.CellContaining(target.Item1.CenterPosition) + target.Item2.Info.Offset;

				var order = new Order("PlacePlugAI", player.PlayerActor, Target.FromCell(world, offset), false)
				{
					TargetString = Info.Plug,
					ExtraData = player.PlayerActor.ActorID,
					SuppressVisualFeedback = true
				};

				world.IssueOrder(order);
			}

			ticks = Info.Interval;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (IsTraitDisabled)
				return;

			var os = order.OrderString;
			if (os != "PlacePlugAI")
				return;

			var ts = order.TargetString;
			if (ts != Info.Plug)
				return;

			self.World.AddFrameEndTask(w =>
			{
				var targetActor = w.GetActorById(order.ExtraData);
				var targetLocation = w.Map.CellContaining(order.Target.CenterPosition);

				if (targetActor == null || targetActor.IsDead)
					return;

				var actorInfo = self.World.Map.Rules.Actors[order.TargetString];

				var faction = self.Owner.Faction.InternalName;
				var buildingInfo = actorInfo.TraitInfo<BuildingInfo>();

				var buildableInfo = actorInfo.TraitInfoOrDefault<BuildableInfo>();
				if (buildableInfo != null && buildableInfo.ForceFaction != null)
					faction = buildableInfo.ForceFaction;

				var host = self.World.WorldActor.Trait<BuildingInfluence>().GetBuildingAt(targetLocation);
				if (host == null)
					return;

				var plugInfo = actorInfo.TraitInfoOrDefault<PlugInfo>();
				if (plugInfo == null)
					return;

				var location = host.Location;
				var pluggable = host.TraitsImplementing<Pluggable>()
					.FirstOrDefault(p => location + p.Info.Offset == targetLocation && p.AcceptsPlug(host, plugInfo.Type));

				if (pluggable == null)
					return;

				pluggable.EnablePlug(host, plugInfo.Type);
				foreach (var s in buildingInfo.BuildSounds)
					Game.Sound.PlayToPlayer(SoundType.World, order.Player, s, host.CenterPosition);
			});
		}

		protected override void TraitEnabled(Actor self)
		{
			ticks = Info.Interval;
		}
	}
}
