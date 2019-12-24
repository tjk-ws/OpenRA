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
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Handle infection by infector units.")]
	public class InfectableInfo : ConditionalTraitInfo, Requires<HealthInfo>
	{
		[Desc("Damage types that removes the infector.")]
		public readonly BitSet<DamageType> RemoveInfectorDamageTypes = default(BitSet<DamageType>);

		[Desc("Damage types that kills the infector.")]
		public readonly BitSet<DamageType> KillInfectorDamageTypes = default(BitSet<DamageType>);

		[GrantedConditionReference]
		[Desc("The condition to grant to self while infected by any actor.")]
		public readonly string InfectedCondition = null;

		[GrantedConditionReference]
		[Desc("Condition granted when being infected by another actor.")]
		public readonly string BeingInfectedCondition = null;

		[Desc("Conditions to grant when infected by specified actors.",
			"A dictionary of [actor id]: [condition].")]
		public readonly Dictionary<string, string> InfectedByConditions = new Dictionary<string, string>();

		[GrantedConditionReference]
		public IEnumerable<string> LinterConditions { get { return InfectedByConditions.Values; } }

		public override object Create(ActorInitializer init) { return new Infectable(init.Self, this); }
	}

	public class Infectable : ConditionalTrait<InfectableInfo>, ISync, ITick, INotifyCreated, INotifyDamage, INotifyKilled, IRemoveInfector
	{
		readonly Health health;

		public Tuple<Actor, AttackInfect, AttackInfectInfo> Infector;
		public int[] FirepowerMultipliers = new int[] { };

		[Sync]
		public int Ticks;

		ConditionManager conditionManager;
		int beingInfectedToken = ConditionManager.InvalidConditionToken;
		Actor enteringInfector;
		int infectedToken = ConditionManager.InvalidConditionToken;
		int infectedByToken = ConditionManager.InvalidConditionToken;

		int dealtDamage = 0;

		public Infectable(Actor self, InfectableInfo info)
			: base(info)
		{
			health = self.Trait<Health>();
		}

		protected override void Created(Actor self)
		{
			conditionManager = self.TraitOrDefault<ConditionManager>();

			base.Created(self);
		}

		public bool TryStartInfecting(Actor self, Actor infector)
		{
			if (infector != null)
			{
				if (enteringInfector == null)
				{
					enteringInfector = infector;

					if (conditionManager != null)
					{
						if (beingInfectedToken == ConditionManager.InvalidConditionToken && !string.IsNullOrEmpty(Info.BeingInfectedCondition))
							beingInfectedToken = conditionManager.GrantCondition(self, Info.BeingInfectedCondition);
					}

					return true;
				}
			}

			return false;
		}

		public void GrantCondition(Actor self)
		{
			if (conditionManager != null)
			{
				if (infectedToken == ConditionManager.InvalidConditionToken && !string.IsNullOrEmpty(Info.InfectedCondition))
					infectedToken = conditionManager.GrantCondition(self, Info.InfectedCondition);

				string infectedByCondition;
				if (Info.InfectedByConditions.TryGetValue(Infector.Item1.Info.Name, out infectedByCondition))
					infectedByToken = conditionManager.GrantCondition(self, infectedByCondition);
			}
		}

		public void RevokeCondition(Actor self, Actor infector = null)
		{
			if (conditionManager != null)
			{
				if (infector != null)
				{
					if (enteringInfector == infector)
					{
						enteringInfector = null;

						if (beingInfectedToken != ConditionManager.InvalidConditionToken)
							beingInfectedToken = conditionManager.RevokeCondition(self, beingInfectedToken);
					}
				}
				else
				{
					if (infectedToken != ConditionManager.InvalidConditionToken)
						infectedToken = conditionManager.RevokeCondition(self, infectedToken);

					if (infectedByToken != ConditionManager.InvalidConditionToken)
						infectedByToken = conditionManager.RevokeCondition(self, infectedByToken);
				}
			}
		}

		void RemoveInfector(Actor self, bool kill, AttackInfo e)
		{
			if (Infector != null && !Infector.Item1.IsDead)
			{
				Infector.Item1.TraitOrDefault<IPositionable>().SetPosition(Infector.Item1, self.CenterPosition);
				self.World.AddFrameEndTask(w =>
				{
					if (Infector == null || Infector.Item1.IsDead)
						return;

					w.Add(Infector.Item1);

					if (kill)
					{
						if (e != null)
							Infector.Item1.Kill(e.Attacker, e.Damage.DamageTypes);
						else
							Infector.Item1.Kill(self);
					}
					else
					{
						var mobile = Infector.Item1.TraitOrDefault<Mobile>();
						if (mobile != null)
						{
							mobile.Nudge(Infector.Item1);
						}
					}

					RevokeCondition(self);
					Infector = null;
					FirepowerMultipliers = new int[] { };
					dealtDamage = 0;
				});
			}
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (Infector != null)
			{
				if (e.Attacker != Infector.Item1)
				{
					if (Infector.Item3.SuppressionDamageThreshold > 0 && e.Damage.Value > Infector.Item3.SuppressionDamageThreshold)
					{
						RemoveInfector(self, true, e);
						return;
					}

					dealtDamage += e.Damage.Value;
				}

				if (e.Damage.DamageTypes.Overlaps(Info.KillInfectorDamageTypes))
					RemoveInfector(self, true, e);
				else if (e.Damage.DamageTypes.Overlaps(Info.RemoveInfectorDamageTypes))
					RemoveInfector(self, false, e);
			}
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
        {
            if (Infector != null)
            {
				var kill = Infector.Item3.SuppressionSumThreshold > 0 && dealtDamage > Infector.Item3.SuppressionSumThreshold;
				kill |= !Infector.Item3.SurviveHostDamageTypes.Overlaps(e.Damage.DamageTypes);
				RemoveInfector(self, kill, e);
            }
		}

		void ITick.Tick(Actor self)
		{
			if (!IsTraitDisabled && Infector != null)
			{
				if (--Ticks < 0)
				{
					var damage = Util.ApplyPercentageModifiers(Infector.Item3.Damage, FirepowerMultipliers);
					health.InflictDamage(self, Infector.Item1, new Damage(damage, Infector.Item3.DamageTypes), false);

					Ticks = Infector.Item3.DamageInterval;
				}
			}
		}

		void IRemoveInfector.RemoveInfector(Actor self, bool kill, AttackInfo e)
		{
			RemoveInfector(self, kill, e);
		}
	}
}
