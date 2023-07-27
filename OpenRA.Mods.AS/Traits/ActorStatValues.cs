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
using System.Linq;
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public enum ActorStatContent { None, Armor, Sight, Speed, Power, Damage, MindControl, Spread, ReloadDelay, MinRange, MaxRange, Harvester, Collector, CashTrickler, PeriodicProducer, Cargo, Carrier, Mob }

	public class ActorStatValuesInfo : TraitInfo
	{
		[Desc("Overrides the icon for the unit for the stats.")]
		public readonly string Icon;

		// Doesn't work properly with `bool?`.
		// [PaletteReference(nameof(IconPaletteIsPlayerPalette))]
		[Desc("Overrides the icon palette for the unit for the stats.")]
		public readonly string IconPalette;

		[Desc("Overrides if icon palette for the unit for the stats is a player palette.")]
		public readonly bool? IconPaletteIsPlayerPalette;

		[Desc("Types of stats to show.")]
		public readonly ActorStatContent[] Stats = { ActorStatContent.Armor, ActorStatContent.Sight };

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

		[Desc("Overrides the minimum range value from the weapons for the stats.")]
		public readonly WDist? MinimumRange;

		[Desc("Overrides the movement speed value from Mobile or Aircraft traits for the stats.")]
		public readonly int? Speed;

		[Desc("Don't show these armor classes for the Armor stat.")]
		public readonly string[] ArmorsToIgnore = Array.Empty<string>();

		[ActorReference]
		[Desc("Actor to use for Tooltip when hovering of the icon.")]
		public readonly string TooltipActor;

		[Desc("Prerequisites to enable upgrades, without them upgrades won't be shown." +
			"Only checked at the actor creation.")]
		public readonly string[] UpgradePrerequisites = Array.Empty<string>();

		[ActorReference]
		[Desc("Upgrades this actor is affected by.")]
		public readonly string[] Upgrades = Array.Empty<string>();

		[ActorReference(dictionaryReference: LintDictionaryReference.Values)]
		[Desc("Overrides available upgrades for the unit for the defined faction.")]
		public readonly Dictionary<string, string[]> FactionUpgrades = new();

		[ActorReference]
		[Desc("Which of the actors defined under Upgrades are produced by the actor itself, and only effects it.")]
		public readonly string[] LocalUpgrades = Array.Empty<string>();

		public override object Create(ActorInitializer init) { return new ActorStatValues(init, this); }
	}

	public class ActorStatValues : INotifyCreated, INotifyDisguised, INotifyOwnerChanged, INotifyProduction
	{
		readonly Actor self;
		public ActorStatValuesInfo Info;
		string faction;

		public string Icon;
		public string IconPalette;
		public bool IconPaletteIsPlayerPalette;
		public WithStatIconOverlay[] IconOverlays;

		public ActorStatContent[] CurrentStats;
		public ActorStatOverride[] StatOverrides;

		public int Speed;

		public ITooltip[] Tooltips;
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
		public ISupplyCollector Collector;
		public CashTrickler[] CashTricklers = Array.Empty<CashTrickler>();
		public PeriodicProducer[] PeriodicProducers = Array.Empty<PeriodicProducer>();
		public Cargo Cargo;
		public SharedCargo SharedCargo;
		public Garrisonable Garrisonable;
		public CarrierMaster CarrierMaster;
		public MobSpawnerMaster[] MobSpawnerMasters;

		public IRevealsShroudModifier[] SightModifiers;
		public IFirepowerModifier[] FirepowerModifiers;
		public IReloadModifier[] ReloadModifiers;
		public IRangeModifier[] RangeModifiers;
		public ISpeedModifier[] SpeedModifiers;
		public IPowerModifier[] PowerModifiers;
		public IResourceValueModifier[] ResourceValueModifiers;

		public ActorInfo TooltipActor;

		PlayerResources playerResources;
		TechTree techTree;

		public bool UpgradesEnabled;
		public string[] FactionUpgrades = Array.Empty<string>();
		public Dictionary<string, bool> Upgrades = new();

		public bool Disguised;
		public Player DisguisePlayer;
		public string DisguiseImage;
		public int DisguiseMaxHealth = 0;
		public string[] DisguiseStatIcons = new string[9];
		public string[] DisguiseStats = new string[9];
		public Dictionary<string, bool> DisguiseUpgrades = new();
		public string[] DisguiseFactionUpgrades = Array.Empty<string>();

		public ActorStatValues(ActorInitializer init, ActorStatValuesInfo info)
		{
			Info = info;
			this.self = init.Self;
			faction = init.GetValue<FactionInit, string>(init.Self.Owner.Faction.InternalName);

			self.World.ActorAdded += ActorAdded;
			self.World.ActorRemoved += ActorRemoved;
		}

		void INotifyCreated.Created(Actor self)
		{
			SetupCameos();
			IconOverlays = self.TraitsImplementing<WithStatIconOverlay>().ToArray();

			StatOverrides = self.TraitsImplementing<ActorStatOverride>().ToArray();
			CalculateStats();

			Tooltips = self.TraitsImplementing<ITooltip>().ToArray();
			Armors = self.TraitsImplementing<Armor>().Where(a => !Info.ArmorsToIgnore.Contains(a.Info.Type)).ToArray();
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
			Collector = self.TraitOrDefault<ISupplyCollector>();
			CashTricklers = self.TraitsImplementing<CashTrickler>().ToArray();
			PeriodicProducers = self.TraitsImplementing<PeriodicProducer>().ToArray();
			Cargo = self.TraitOrDefault<Cargo>();
			SharedCargo = self.TraitOrDefault<SharedCargo>();
			Garrisonable = self.TraitOrDefault<Garrisonable>();
			CarrierMaster = self.TraitOrDefault<CarrierMaster>();
			MobSpawnerMasters = self.TraitsImplementing<MobSpawnerMaster>().ToArray();

			if (Info.Speed != null)
				Speed = Info.Speed.Value;
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
			ResourceValueModifiers = self.TraitsImplementing<IResourceValueModifier>().ToArray();

			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			techTree = self.Owner.PlayerActor.Trait<TechTree>();

			UpgradesEnabled = Info.Upgrades.Length > 0 && techTree.HasPrerequisites(Info.UpgradePrerequisites);
			if (UpgradesEnabled)
			{
				FactionUpgrades = Info.FactionUpgrades.ContainsKey(faction) ? Info.FactionUpgrades[faction] : Info.Upgrades;
				foreach (var upgrade in FactionUpgrades)
					Upgrades.Add(upgrade, self.World.Actors.Where(a => a.Owner == self.Owner && a.Info.Name == upgrade).Any());
			}
		}

		void SetupCameos()
		{
			var bi = self.Info.TraitInfoOrDefault<BuildableInfo>();
			if (Info.Icon != null)
				Icon = Info.Icon;
			else
				if (bi != null)
					Icon = bi.Icon;

			if (Info.IconPalette != null)
				IconPalette = Info.IconPalette;
			else
				if (bi != null)
					IconPalette = bi.IconPalette;

			if (Info.IconPaletteIsPlayerPalette != null)
				IconPaletteIsPlayerPalette = Info.IconPaletteIsPlayerPalette.Value;
			else
				if (bi != null)
					IconPaletteIsPlayerPalette = bi.IconPaletteIsPlayerPalette;

			if (Info.TooltipActor != null)
				TooltipActor = self.World.Map.Rules.Actors[Info.TooltipActor];
			else
				TooltipActor = self.Info;
		}

		public void CalculateStats()
		{
			CurrentStats = Info.Stats;
			var statOverride = StatOverrides.Where(so => !so.IsTraitDisabled).FirstOrDefault();
			if (statOverride != null)
				CurrentStats = statOverride.Info.Stats;
		}

		void ActorAdded(Actor a)
		{
			if (!UpgradesEnabled || Info.LocalUpgrades.Contains(a.Info.Name))
				return;

			if (a.Owner == self.Owner && Upgrades.ContainsKey(a.Info.Name))
				Upgrades[a.Info.Name] = true;

			if (a.Owner == DisguisePlayer && DisguiseUpgrades.ContainsKey(a.Info.Name))
				DisguiseUpgrades[a.Info.Name] = true;
		}

		void ActorRemoved(Actor a)
		{
			if (!UpgradesEnabled || Info.LocalUpgrades.Contains(a.Info.Name))
				return;

			// There may be others, just check in general.
			if (a.Owner == self.Owner && Upgrades.ContainsKey(a.Info.Name))
				Upgrades[a.Info.Name] = self.World.Actors.Where(other => other.Owner == self.Owner && other.Info.Name == a.Info.Name).Any();

			if (a.Owner == DisguisePlayer && DisguiseUpgrades.ContainsKey(a.Info.Name))
				DisguiseUpgrades[a.Info.Name] = self.World.Actors.Where(other => other.Owner == DisguisePlayer && other.Info.Name == a.Info.Name).Any();
		}

		void INotifyProduction.UnitProduced(Actor self, Actor other, CPos exit)
		{
			if (Info.LocalUpgrades.Length == 0)
				return;

			if (Info.LocalUpgrades.Contains(other.Info.Name) && Upgrades.ContainsKey(other.Info.Name))
				Upgrades[other.Info.Name] = true;
		}

		public bool IsValidArmament(string armament)
		{
			if (Info.Armaments != null)
				return Info.Armaments.Contains(armament);
			else
				return AttackBases.Any(ab => ab.Info.Armaments.Contains(armament));
		}

		public string CalculateArmor()
		{
			if (Shielded != null && !Shielded.IsTraitDisabled && Shielded.Strength > 0)
				return (Shielded.Strength / 100).ToString() + " / " + (Shielded.Info.MaxStrength / 100).ToString();

			var activeArmor = Armors.FirstOrDefault(a => !a.IsTraitDisabled);
			if (activeArmor == null)
				return TranslationProvider.GetString("label-armor-class.no-armor");

			return TranslationProvider.GetString("label-armor-class." + activeArmor?.Info.Type.Replace('.', '-'));
		}

		public string CalculateSight()
		{
			var revealsShroudValue = WDist.Zero;
			if (Info.Sight != null)
				revealsShroudValue = Info.Sight.Value;
			else if (RevealsShrouds.Any(rs => !rs.IsTraitDisabled))
			{
				var revealsShroudTrait = RevealsShrouds.Where(rs => !rs.IsTraitDisabled).MaxBy(rs => rs.Info.Range);
				if (revealsShroudTrait != null)
					revealsShroudValue = revealsShroudTrait.Info.Range;
			}

			foreach (var rsm in SightModifiers.Select(rsm => rsm.GetRevealsShroudModifier()))
				revealsShroudValue = revealsShroudValue * rsm / 100;

			return Math.Round((float)revealsShroudValue.Length / 1024, 2).ToString();
		}

		public string CalculateSpeed()
		{
			if (Mobile == null && Aircraft == null)
				return "0";

			var speedValue = Speed;
			foreach (var sm in SpeedModifiers.Select(sm => sm.GetSpeedModifier()))
				speedValue = speedValue * sm / 100;

			return speedValue.ToString();
		}

		public string CalculatePower()
		{
			var enabledPowers = Powers.Where(p => !p.IsTraitDisabled);
			if (enabledPowers.Count() == 0)
				return "0";

			var powerValue = enabledPowers.Sum(p => p.Info.Amount);
			foreach (var pm in PowerModifiers.Select(pm => pm.GetPowerModifier()))
				powerValue = powerValue * pm / 100;

			return powerValue.ToString();
		}

		public string CalculateMindControl()
		{
			if (MindController == null)
				return "0 / 0";

			return MindController.Slaves.Count().ToString() + " / " + MindController.Info.Capacity.ToString();
		}

		public string CalculateDamage()
		{
			var damageValue = 0;
			if (Info.Damage != null)
				damageValue = Info.Damage.Value;
			else
			{
				var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
				if (enabledArmaments.Any())
					damageValue = enabledArmaments.Sum(ar => ar.Info.Damage ?? 0);
			}

			foreach (var dm in FirepowerModifiers.Select(fm => fm.GetFirepowerModifier(null)))
				damageValue = damageValue * dm / 100;

			return damageValue.ToString();
		}

		public string CalculateSpread()
		{
			var spreadValue = WDist.Zero;
			if (Info.Spread != null)
				spreadValue = Info.Spread.Value;
			else
			{
				var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
				if (enabledArmaments.Any())
					spreadValue = enabledArmaments.Max(ar => ar.Info.Spread ?? WDist.Zero);
			}

			return Math.Round((float)spreadValue.Length / 1024, 2).ToString();
		}

		public string CalculateRoF()
		{
			var rofValue = 0;
			if (Info.ReloadDelay != null)
				rofValue = Info.ReloadDelay.Value;
			else
			{
				var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
				if (enabledArmaments.Any())
					rofValue = enabledArmaments.Min(ar => ar.Info.ReloadDelay ?? ar.Weapon.ReloadDelay);
			}

			foreach (var rm in ReloadModifiers.Select(sm => sm.GetReloadModifier(null)))
				rofValue = rofValue * rm / 100;

			return rofValue.ToString();
		}

		public string CalculateRange(int slot)
		{
			var minimumRangeValue = WDist.Zero;
			var shortRangeValue = WDist.Zero;
			var longRangeValue = WDist.Zero;

			if (Info.Range != null)
				longRangeValue = Info.Range.Value;
			else
			{
				var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
				if (enabledArmaments.Any())
				{
					shortRangeValue = enabledArmaments.Min(ar => ar.Info.Range ?? ar.Weapon.Range);
					longRangeValue = enabledArmaments.Max(ar => ar.Info.Range ?? ar.Weapon.Range);
				}
			}

			foreach (var rm in RangeModifiers.Select(rm => rm.GetRangeModifier()))
			{
				shortRangeValue = shortRangeValue * rm / 100;
				longRangeValue = longRangeValue * rm / 100;
			}

			if (Info.MinimumRange != null)
				minimumRangeValue = Info.MinimumRange.Value;
			else
			{
				var enabledArmaments = Armaments.Where(a => !a.IsTraitDisabled);
				if (enabledArmaments.Any())
					minimumRangeValue = enabledArmaments.Min(ar => ar.Info.MinimumRange ?? ar.Weapon.MinRange);
			}

			var text = "";
			if (CurrentStats[slot - 1] == ActorStatContent.MaxRange)
				text += Math.Round((float)longRangeValue.Length / 1024, 2).ToString();
			else if (CurrentStats[slot - 1] == ActorStatContent.MinRange)
				text += Math.Round((float)shortRangeValue.Length / 1024, 2).ToString();

			if (minimumRangeValue.Length > 100)
				text = Math.Round((float)minimumRangeValue.Length / 1024, 2).ToString() + "-" + text;

			return text;
		}

		public string CalculateHarvester()
		{
			if (Harvester == null)
				return "$0";

			var currentContents = Harvester.Contents.Values.Sum().ToString();
			var capacity = Harvester.Info.Capacity.ToString();

			var value = 0;
			foreach (var content in Harvester.Contents)
				value += playerResources.Info.ResourceValues[content.Key] * content.Value;

			return currentContents + " / " + capacity + " ($" + value.ToString() + ")";
		}

		public string CalculateCollector()
		{
			if (Collector == null)
				return "$0";

			var value = Collector.Amount();
			foreach (var dm in ResourceValueModifiers.Select(rvm => rvm.GetResourceValueModifier()))
				value = value * dm / 100;

			return "$" + value.ToString();
		}

		public string CalculateCashTrickler()
		{
			var enabledTricklers = CashTricklers.Where(ct => !ct.IsTraitDisabled);
			if (enabledTricklers.Count() == 0)
				return "00:00";

			var closestTrickler = enabledTricklers.MinBy(ct => ct.Ticks);
			return WidgetUtils.FormatTime(closestTrickler.Ticks, self.World.Timestep);
		}

		public string CalculatePeriodicProducer()
		{
			var enabledPProducers = PeriodicProducers.Where(pp => !pp.IsTraitDisabled);
			if (enabledPProducers.Count() == 0)
				return "00:00";

			var closestPProducer = enabledPProducers.MinBy(pp => pp.Ticks);
			return WidgetUtils.FormatTime(closestPProducer.Ticks, self.World.Timestep);
		}

		public string CalculateCargo()
		{
			if (Cargo != null)
				return Cargo.TotalWeight + " / " + Cargo.Info.MaxWeight;
			else if (SharedCargo != null)
				return SharedCargo.Manager.TotalWeight + " / " + SharedCargo.Manager.Info.MaxWeight;
			else if (Garrisonable != null)
				return Garrisonable.TotalWeight + " / " + Garrisonable.Info.MaxWeight;
			else
				return "0 / 0";
		}

		public string CalculateCarrier()
		{
			if (CarrierMaster == null)
				return "0 / 0 / 0";

			var slaves = CarrierMaster.SlaveEntries.Where(s => s.IsValid);
			return slaves.Where(x => !x.IsLaunched).Count().ToString() + " / " + slaves.Count().ToString() + " / " + CarrierMaster.Info.Actors.Length.ToString();
		}

		public string CalculateMobSpawner()
		{
			var total = 0;
			var spawned = 0;
			foreach (var mobSpawnerMaster in MobSpawnerMasters.Where(msm => !msm.IsTraitDisabled))
			{
				total += mobSpawnerMaster.Info.Actors.Length;
				spawned += mobSpawnerMaster.SlaveEntries.Where(s => s.IsValid).Count();
			}

			return spawned.ToString() + " / " + total.ToString();
		}

		public string GetIconFor(int slot)
		{
			if (CurrentStats.Length < slot || CurrentStats[slot - 1] == ActorStatContent.None)
				return null;
			else if (CurrentStats[slot - 1] == ActorStatContent.Armor)
			{
				if (Shielded != null && !Shielded.IsTraitDisabled && Shielded.Strength > 0)
					return "actor-stats-shield";

				return "actor-stats-armor";
			}
			else if (CurrentStats[slot - 1] == ActorStatContent.Sight)
				return "actor-stats-sight";
			else if (CurrentStats[slot - 1] == ActorStatContent.Speed)
				return "actor-stats-speed";
			else if (CurrentStats[slot - 1] == ActorStatContent.Power)
				return "actor-stats-power";
			else if (CurrentStats[slot - 1] == ActorStatContent.Damage)
				return "actor-stats-damage";
			else if (CurrentStats[slot - 1] == ActorStatContent.MindControl)
				return "actor-stats-mindcontrol";
			else if (CurrentStats[slot - 1] == ActorStatContent.ReloadDelay)
				return "actor-stats-rof";
			else if (CurrentStats[slot - 1] == ActorStatContent.Spread)
				return "actor-stats-spread";
			else if (CurrentStats[slot - 1] == ActorStatContent.MinRange)
				if (CurrentStats.Contains(ActorStatContent.MaxRange))
					return "actor-stats-shortrange";
				else
					return "actor-stats-range";
			else if (CurrentStats[slot - 1] == ActorStatContent.MaxRange)
				if (CurrentStats.Contains(ActorStatContent.MinRange))
					return "actor-stats-longrange";
				else
					return "actor-stats-range";
			else if (CurrentStats[slot - 1] == ActorStatContent.Harvester)
				return "actor-stats-resources";
			else if (CurrentStats[slot - 1] == ActorStatContent.Collector)
				return "actor-stats-resources";
			else if (CurrentStats[slot - 1] == ActorStatContent.CashTrickler)
				return "actor-stats-timer";
			else if (CurrentStats[slot - 1] == ActorStatContent.PeriodicProducer)
				return "actor-stats-timer";
			else if (CurrentStats[slot - 1] == ActorStatContent.Cargo)
				return "actor-stats-cargo";
			else if (CurrentStats[slot - 1] == ActorStatContent.Carrier)
				return "actor-stats-carrier";
			else if (CurrentStats[slot - 1] == ActorStatContent.Mob)
				return "actor-stats-mob";
			else
				return null;
		}

		public string GetValueFor(int slot)
		{
			if (CurrentStats.Length < slot || CurrentStats[slot - 1] == ActorStatContent.None)
				return null;
			else if (CurrentStats[slot - 1] == ActorStatContent.Armor)
				return CalculateArmor();
			else if (CurrentStats[slot - 1] == ActorStatContent.Sight)
				return CalculateSight();
			else if (CurrentStats[slot - 1] == ActorStatContent.Speed)
				return CalculateSpeed();
			else if (CurrentStats[slot - 1] == ActorStatContent.Power)
				return CalculatePower();
			else if (CurrentStats[slot - 1] == ActorStatContent.Damage)
				return CalculateDamage();
			else if (CurrentStats[slot - 1] == ActorStatContent.MindControl)
				return CalculateMindControl();
			else if (CurrentStats[slot - 1] == ActorStatContent.ReloadDelay)
				return CalculateRoF();
			else if (CurrentStats[slot - 1] == ActorStatContent.Spread)
				return CalculateSpread();
			else if (CurrentStats[slot - 1] == ActorStatContent.MinRange || CurrentStats[slot - 1] == ActorStatContent.MaxRange)
				return CalculateRange(slot);
			else if (CurrentStats[slot - 1] == ActorStatContent.Harvester)
				return CalculateHarvester();
			else if (CurrentStats[slot - 1] == ActorStatContent.Collector)
				return CalculateCollector();
			else if (CurrentStats[slot - 1] == ActorStatContent.CashTrickler)
				return CalculateCashTrickler();
			else if (CurrentStats[slot - 1] == ActorStatContent.PeriodicProducer)
				return CalculatePeriodicProducer();
			else if (CurrentStats[slot - 1] == ActorStatContent.Cargo)
				return CalculateCargo();
			else if (CurrentStats[slot - 1] == ActorStatContent.Carrier)
				return CalculateCarrier();
			else if (CurrentStats[slot - 1] == ActorStatContent.Mob)
				return CalculateMobSpawner();

			return "";
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			foreach (var upgrade in FactionUpgrades)
				Upgrades[upgrade] = self.World.Actors.Where(a => a.Owner == newOwner && a.Info.Name == upgrade).Any();
		}

		void INotifyDisguised.DisguiseChanged(Actor self, Actor target)
		{
			Disguised = self != target;

			if (Disguised)
			{
				var targetASV = target.TraitOrDefault<ActorStatValues>();
				if (targetASV != null)
				{
					Icon = targetASV.Icon;
					IconPalette = targetASV.IconPalette;
					IconPaletteIsPlayerPalette = targetASV.IconPaletteIsPlayerPalette;
					TooltipActor = targetASV.TooltipActor;

					DisguisePlayer = target.Owner;
					DisguiseImage = target.TraitOrDefault<RenderSprites>()?.GetImage(target);

					var health = targetASV.Health;
					if (health != null)
						DisguiseMaxHealth = health.MaxHP;

					for (int i = 1; i <= 8; i++)
					{
						DisguiseStatIcons[i] = targetASV.GetIconFor(i);
						DisguiseStats[i] = targetASV.GetValueFor(i);
					}

					DisguiseUpgrades = targetASV.Upgrades;
					DisguiseFactionUpgrades = targetASV.FactionUpgrades;
				}
				else
				{
					SetupCameos();
					DisguiseImage = null;
					DisguiseMaxHealth = 0;
					Disguised = false;
				}
			}
			else
			{
				SetupCameos();
				DisguiseImage = null;
				DisguiseMaxHealth = 0;
			}
		}
	}
}
