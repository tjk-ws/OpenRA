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
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Support power type to fire a burst of armaments.")]
	public class FireArmamentPowerInfo : SupportPowerInfo
	{
		[Desc("The `Name` of the armaments this support power is allowed to fire.")]
		public readonly string ArmamentName = "superweapon";

		[Desc("If `AllowMultiple` is `false`, how many instances of this support power are allowed to fire.",
		      "Actual instances might end up less due to range/etc.")]
		public readonly int MaximumFiringInstances = 1;

		[Desc("Amount of time before detonation to remove the beacon.")]
		public readonly int BeaconRemoveAdvance = 25;

		[Desc("Range of cells the camera should reveal around target cell.")]
		public readonly WDist CameraRange = WDist.Zero;

		[Desc("Can the camera reveal shroud generated by the GeneratesShroud trait?")]
		public readonly bool RevealGeneratedShroud = true;

		[Desc("Reveal cells to players with these stances only.")]
		public readonly Stance CameraStances = Stance.Ally;

		[Desc("Amount of time before firing to spawn the camera.")]
		public readonly int CameraSpawnAdvance = 25;

		[Desc("Amount of time after firing to remove the camera.")]
		public readonly int CameraRemoveDelay = 25;

		public readonly WDist TargetCircleRange = WDist.Zero;
		public readonly Color TargetCircleColor = Color.White;
		public readonly bool TargetCircleUsePlayerColor = false;

		public override object Create(ActorInitializer init) { return new FireArmamentPower(init.Self, this); }
	}

	public class FireArmamentPower : SupportPower, ITick, INotifyBurstComplete, INotifyCreated, IResolveOrder
	{
		public readonly FireArmamentPowerInfo FireArmamentPowerInfo;

		IFacing facing;
		HashSet<Armament> activeArmaments;

		bool turreted;
		HashSet<Turreted> turrets;

		bool enabled;
		int ticks;
		int estimatedTicks;
		Target target;

		public Armament[] Armaments;

		public FireArmamentPower(Actor self, FireArmamentPowerInfo info)
			: base(self, info)
		{
			FireArmamentPowerInfo = info;
			enabled = false;
		}

		void INotifyCreated.Created(Actor self)
		{
			facing = self.TraitOrDefault<IFacing>();
			Armaments = self.TraitsImplementing<Armament>().Where(t => t.Info.Name.Contains(FireArmamentPowerInfo.ArmamentName)).ToArray();

			var armamentturrets = Armaments.Select(x => x.Info.Turret).ToHashSet();
			turreted = self.TraitsImplementing<Turreted>().Where(x => armamentturrets.Contains(x.Name)).Count() > 0;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);

			if (FireArmamentPowerInfo.MaximumFiringInstances > 1)
				return;

			Activation(self, order);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString.Contains(Info.OrderName))
				Activation(self, order);
		}

		void Activation(Actor self, Order order)
		{
			activeArmaments = Armaments.Where(x => !x.IsTraitDisabled).ToHashSet();

			if (turreted)
			{
				var armamentturrets = activeArmaments.Select(x => x.Info.Turret).ToHashSet();

				// TODO: Fix this when upgradable Turreteds arrive.
				turrets = self.TraitsImplementing<Turreted>().Where(x => armamentturrets.Contains(x.Name)).ToHashSet();
			}

			if (self.Owner.IsAlliedWith(self.World.RenderPlayer))
				Game.Sound.Play(SoundType.World, FireArmamentPowerInfo.LaunchSound);
			else
				Game.Sound.Play(SoundType.World, FireArmamentPowerInfo.IncomingSound);

			target = order.Target;

			enabled = true;

			// TODO: Estimate the projectile travel time somehow
			estimatedTicks = activeArmaments.Max(x => x.FireDelay);

			if (FireArmamentPowerInfo.CameraRange != WDist.Zero)
			{
				var type = FireArmamentPowerInfo.RevealGeneratedShroud ? Shroud.SourceType.Visibility
					: Shroud.SourceType.PassiveVisibility;

				self.World.AddFrameEndTask(w => w.Add(new RevealShroudEffect(target.CenterPosition, FireArmamentPowerInfo.CameraRange, type, self.Owner,
					FireArmamentPowerInfo.CameraStances, estimatedTicks - FireArmamentPowerInfo.CameraSpawnAdvance,
					FireArmamentPowerInfo.CameraSpawnAdvance + FireArmamentPowerInfo.CameraRemoveDelay)));
			}

			if (FireArmamentPowerInfo.DisplayBeacon)
			{
				var beacon = new Beacon(
					order.Player,
					target.CenterPosition,
					FireArmamentPowerInfo.BeaconPaletteIsPlayerPalette,
					FireArmamentPowerInfo.BeaconPalette,
					FireArmamentPowerInfo.BeaconImage,
					FireArmamentPowerInfo.BeaconSequence,
					FireArmamentPowerInfo.BeaconPoster,
					FireArmamentPowerInfo.BeaconPosterPalette,
					FireArmamentPowerInfo.ArrowSequence,
					FireArmamentPowerInfo.CircleSequence,
					FireArmamentPowerInfo.ClockSequence,
					() => FractionComplete);

				Action removeBeacon = () => self.World.AddFrameEndTask(w =>
				                                                       {
					w.Remove(beacon);
					beacon = null;
				});

				self.World.AddFrameEndTask(w =>
				                           {
					w.Add(beacon);
					w.Add(new DelayedAction(estimatedTicks - FireArmamentPowerInfo.BeaconRemoveAdvance, removeBeacon));
				});
			}

			ticks = 0;
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			Game.Sound.PlayToPlayer(SoundType.UI, manager.Self.Owner, FireArmamentPowerInfo.SelectTargetSound);
			self.World.OrderGenerator = new SelectArmamentPowerTarget(self, order, manager, this);
		}

		void ITick.Tick(Actor self)
		{
			if (!enabled)
				return;

			if (turreted)
			{
				foreach (var t in turrets)
				{
					if (!t.FaceTarget(self, target))
						return;
				}
			}

			foreach (var a in activeArmaments) {
				a.CheckFire(self, facing, target);
			}

			ticks++;

			if (!activeArmaments.Any())
				enabled = false;
		}

		void INotifyBurstComplete.FiredBurst(Actor self, Target target, Armament a)
		{
			self.World.AddFrameEndTask(w => activeArmaments.Remove(a));
		}

		public bool IsActive(Actor self)
		{
			var activeArmaments = Armaments.Where(x => !x.IsTraitDisabled).ToHashSet();

			var armamentTurrets = activeArmaments.Select(x => x.Info.Turret).ToHashSet();

			// TODO: Fix this when upgradable Turreteds arrive.
			turrets = self.TraitsImplementing<Turreted>().Where(x => armamentTurrets.Contains(x.Name)).ToHashSet();

			return activeArmaments.Count > 0 && (!turreted || turrets.Count > 0);
		}

		float FractionComplete { get { return ticks * 1f / estimatedTicks; } }
	}

	public class SelectArmamentPowerTarget : IOrderGenerator
	{
		readonly Actor self;
		readonly SupportPowerManager manager;
		readonly string order;
		readonly FireArmamentPower power;

		readonly IEnumerable<Tuple<FireArmamentPower, WDist, WDist>> instances;

		public SelectArmamentPowerTarget(Actor self, string order, SupportPowerManager manager, FireArmamentPower power)
		{
			// Clear selection if using Left-Click Orders
			if (Game.Settings.Game.UseClassicMouseStyle)
				manager.Self.World.Selection.Clear();

			this.self = self;
			this.manager = manager;
			this.order = order;
			this.power = power;

			instances = GetActualInstances(self, power);
		}

		IEnumerable<Tuple<FireArmamentPower, WDist, WDist>> GetActualInstances(Actor self, FireArmamentPower power)
		{
			if (!power.Info.AllowMultiple)
			{
				var actorswithpower = self.World.ActorsWithTrait<FireArmamentPower>()
					.Where(x => x.Actor.Owner == self.Owner
						&& x.Trait.FireArmamentPowerInfo.OrderName.Contains(power.FireArmamentPowerInfo.OrderName)
						&& x.Trait.IsActive(x.Actor));
				foreach (var a in actorswithpower)
				{
					yield return Tuple.Create(a.Trait,
						a.Trait.Armaments.Where(x => !x.IsTraitDisabled).Min(x => x.Weapon.MinRange),
						a.Trait.Armaments.Where(x => !x.IsTraitDisabled).Max(x => x.Weapon.Range));
				}
			}
			else
			{
				yield return Tuple.Create(power,
					power.Armaments.Where(x => !x.IsTraitDisabled).Min(a => a.Weapon.MinRange),
					power.Armaments.Where(x => !x.IsTraitDisabled).Max(a => a.Weapon.Range));
			}

			yield break;
		}

		IEnumerable<Order> IOrderGenerator.Order(World world, CPos xy, int2 worldpixel, MouseInput mi)
		{
			var pos = world.Map.CenterOfCell(xy);

			world.CancelInputMode();
			if (mi.Button == MouseButton.Left && IsValidTargetCell(xy))
			{
				yield return new Order(order, manager.Self, Target.FromCell(world, xy), false) { SuppressVisualFeedback = true };

				var actors = instances.Where(x => !x.Item1.IsTraitPaused && !x.Item1.IsTraitDisabled
					&& (x.Item1.Self.CenterPosition - pos).HorizontalLengthSquared < x.Item3.LengthSquared)
					.OrderBy(x => (x.Item1.Self.CenterPosition - pos).HorizontalLengthSquared).Select(x => x.Item1.Self).Take(power.FireArmamentPowerInfo.MaximumFiringInstances);

				foreach (var a in actors)
				{
					yield return new Order(order, a, Target.FromCell(world, xy), false) { SuppressVisualFeedback = true };
				}
			}
		}

		public virtual void Tick(World world)
		{
			// Cancel the OG if we can't use the power
			if (!manager.Powers.ContainsKey(order))
				world.CancelInputMode();
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }

		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world)
		{
			foreach (var i in instances)
			{
				if (!i.Item1.IsTraitPaused && !i.Item1.IsTraitDisabled)
				{
					yield return new RangeCircleRenderable(
						i.Item1.Self.CenterPosition,
						i.Item2,
						0,
						Color.Red,
						Color.FromArgb(96, Color.Black));

					yield return new RangeCircleRenderable(
						i.Item1.Self.CenterPosition,
						i.Item3,
						0,
						Color.Red,
						Color.FromArgb(96, Color.Black));
				}
			}

			if (power.FireArmamentPowerInfo.TargetCircleRange > WDist.Zero)
			{
				var xy = wr.Viewport.ViewToWorld(Viewport.LastMousePos);

				var targetRangeColor = power.FireArmamentPowerInfo.TargetCircleUsePlayerColor
					? power.Self.Owner.Color : power.FireArmamentPowerInfo.TargetCircleColor;

				yield return new RangeCircleRenderable(
					world.Map.CenterOfCell(xy),
					power.FireArmamentPowerInfo.TargetCircleRange,
					0,
					targetRangeColor,
					Color.FromArgb(96, Color.Black));
			}
		}

		string IOrderGenerator.GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			return IsValidTargetCell(cell) ? power.FireArmamentPowerInfo.Cursor : "generic-blocked";
		}

		bool IsValidTargetCell(CPos xy)
		{
			if (!self.World.Map.Contains(xy))
				return false;

			var tc = Target.FromCell(self.World, xy);

			return instances.Any(x => !x.Item1.IsTraitPaused && !x.Item1.IsTraitDisabled
				&& tc.IsInRange(x.Item1.Self.CenterPosition, x.Item3) && !tc.IsInRange(x.Item1.Self.CenterPosition, x.Item2));
		}

		public void Deactivate() { }
	}
}