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
	abstract class GroundStateBase : StateBase
	{
	}

	class GroundUnitsIdleState : GroundStateBase, IState
	{
		Actor leader;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (owner.SquadManager.UnitCannotBeOrdered(leader))
				leader = GetPathfindLeader(owner).Item1;

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
				Retreat(owner, flee: false, rearm: true, repair: true);
				return;
			}

			if ((AttackOrFleeFuzzy.Default.CanAttack(owner.Units.Select(u => u.Item1), enemyUnits)))
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsAttackMoveState(), false);
			else
				Retreat(owner, flee: true, rearm: true, repair: true);
		}

		public void Deactivate(Squad owner) { }
	}

	// This version AI forcus on solving pathfinding problem for AI
	// 1. use a leader to guide the entire squad to target, solve stuck on twisted road and saving performance on pathfinding
	// 2. have two methods to solve entire squad stuck. First, try make way for leader. Second, kick stuck units
	class GroundUnitsAttackMoveState : GroundStateBase, IState
	{
		const int MaxAttemptsToAdvance = 6;
		const int MakeWayTicks = 2;

		// failedAttempts: squad is considered to be stuck when it is reduced to 0
		// makeWay: the remaining tick for squad on make way behaviour
		// canMoveAfterMakeWay: to find if make way is enough for solve stuck problem, if not, will kick stuck unit when
		// stuckDistThreshold:
		int failedAttempts = -(MaxAttemptsToAdvance * 2); // Give tolerance for AI grouping team at start, so it is not zero
		int makeWay = MakeWayTicks;
		bool canMoveAfterMakeWay = true;
		long stuckDistThreshold;

		(Actor, WPos) leader = (null, WPos.Zero);
		WPos lastLeaderPos = WPos.Zero; // Record leader location at every bot tick, to find if leader/squad is stuck

		public void Activate(Squad owner)
		{
			stuckDistThreshold = 142179L * owner.SquadManager.Info.AttackForceInterval;
		}

		public void Tick(Squad owner)
		{
			// Basic check
			if (!owner.IsValid)
				return;

			// Initialize leader. Optimize pathfinding by using a leader with specific locomotor.
			// Drop former "owner.Units.ClosestTo(owner.TargetActor.CenterPosition)",
			// which is the shortest geometric distance, but it has no relation to pathfinding distance in map.
			if (owner.SquadManager.UnitCannotBeOrdered(leader.Item1))
				leader = GetPathfindLeader(owner);

			if (!owner.IsTargetValid)
			{
				var targetActor = owner.SquadManager.FindClosestEnemy(leader.Item1);
				if (targetActor != null)
					owner.TargetActor = targetActor;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsFleeState(), false);
					return;
				}
			}

			// Switch to "GroundUnitsAttackState" if we encounter enemy units.
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);

			var enemyActor = owner.SquadManager.FindClosestEnemy(leader.Item1, attackScanRadius);
			if (enemyActor != null)
			{
				owner.TargetActor = enemyActor;
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsAttackState(), false);
				return;
			}

			// Since units have different movement speeds, they get separated while approaching the target.
			// Let them regroup into tighter formation towards "leader".
			//
			// "occupiedArea" means the space the squad units will occupy (if 1 per Cell).
			// leader only stop when scope of "lookAround" is not covered all units;
			// units in "unitsHurryUp"  will catch up, which keep the team tight while not stuck.
			//
			// Imagining "occupiedArea" takes up a a place shape like square,
			// we need to draw a circle to cover the the enitire circle.
			var occupiedArea = (long)WDist.FromCells(owner.Units.Count).Length * 1024;

			// Solve squad stuck by two method: if canMoveAfterMakeWay is true, try make way for leader,
			// otherwise try kick units in squad that cannot move at all.
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

					// If not, check and record all units position
					else
					{
						for (var i = 0; i < owner.Units.Count; i++)
						{
							var u = owner.Units[i];
							var dist = (u.Item1.CenterPosition - leader.Item1.CenterPosition).HorizontalLengthSquared;
							if ((u.Item1.CenterPosition - u.Item2).HorizontalLengthSquared < stuckDistThreshold // Check if unit cannot move
								&& dist > (u.Item2 - leader.Item2).HorizontalLengthSquared // Check if unit are further from leader as before
								&& dist > 5 * occupiedArea // Ckeck if unit in valid distance from leader
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
						// Give some tolerance for AI regrouping when make way
						failedAttempts = 0 - MakeWayTicks;

						// Change target that may cause the stuck
						owner.TargetActor = owner.SquadManager.FindClosestEnemy(leader.Item1);
						canMoveAfterMakeWay = false;
						owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Item1.Location), true, groupedActors: others.ToArray()));
					}

					makeWay--;
				}

				return;
			}

			// Stuck check: by using "failedAttempts" to get if the leader is waiting for squad too long .
			// When just after a stuck-solving process, only record position and skip the stuck check.
			if (makeWay > 0)
			{
				if ((leader.Item1.CenterPosition - lastLeaderPos).HorizontalLengthSquared < stuckDistThreshold / 2) // Becuase compared to kick leader check, lastLeaderPos record every ticks so we reduce the threshold
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

			// "Leader" will check how many squad members are around
			// to decide if it needs to continue.
			//
			// Units that need hurry up ("unitsHurryUp") will try catch up before Leader waiting,
			// which can make squad members follows relatively tight without stucking "Leader".
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

	class GroundUnitsAttackState : GroundStateBase, IState
	{
		int tryAttackTick;

		Actor leader;
		Actor formerTarget;
		int tryAttack = 0;

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
				leader = owner.Units.First().Item1;

			// Rescan target to prevent being ambushed and die without fight
			// If there is no threat around, return to AttackMove state for formation
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);
			var closestEnemy = owner.SquadManager.FindClosestEnemy(leader, attackScanRadius);

			var cannotRetaliate = true;
			var followingUnits = new List<Actor>();
			var attackingUnits = new List<Actor>();

			// Becuase MoveWithinRange can cause huge lag when stuck
			// we only allow free attack behaivour within TryAttackTick
			// then the squad will gather to a certain leader
			if (closestEnemy == null)
			{
				owner.TargetActor = owner.SquadManager.FindClosestEnemy(leader);
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsAttackMoveState(), false);
				return;
			}

			foreach (var u in owner.Units)
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

			// Because ShouldFlee(owner) cannot retreat units while they cannot even fight
			// a unit that they cannot target. Therefore, use `cannotRetaliate` here to solve this bug.
			if (ShouldFleeSimple(owner) || cannotRetaliate)
			{
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsFleeState(), false);
				return;
			}

			// Refresh tryAttack when target switched
			tryAttack++;
			if (formerTarget != owner.TargetActor)
			{
				tryAttack = 0;
				formerTarget = owner.TargetActor;
			}

			owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Location), false, groupedActors: followingUnits.ToArray()));
			owner.Bot.QueueOrder(new Order("Attack", null, Target.FromActor(owner.TargetActor), false, groupedActors: attackingUnits.ToArray()));
		}

		public void Deactivate(Squad owner) { }
	}

	class GroundUnitsFleeState : GroundStateBase, IState
	{
		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			Retreat(owner, flee: true, rearm: true, repair: true);
			owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsIdleState(), false);
		}

		public void Deactivate(Squad owner) { owner.SquadManager.DismissSquad(owner); }
	}
}
