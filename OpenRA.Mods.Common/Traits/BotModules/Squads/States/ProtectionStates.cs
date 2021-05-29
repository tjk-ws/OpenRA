#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.BotModules.Squads
{
	abstract class ProtectionStateBase : GroundStateBase
	{
	}

	class UnitsForProtectionIdleState : ProtectionStateBase, IState
	{
		public void Activate(Squad owner) { }
		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				Retreat(owner, flee: false, rearm: true, repair: true);
				return;
			}
			else
				owner.FuzzyStateMachine.ChangeState(owner, new UnitsForProtectionAttackState(), false);
		}

		public void Deactivate(Squad owner) { }
	}

	class UnitsForProtectionAttackState : ProtectionStateBase, IState
	{
		public const int BackoffTicks = 4;
		int tryAttackTick;

		internal int Backoff = BackoffTicks;
		Actor formerTarget;
		int tryAttack = 0;

		public void Activate(Squad owner)
		{
			tryAttackTick = owner.SquadManager.Info.ProtectionScanRadius;
		}

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			var leader = owner.Units.FirstOrDefault().Item1;

			if (!owner.IsTargetValid)
			{
				owner.TargetActor = owner.SquadManager.FindClosestEnemy(leader, WDist.FromCells(owner.SquadManager.Info.ProtectionScanRadius));

				if (owner.TargetActor == null)
				{
					owner.FuzzyStateMachine.ChangeState(owner, new UnitsForProtectionFleeState(), false);
					return;
				}
			}

			// rescan target to prevent being ambushed and die without fight
			// return to AttackMove state for formation
			var protectionScanRadius = WDist.FromCells(owner.SquadManager.Info.ProtectionScanRadius);
			var targetActor = owner.SquadManager.FindClosestEnemy(leader, protectionScanRadius);
			var cannotRetaliate = false;
			var resupplyingUnits = new List<Actor>();
			var followingUnits = new List<Actor>();
			var attackingUnits = new List<Actor>();

			if (targetActor != null)
				owner.TargetActor = targetActor;

			if (!owner.IsTargetVisible)
			{
				if (Backoff < 0)
				{
					owner.FuzzyStateMachine.ChangeState(owner, new UnitsForProtectionFleeState(), false);
					Backoff = BackoffTicks;
					return;
				}

				Backoff--;
			}
			else
			{
				cannotRetaliate = true;

				for (var i = 0; i < owner.Units.Count; i++)
				{
					var u = owner.Units[i];

					// Air units control:
					var ammoPools = u.Item1.TraitsImplementing<AmmoPool>().ToArray();
					if (u.Item1.Info.HasTraitInfo<AircraftInfo>() && ammoPools.Any())
					{
						if (IsAttackingAndTryAttack(u.Item1).Item2)
						{
							cannotRetaliate = false;
							continue;
						}

						if (!ReloadsAutomatically(ammoPools, u.Item1.TraitOrDefault<Rearmable>()))
						{
							if (IsRearming(u.Item1))
								continue;

							if (!HasAmmo(ammoPools))
							{
								resupplyingUnits.Add(u.Item1);
								continue;
							}
						}

						if (CanAttackTarget(u.Item1, owner.TargetActor))
						{
							attackingUnits.Add(u.Item1);
							cannotRetaliate = false;
						}
						else
							followingUnits.Add(u.Item1);
					}

					// Ground/naval units control:
					// Becuase MoveWithinRange can cause huge lag when stuck
					// we only allow free attack behaivour within TryAttackTick
					// then the squad will gather to a certain leader
					else
					{
						var attackCondition = IsAttackingAndTryAttack(u.Item1);

						if (attackCondition.Item2 &&
							(u.Item1.CenterPosition - owner.TargetActor.CenterPosition).HorizontalLengthSquared <
							(leader.CenterPosition - owner.TargetActor.CenterPosition).HorizontalLengthSquared)
							leader = u.Item1;

						if (attackCondition.Item1)
							cannotRetaliate = false;
						else if (CanAttackTarget(u.Item1, owner.TargetActor))
						{
							if (tryAttack > tryAttackTick && attackCondition.Item2)
							{
								followingUnits.Add(u.Item1);
								continue;
							}

							attackingUnits.Add(u.Item1);
							cannotRetaliate = false;
						}
						else
							followingUnits.Add(u.Item1);
					}
				}
			}

			if (cannotRetaliate)
			{
				owner.FuzzyStateMachine.ChangeState(owner, new UnitsForProtectionFleeState(), false);
				return;
			}

			tryAttack++;
			if (formerTarget != owner.TargetActor)
			{
				tryAttack = 0;
				formerTarget = owner.TargetActor;
			}

			owner.Bot.QueueOrder(new Order("ReturnToBase", null, false, groupedActors: resupplyingUnits.ToArray()));
			owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Location), false, groupedActors: followingUnits.ToArray()));
			owner.Bot.QueueOrder(new Order("Attack", null, Target.FromActor(owner.TargetActor), false, groupedActors: attackingUnits.ToArray()));
		}

		public void Deactivate(Squad owner) { }
	}

	class UnitsForProtectionFleeState : ProtectionStateBase, IState
	{
		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			owner.TargetActor = null;

			if (!owner.IsValid)
				return;

			Retreat(owner, flee: true, rearm: true, repair: true);
			owner.FuzzyStateMachine.ChangeState(owner, new UnitsForProtectionIdleState(), false);
		}

		public void Deactivate(Squad owner) { }
	}
}
