﻿#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Traits;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Attach this to actors which should regenerate or lose health points over time.")]
	sealed class ChangesShieldInfo : ConditionalTraitInfo, Requires<ShieldedInfo>
	{
		[Desc("Absolute amount of health points added in each step.",
			"Use negative values to apply damage.")]
		public readonly int Step = 5;

		[Desc("Relative percentages of health added in each step.",
			"Use negative values to apply damage.",
			"When both values are defined, their summary will be applied.")]
		public readonly int PercentageStep = 0;

		[Desc("Time in ticks to wait between each health modification.")]
		public readonly int Delay = 5;

		[Desc("Heal if current health is below this percentage of full health.")]
		public readonly int StartIfBelow = 50;

		[Desc("Time in ticks to wait after taking damage.")]
		public readonly int DamageCooldown = 0;

		public override object Create(ActorInitializer init) { return new ChangesShield(init.Self, this); }
	}

	sealed class ChangesShield : ConditionalTrait<ChangesShieldInfo>, ITick, INotifyDamage, ISync
	{
		readonly Shielded shield;

		[Sync]
		int ticks;

		[Sync]
		int damageTicks;

		public ChangesShield(Actor self, ChangesShieldInfo info)
			: base(info)
		{
			shield = self.Trait<Shielded>();
		}

		void ITick.Tick(Actor self)
		{
			if (self.IsDead || IsTraitDisabled)
				return;

			// Cast to long to avoid overflow when multiplying by the health
			if (shield.Strength >= Info.StartIfBelow * (long)shield.MaxStrength / 100)
				return;

			if (damageTicks > 0)
			{
				--damageTicks;
				return;
			}

			if (--ticks <= 0)
			{
				ticks = Info.Delay;

				// Cast to long to avoid overflow when multiplying by the health
				shield.Regenerate(self, Info.Step + Info.PercentageStep * shield.MaxStrength / 100);
			}
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (e.Damage.Value > 0)
				damageTicks = Info.DamageCooldown;
		}
	}
}
