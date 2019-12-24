#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Activities;
using OpenRA.Mods.AS.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Move onto the target then execute the attack.")]
	public class AttackInfectInfo : AttackFrontalInfo, Requires<MobileInfo>
	{
		[Desc("Range of the final joust of the infector.")]
		public readonly WDist JoustRange = WDist.Zero;

		[Desc("Conditions that last from start of the joust until the attack.")]
		[GrantedConditionReference]
		public readonly string JoustCondition = "jousting";

		[FieldLoader.Require]
		[Desc("How much damage to deal.")]
		public readonly int Damage;

		[FieldLoader.Require]
		[Desc("How often to deal the damage.")]
		public readonly int DamageInterval;

		[Desc("Damage types for the infection damage.")]
		public readonly BitSet<DamageType> DamageTypes = default(BitSet<DamageType>);

		[Desc("If an external actor delivers more damage than this value, the infector is killed immediately.",
			"Use -1 to never kill the infector.")]
		public readonly int SuppressionDamageThreshold = -1;

		[Desc("If the infected actor receives more damage from external sources than this value, the infector dies along with the infected.",
			"Use -1 to never kill the infector.")]
		public readonly int SuppressionSumThreshold = -1;

		[Desc("Damage types which allows the infector survive when it's host dies.")]
		public readonly BitSet<DamageType> SurviveHostDamageTypes = default(BitSet<DamageType>);

		public override object Create(ActorInitializer init) { return new AttackInfect(init.Self, this); }
	}

	public class AttackInfect : AttackFrontal
	{
		readonly AttackInfectInfo info;

		ConditionManager conditionManager;
		int joustToken = ConditionManager.InvalidConditionToken;

		public AttackInfect(Actor self, AttackInfectInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		protected override void Created(Actor self)
		{
			conditionManager = self.TraitOrDefault<ConditionManager>();
			base.Created(self);
		}

		protected override bool CanAttack(Actor self, Target target)
		{
			if (target.Type != TargetType.Actor)
				return false;

			if (self.Location == target.Actor.Location && HasAnyValidWeapons(target))
				return true;

			return base.CanAttack(self, target);
		}

		public void GrantJoustCondition(Actor self)
		{
			if (conditionManager != null && !string.IsNullOrEmpty(info.JoustCondition))
				joustToken = conditionManager.GrantCondition(self, info.JoustCondition);
		}

		public void RevokeJoustCondition(Actor self)
		{
			if (joustToken != ConditionManager.InvalidConditionToken)
				joustToken = conditionManager.RevokeCondition(self, joustToken);
		}

		public override Activity GetAttackActivity(Actor self, Target newTarget, bool allowMove, bool forceAttack, Color? targetLineColor)
		{
			return new Infect(self, newTarget, this, info, targetLineColor);
		}
	}
}
