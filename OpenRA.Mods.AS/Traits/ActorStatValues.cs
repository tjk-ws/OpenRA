#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
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
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public class ActorStatValuesInfo : TraitInfo
	{
		[Desc("Overrides the icon for the unit for the stats.")]
		public readonly string Icon;

		[PaletteReference(nameof(IconPaletteIsPlayerPalette))]
		[Desc("Overrides the icon palette for the unit for the stats.")]
		public readonly string IconPalette;

		[Desc("Overrides if icon palette for the unit for the stats is a player palette.")]
		public readonly bool? IconPaletteIsPlayerPalette;

		[Desc("Armament names to use for weapon stats.")]
		public readonly string[] Armaments;

		[Desc("Use this value for base damage of the unit for the stats.")]
		public readonly int? Damage;

		[Desc("Use this value for weapon spread of the unit for the stats.")]
		public readonly WDist? Spread;

		[Desc("Overrides the reload delay value from the weapons for the stats.")]
		public readonly int? ReloadDelay;

		[Desc("Overrides the sight value from RevealsShroud trait for the stats.")]
		public readonly WDist? Sight;

		[Desc("Overrides the range value from the weapons for the stats.")]
		public readonly WDist? Range;

		[Desc("Overrides the movement speed value from Mobile or Aircraft traits for the stats.")]
		public readonly int? Speed;

		[Desc("Don't show these armor classes for the Armor stat.")]
		public readonly string[] ArmorsToIgnore;

		[ActorReference]
		[Desc("Actor to use for Tooltip when hovering of the icon.")]
		public readonly string TooltipActor;

		[Desc("Use Damage and Spread instead of Damage, Range and Reload Delay values for weapon.")]
		public readonly bool ExplosionWeapon = false;

		public override object Create(ActorInitializer init) { return new ActorStatValues(this, init.Self); }
	}

	public class ActorStatValues : INotifyCreated
	{
		ActorStatValuesInfo info;

		public string Icon;
		public string IconPalette;
		public bool IconPaletteIsPlayerPalette;
		public WithStatIconOverlay[] IconOverlays;

		public int Speed;

		public Tooltip[] Tooltips;
		public Armor[] Armors;
		public RevealsShroud[] RevealsShrouds;
		public AttackBase[] AttackBases;
		public Armament[] Armaments;
		public Power[] Powers;

		public IHealth Health;
		public Shielded Shielded;

		public Mobile Mobile;
		public Aircraft Aircraft;

		public MindController MindController;

		public Harvester Harvester;
		public Cargo Cargo;
		public SharedCargo SharedCargo;
		public Garrisonable Garrisonable;
		public CarrierMaster CarrierMaster;

		public IRevealsShroudModifier[] SightModifiers;
		public IFirepowerModifier[] FirepowerModifiers;
		public IReloadModifier[] ReloadModifiers;
		public IRangeModifier[] RangeModifiers;
		public ISpeedModifier[] SpeedModifiers;
		public IPowerModifier[] PowerModifiers;

		public ActorInfo TooltipActor;

		PlayerResources playerResources;

		public ActorStatValues(ActorStatValuesInfo info, Actor self)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			var bi = self.Info.TraitInfoOrDefault<BuildableInfo>();
			if (info.Icon != null)
				Icon = info.Icon;
			else
				if (bi != null)
					Icon = bi.Icon;

			if (info.IconPalette != null)
				IconPalette = info.IconPalette;
			else
				if (bi != null)
					IconPalette = bi.IconPalette;

			if (info.IconPaletteIsPlayerPalette != null)
				IconPaletteIsPlayerPalette = info.IconPaletteIsPlayerPalette.Value;
			else
				if (bi != null)
					IconPaletteIsPlayerPalette = bi.IconPaletteIsPlayerPalette;

			IconOverlays = self.TraitsImplementing<WithStatIconOverlay>().ToArray();

			Tooltips = self.TraitsImplementing<Tooltip>().ToArray();
			Armors = self.TraitsImplementing<Armor>().Where(a => !info.ArmorsToIgnore.Contains(a.Info.Type)).ToArray();
			RevealsShrouds = self.TraitsImplementing<RevealsShroud>().ToArray();
			Powers = self.TraitsImplementing<Power>().ToArray();

			AttackBases = self.TraitsImplementing<AttackBase>().ToArray();
			Armaments = self.TraitsImplementing<Armament>().Where(a => IsValidArmament(a.Info.Name)).ToArray();

			Health = self.TraitOrDefault<IHealth>();
			Shielded = self.TraitOrDefault<Shielded>();

			Mobile = self.TraitOrDefault<Mobile>();
			Aircraft = self.TraitOrDefault<Aircraft>();

			MindController = self.TraitOrDefault<MindController>();

			Harvester = self.TraitOrDefault<Harvester>();
			Cargo = self.TraitOrDefault<Cargo>();
			SharedCargo = self.TraitOrDefault<SharedCargo>();
			Garrisonable = self.TraitOrDefault<Garrisonable>();
			CarrierMaster = self.TraitOrDefault<CarrierMaster>();

			if (info.Speed != null)
				Speed = info.Speed.Value;
			else if (Aircraft != null)
				Speed = Aircraft.Info.Speed;
			else if (Mobile != null)
				Speed = Mobile.Info.Speed;

			SightModifiers = self.TraitsImplementing<IRevealsShroudModifier>().ToArray();
			FirepowerModifiers = self.TraitsImplementing<IFirepowerModifier>().ToArray();
			ReloadModifiers = self.TraitsImplementing<IReloadModifier>().ToArray();
			RangeModifiers = self.TraitsImplementing<IRangeModifier>().ToArray();
			SpeedModifiers = self.TraitsImplementing<ISpeedModifier>().ToArray();
			PowerModifiers = self.TraitsImplementing<IPowerModifier>().ToArray();

			if (info.TooltipActor != null)
				TooltipActor = self.World.Map.Rules.Actors[info.TooltipActor];
			else
				TooltipActor = self.Info;

			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
		}

		public bool IsValidArmament(string armament)
		{
			if (info.Armaments != null)
				return info.Armaments.Contains(armament);
			else
				return AttackBases.Any(ab => ab.Info.Armaments.Contains(armament));
		}

		public bool ShowWeaponData() { return AttackBases.Any() && Armaments.Any(); }
		public bool ShowSpeed() { return Mobile != null || Aircraft != null; }
		public bool ShowPower() { return !ShowSpeed() && Powers.Any(p => !p.IsTraitDisabled); }

		public string GetIconFor(int slot)
		{
			if (slot == 1)
			{
				if (Shielded != null && !Shielded.IsTraitDisabled && Shielded.Strength > 0)
					return "actor-stats-shield";

				return "";
			}

			if (slot == 2)
				return "";

			if (slot == 3)
			{
				if (ShowSpeed())
					return "actor-stats-speed";
				else if (ShowPower())
					return "actor-stats-power";
				else
					return null;
			}

			if (slot == 4)
			{
				if (ShowWeaponData())
				{
					if (MindController != null)
						return "actor-stats-mindcontrol";

					return "";
				}
				else
					return null;
			}

			if (slot == 5)
			{
				if (ShowWeaponData())
				{
					if (info.ExplosionWeapon)
						return "actor-stats-spread";

					return "";
				}
				else
					return null;
			}

			if (slot == 6)
			{
				if (ShowWeaponData() && !info.ExplosionWeapon)
					return "";
				else
					return null;
			}

			if (slot == 7)
			{
				if (Harvester != null)
					return "actor-stats-resources";
				else if (Cargo != null || SharedCargo != null || Garrisonable != null)
					return "actor-stats-cargo";
				else if (CarrierMaster != null)
					return "actor-stats-carrier";
				else
					return null;
			}

			return null;
		}

		public string GetValueFor(int slot)
		{
			if (slot == 1)
			{
				if (Shielded != null && !Shielded.IsTraitDisabled && Shielded.Strength > 0)
					return (Shielded.Strength / 100).ToString() + " / " + (Shielded.Info.MaxStrength / 100).ToString();

				var activeArmor = Armors.FirstOrDefault(a => !a.IsTraitDisabled);
				return activeArmor?.Info.Type;
			}

			if (slot == 2)
			{
				var revealsShroudValue = WDist.Zero;
				if (info.Sight != null)
					revealsShroudValue = info.Sight.Value;
				else if (RevealsShrouds.Any())
				{
					var revealsShroudTrait = RevealsShrouds.MaxBy(rs => rs.Info.Range);
					if (revealsShroudTrait != null)
						revealsShroudValue = revealsShroudTrait.Info.Range;
				}

				foreach (var rsm in SightModifiers.Select(rsm => rsm.GetRevealsShroudModifier()))
					revealsShroudValue = revealsShroudValue * rsm / 100;

				return Math.Round((float)revealsShroudValue.Length / 1024, 2).ToString();
			}

			if (slot == 3)
			{
				if (ShowSpeed())
				{
					var speedValue = Speed;
					foreach (var sm in SpeedModifiers.Select(sm => sm.GetSpeedModifier()))
						speedValue = speedValue * sm / 100;

					return speedValue.ToString();
				}
				else if (ShowPower())
				{
					var powerValue = Powers.Where(p => !p.IsTraitDisabled).Sum(p => p.Info.Amount);
					foreach (var pm in PowerModifiers.Select(pm => pm.GetPowerModifier()))
						powerValue = powerValue * pm / 100;

					return powerValue.ToString();
				}
				else
					return "";
			}

			if (slot == 4)
			{
				if (ShowWeaponData())
				{
					if (MindController != null)
						return MindController.Slaves.Count().ToString() + " / " + MindController.Info.Capacity.ToString();

					var damageValue = 0;
					if (info.Damage != null)
						damageValue = info.Damage.Value;
					else
					{
						var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
						if (enabledArmaments.Any())
							damageValue = enabledArmaments.Sum(ar => ar.Info.Damage ?? 0);
					}

					foreach (var dm in FirepowerModifiers.Select(fm => fm.GetFirepowerModifier()))
						damageValue = damageValue * dm / 100;

					return damageValue.ToString();
				}
				else
					return "";
			}

			if (slot == 5)
			{
				if (ShowWeaponData())
				{
					if (info.ExplosionWeapon)
					{
						var spreadValue = WDist.Zero;
						if (info.Spread != null)
							spreadValue = info.Spread.Value;
						else
						{
							var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
							if (enabledArmaments.Any())
								spreadValue = enabledArmaments.Max(ar => ar.Info.Spread ?? WDist.Zero);
						}

						return Math.Round((float)spreadValue.Length / 1024, 2).ToString();
					}

					var rangeValue = WDist.Zero;
					if (info.Range != null)
						rangeValue = info.Range.Value;
					else
					{
						var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
						if (enabledArmaments.Any())
							rangeValue = enabledArmaments.Max(ar => ar.Info.Range ?? ar.Weapon.Range);
					}

					foreach (var rm in RangeModifiers.Select(rm => rm.GetRangeModifier()))
						rangeValue = rangeValue * rm / 100;

					return Math.Round((float)rangeValue.Length / 1024, 2).ToString();
				}
				else
					return "";
			}

			if (slot == 6)
			{
				if (ShowWeaponData() && !info.ExplosionWeapon)
				{
					var rofValue = 0;
					if (info.ReloadDelay != null)
						rofValue = info.ReloadDelay.Value;
					else
					{
						var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
						if (enabledArmaments.Any())
							rofValue = enabledArmaments.Min(ar => ar.Info.ReloadDelay ?? ar.Weapon.ReloadDelay);
					}

					foreach (var rm in ReloadModifiers.Select(sm => sm.GetReloadModifier()))
						rofValue = rofValue * rm / 100;

					return rofValue.ToString();
				}
				else
					return "";
			}

			if (slot == 7)
			{
				if (Harvester != null)
				{
					var currentContents = Harvester.Contents.Values.Sum().ToString();
					var capacity = Harvester.Info.Capacity.ToString();

					var value = 0;
					foreach (var content in Harvester.Contents)
						value += playerResources.Info.ResourceValues[content.Key] * content.Value;

					return currentContents + " / " + capacity + " ($" + value.ToString() + ")";
				}
				else if (Cargo != null)
				{
					return Cargo.TotalWeight + " / " + Cargo.Info.MaxWeight;
				}
				else if (SharedCargo != null)
				{
					return SharedCargo.Manager.TotalWeight + " / " + SharedCargo.Manager.Info.MaxWeight;
				}
				else if (Garrisonable != null)
				{
					return Garrisonable.TotalWeight + " / " + Garrisonable.Info.MaxWeight;
				}
				else if (CarrierMaster != null)
				{
					var slaves = CarrierMaster.SlaveEntries.Where(s => s.IsValid);
					return slaves.Where(x => !x.IsLaunched).Count().ToString() + " / " + slaves.Count().ToString() + " / " + CarrierMaster.Info.Actors.Count().ToString();
				}
				else
					return "";
			}

			return "";
		}
	}
}
