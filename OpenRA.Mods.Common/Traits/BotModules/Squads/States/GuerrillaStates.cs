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
	abstract class GuerrillaStatesBase : GroundStateBase
	{
	}

	class GuerrillaUnitsIdleState : GuerrillaStatesBase, IState
	{
		Actor leader;
		int squadsize;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (owner.SquadManager.UnitCannotBeOrdered(leader) || squadsize != owner.Units.Count)
			{
				leader = GetPathfindLeader(owner).Item1;
				squadsize = owner.Units.Count;
			}

			if (!owner.IsTargetValid)
			{
				var closestEnemy = owner.SquadManager.FindClosestEnemy(leader);
				if (closestEnemy == null)
					return;

				owner.TargetActor = closestEnemy;
			}

			var enemyUnits = owner.World.FindActorsInCircle(owner.TargetActor.CenterPosition, WDist.FromCells(owner.SquadManager.Info.IdleScanRadius))
				.Where(owner.SquadManager.IsPreferredEnemyUnit).ToList();

			if (enemyUnits.Count == 0)
			{
				Retreat(owner, false, true, true);
				return;
			}

			if ((AttackOrFleeFuzzy.Default.CanAttack(owner.Units.Select(u => u.Item1), enemyUnits)))
			{
				// We have gathered sufficient units. Attack the nearest enemy unit.
				owner.BaseLocation = RandomBuildingLocation(owner);
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsAttackMoveState(), false);
			}
			else
				Retreat(owner, true, true, true);
		}

		public void Deactivate(Squad owner) { }
	}

	// See detailed comments at GroundStates.cs
	// There is many in common
	class GuerrillaUnitsAttackMoveState : GuerrillaStatesBase, IState
	{
		const int MaxAttemptsToAdvance = 6;
		const int MakeWayTicks = 2;

		// Give tolerance for AI grouping team at start
		int failedAttempts = -(MaxAttemptsToAdvance * 2);
		int makeWay = MakeWayTicks;
		bool canMoveAfterMakeWay = true;
		long stuckDistThreshold;

		(Actor, WPos) leader = (null, WPos.Zero);
		WPos lastLeaderPos = WPos.Zero;
		int squadsize = 0;

		public void Activate(Squad owner)
		{
			stuckDistThreshold = 142179L * owner.SquadManager.Info.AttackForceInterval;
		}

		public void Tick(Squad owner)
		{
			// Basic check
			if (!owner.IsValid)
				return;

			// Initialize leader. Optimize pathfinding by using leader.
			// Drop former "owner.Units.ClosestTo(owner.TargetActor.CenterPosition)",
			// which is the shortest geometric distance, but it has no relation to pathfinding distance in map.
			if (owner.SquadManager.UnitCannotBeOrdered(leader.Item1) || squadsize != owner.Units.Count)
			{
				leader = GetPathfindLeader(owner);
				squadsize = owner.Units.Count;
			}

			if (!owner.IsTargetValid)
			{
				var targetActor = owner.SquadManager.FindClosestEnemy(leader.Item1);
				if (targetActor != null)
					owner.TargetActor = targetActor;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsFleeState(), false);
					return;
				}
			}

			// Switch to attack state if we encounter enemy units like ground squad
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);

			var enemyActor = owner.SquadManager.FindClosestEnemy(leader.Item1, attackScanRadius);
			if (enemyActor != null)
			{
				owner.TargetActor = enemyActor;
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsHitState(), false);
				return;
			}

			// Solve squad stuck by two method: if canMoveAfterMakeWay is true, use regular method,
			// otherwise try kick units in squad that cannot move at all.
			var occupiedArea = (long)WDist.FromCells(owner.Units.Count).Length * 1024;
			if (failedAttempts >= MaxAttemptsToAdvance)
			{
				// Kick stuck units: Kick stuck units that cannot move at all
				if (!canMoveAfterMakeWay)
				{
					var stopUnits = new List<Actor>();

					// Check if it is the leader stuck
					if ((leader.Item1.CenterPosition - leader.Item2).HorizontalLengthSquared < stuckDistThreshold && !IsAttackingAndTryAttack(leader.Item1).Item1)
					{
						stopUnits.Add(leader.Item1);
						owner.Units.Remove(leader);
					}

					// Check if it is the units stuck
					else
					{
						for (var i = 0; i < owner.Units.Count; i++)
						{
							var u = owner.Units[i];
							var dist = (u.Item1.CenterPosition - leader.Item1.CenterPosition).HorizontalLengthSquared;
							if ((u.Item1.CenterPosition - u.Item2).HorizontalLengthSquared < stuckDistThreshold
								&& dist > (u.Item2 - leader.Item2).HorizontalLengthSquared
								&& dist > 5 * occupiedArea
								&& !IsAttackingAndTryAttack(u.Item1).Item1)
							{
								stopUnits.Add(u.Item1);
								owner.Units.RemoveAt(i);
							}
							else
								owner.Units[i] = (u.Item1, u.Item1.CenterPosition);
						}
					}

					if (owner.Units.Count == 0)
						return;
					failedAttempts = MaxAttemptsToAdvance - 2;
					leader = GetPathfindLeader(owner);
					owner.Bot.QueueOrder(new Order("AttackMove", leader.Item1, Target.FromCell(owner.World, owner.TargetActor.Location), false));
					owner.Bot.QueueOrder(new Order("Stop", null, false, groupedActors: stopUnits.ToArray()));
					makeWay = 0;
				}

				// Make way for leader: Make sure the guide unit has not been blocked by the rest of the squad.
				// If canMoveAfterMakeWay is not reset to true after this, will try kick unit
				if (makeWay > 0)
				{
					owner.Bot.QueueOrder(new Order("AttackMove", leader.Item1, Target.FromCell(owner.World, owner.TargetActor.Location), false));

					var others = owner.Units.Where(u => u.Item1 != leader.Item1).Select(u => u.Item1);
					owner.Bot.QueueOrder(new Order("Scatter", null, false, groupedActors: others.ToArray()));
					if (makeWay == 1)
					{
						// Give some tolerance for AI regrouping when stuck at first time
						failedAttempts = 0 - MakeWayTicks;

						// Change target that may cause the stuck, which also makes Guerrilla Squad unpredictable
						owner.TargetActor = owner.SquadManager.FindClosestEnemy(leader.Item1);
						makeWay = MakeWayTicks;
						canMoveAfterMakeWay = false;
						owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Item1.Location), true, groupedActors: others.ToArray()));
					}

					makeWay--;
				}

				return;
			}

			// Check if the leader is waiting for squad too long. Skips when just after a stuck-solving process.
			if (makeWay > 0)
			{
				if ((leader.Item1.CenterPosition - lastLeaderPos).HorizontalLengthSquared < stuckDistThreshold / 2) // Becuase compared to kick leader check, lastLeaderPos every squad ticks so we reduce the threshold
					failedAttempts++;
				else
				{
					failedAttempts = 0;
					canMoveAfterMakeWay = true;
					lastLeaderPos = leader.Item1.CenterPosition;
				}
			}
			else
			{
				makeWay = MakeWayTicks;
				lastLeaderPos = leader.Item1.CenterPosition;
			}

			// The same as ground squad regroup
			var leaderWaitCheck = owner.Units.Any(u => (u.Item1.CenterPosition - leader.Item1.CenterPosition).HorizontalLengthSquared > occupiedArea * 5);

			if (leaderWaitCheck)
				owner.Bot.QueueOrder(new Order("Stop", leader.Item1, false));
			else
				owner.Bot.QueueOrder(new Order("AttackMove", leader.Item1, Target.FromCell(owner.World, owner.TargetActor.Location), false));

			var unitsHurryUp = owner.Units.Where(u => (u.Item1.CenterPosition - leader.Item1.CenterPosition).HorizontalLengthSquared >= occupiedArea * 2).Select(u => u.Item1).ToArray();
			owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Item1.Location), false, groupedActors: unitsHurryUp));
		}

		public void Deactivate(Squad owner) { }
	}

	class GuerrillaUnitsHitState : GuerrillaStatesBase, IState
	{
		int tryAttackTick;

		Actor leader;
		Actor formerTarget;
		int tryAttack = 0;
		bool isFirstTick = true; // Only record HP and do not retreat at first tick
		int squadsize = 0;

		public void Activate(Squad owner)
		{
			tryAttackTick = owner.SquadManager.Info.AttackScanRadius;
		}

		public void Tick(Squad owner)
		{
			// Basic check
			if (!owner.IsValid)
				return;

			if (owner.SquadManager.UnitCannotBeOrdered(leader))
				leader = owner.Units.FirstOrDefault().Item1;

			// Rescan target to prevent being ambushed and die without fight
			// If there is no threat around, return to AttackMove state for formation
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);
			var closestEnemy = owner.SquadManager.FindClosestEnemy(leader, attackScanRadius);

			var healthChange = false;
			var cannotRetaliate = true;
			var followingUnits = new List<Actor>();
			var attackingUnits = new List<Actor>();

			if (closestEnemy == null)
			{
				owner.TargetActor = owner.SquadManager.FindClosestEnemy(leader);
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsAttackMoveState(), false);
				return;
			}
			else
			{
				owner.TargetActor = closestEnemy;
				for (var i = 0; i < owner.Units.Count; i++)
				{
					var u = owner.Units[i];
					var attackCondition = IsAttackingAndTryAttack(u.Item1);

					var health = u.Item1.TraitOrDefault<IHealth>();

					if (health != null)
					{
						var healthWPos = new WPos(0, 0, (int)health.DamageState); // HACK: use WPos.Z storage HP
						if (u.Item2.Z != healthWPos.Z)
						{
							if (u.Item2.Z < healthWPos.Z)
								healthChange = true;
							owner.Units[i] = (u.Item1, healthWPos);
						}
					}

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

			// Because ShouldFlee(owner) cannot retreat units while they cannot even fight
			// a unit that they cannot target. Therefore, use `cannotRetaliate` here to solve this bug.
			if (cannotRetaliate)
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsFleeState(), true);

			tryAttack++;
			if (formerTarget != owner.TargetActor)
			{
				tryAttack = 0;
				formerTarget = owner.TargetActor;
			}

			var unitlost = squadsize > owner.Units.Count;
			squadsize = owner.Units.Count;

			if ((healthChange || unitlost) && !isFirstTick)
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsRunState(), true);

			owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Location), false, groupedActors: followingUnits.ToArray()));
			owner.Bot.QueueOrder(new Order("Attack", null, Target.FromActor(owner.TargetActor), false, groupedActors: attackingUnits.ToArray()));

			isFirstTick = false;
		}

		public void Deactivate(Squad owner) { }
	}

	class GuerrillaUnitsRunState : GuerrillaStatesBase, IState
	{
		public const int HitTicks = 2;
		internal int Hit = HitTicks;
		bool ordered;

		public void Activate(Squad owner) { ordered = false; }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (Hit-- <= 0)
			{
				Hit = HitTicks;
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsHitState(), true);
				return;
			}

			if (!ordered)
			{
				owner.Bot.QueueOrder(new Order("Move", null, Target.FromCell(owner.World, owner.BaseLocation), false, groupedActors: owner.Units.Select(u => u.Item1).ToArray()));
				ordered = true;
			}
		}

		public void Deactivate(Squad owner) { }
	}

	class GuerrillaUnitsFleeState : GuerrillaStatesBase, IState
	{
		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			Retreat(owner, true, true, true);
			owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsIdleState(), false);
		}

		public void Deactivate(Squad owner) { }
	}
}
