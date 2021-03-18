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
		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var closestEnemy = FindClosestEnemy(owner);
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

			if (AttackOrFleeFuzzy.Default.CanAttack(owner.Units, enemyUnits))
			{
				// We have gathered sufficient units. Attack the nearest enemy unit.
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsAttackMoveState(), false);
			}
			else
				Retreat(owner, true, true, true);
		}

		public void Deactivate(Squad owner) { }
	}

	class GuerrillaUnitsAttackMoveState : GuerrillaStatesBase, IState
	{
		const int MaxAttemptsToAdvance = 6;
		const int MakeWayTicks = 2;

		// Give tolerance for AI grouping team at start
		int failedAttempts = -(MaxAttemptsToAdvance * 2);
		int makeWay = MakeWayTicks;
		WPos lastPos = WPos.Zero;

		// Optimazing state switch
		internal bool AttackLoop = false;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			// Basic check
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var targetActor = GetRandomPreferredTarget(owner);
				if (targetActor != null)
					owner.TargetActor = targetActor;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsFleeState(), false);
					return;
				}
			}

			// Initialize leader. Optimize pathfinding by using leader.
			// Drop former "owner.Units.ClosestTo(owner.TargetActor.CenterPosition)",
			// which is the shortest geometric distance, but it has no relation to pathfinding distance in map.
			var leader = owner.Units.FirstOrDefault();
			if (leader == null)
				return;

			// Switch to "GroundUnitsAttackState" if we encounter enemy units.
			// This makes sure that the entire squad will stay together and fight, instead of splitting
			// into two groups based on whether they were close enough for AttackMove to acquire the target.
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);

			var enemyActor = owner.SquadManager.FindClosestEnemy(leader.CenterPosition, attackScanRadius);
			if (enemyActor != null)
			{
				owner.TargetActor = enemyActor;
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsHitState(), false);
				return;
			}

			// Make sure the guide unit has not been blocked by the rest of the squad
			if (failedAttempts >= MaxAttemptsToAdvance)
			{
				if (makeWay > 0)
				{
					owner.Bot.QueueOrder(new Order("AttackMove", leader, Target.FromCell(owner.World, owner.TargetActor.Location), false));

					var others = owner.Units.Where(u => u != leader);
					owner.Bot.QueueOrder(new Order("Scatter", null, false, groupedActors: others.ToArray()));

					makeWay--;
				}
				else
				{
					// Give some tolerance for AI regrouping
					failedAttempts = 0 - MakeWayTicks;
					makeWay = MakeWayTicks;
				}

				return;
			}

			// Check if the squad is stuck due to the map having a very twisted path
			// or currently bridge and tunnel from TS mod
			if (leader.CenterPosition == lastPos)
				failedAttempts++;
			else
				failedAttempts = 0;

			lastPos = leader.CenterPosition;

			// Since units have different movement speeds, they get separated while approaching the target.
			// Let them regroup into tighter formation towards "leader".
			//
			// "occupiedArea" means the space the squad units will occupy (if 1 per Cell).
			// leader only stop when scope of "lookAround" is not covered all units;
			// units in "unitsHurryUp"  will catch up, which keep the team tight while not stuck.
			//
			// Imagining "occupiedArea" takes up a a place shape like square,
			// we need to draw a circle to cover the the enitire circle.
			//
			// "Leader" will check how many squad members are around
			// to decide if it needs to continue.
			//
			// However in practice because of the poor PF, squad tend to PF to a ellipse.
			// "Leader" will have to check a circle with 5 x "occupiedArea" mentioned before
			// to get the best regrouping gameplay, which is the "leaderWaitCheck".
			//
			// Units that need hurry up ("unitsHurryUp") will try catch up before Leader waiting,
			// which can make squad members follows relatively tight without stucking "Leader".
			var occupiedArea = (long)WDist.FromCells(owner.Units.Count).Length * 1024;

			var unitsHurryUp = owner.Units.Where(a => (a.CenterPosition - leader.CenterPosition).LengthSquared >= occupiedArea * 2);
			var leaderWaitCheck = owner.Units.Any(a => (a.CenterPosition - leader.CenterPosition).LengthSquared > occupiedArea * 5);

			if (leaderWaitCheck)
				owner.Bot.QueueOrder(new Order("Stop", leader, false));
			else
				owner.Bot.QueueOrder(new Order("AttackMove", leader, Target.FromCell(owner.World, owner.TargetActor.Location), false));

			owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Location), false, groupedActors: unitsHurryUp.ToArray()));
		}

		public void Deactivate(Squad owner) { }
	}

	class GuerrillaUnitsHitState : GuerrillaStatesBase, IState
	{
		public const int BackOffTicks = 1;
		internal int BackOff = BackOffTicks;

		internal bool GetIntoAttackLoop = false;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (BackOff-- <= 0)
			{
				BackOff = BackOffTicks;
				if (!GetIntoAttackLoop)
				{
					GetIntoAttackLoop = true;
					owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsRunState(), true);
				}
				else
					owner.FuzzyStateMachine.RevertToPreviousState(owner, true);
			}

			// Basic check
			if (!owner.IsValid)
				return;

			var leader = owner.Units.FirstOrDefault();
			if (leader == null)
				return;

			// Rescan target to prevent being ambushed and die without fight
			// If there is no threat around, return to AttackMove state for formation
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);
			var targetActor = owner.SquadManager.FindClosestEnemy(leader.CenterPosition, attackScanRadius);

			var cannotRetaliate = true;
			List<Actor> followingUnits = new List<Actor>();
			List<Actor> attackingUnits = new List<Actor>();
			if (targetActor == null)
			{
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsAttackMoveState(), false);
				return;
			}
			else
			{
				owner.TargetActor = targetActor;

				foreach (var a in owner.Units)
				{
					if (!BusyAttack(a))
					{
						if (CanAttackTarget(a, targetActor))
						{
							attackingUnits.Add(a);
							cannotRetaliate = false;
						}
						else
							followingUnits.Add(a);
					}
					else
						cannotRetaliate = false;
				}
			}

			// Because ShouldFlee(owner) cannot retreat units while they cannot even fight
			// a unit that they cannot target. Therefore, use `cannotRetaliate` here to solve this bug.
			if (cannotRetaliate)
				owner.FuzzyStateMachine.ChangeState(owner, new GuerrillaUnitsFleeState(), true);

			owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Location), false, groupedActors: followingUnits.ToArray()));
			owner.Bot.QueueOrder(new Order("Attack", null, Target.FromActor(owner.TargetActor), false, groupedActors: attackingUnits.ToArray()));
		}

		public void Deactivate(Squad owner) { }
	}

	class GuerrillaUnitsRunState : GuerrillaStatesBase, IState
	{
		public const int HitTicks = 2;
		internal int Hit = HitTicks;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (Hit-- <= 0)
			{
				Hit = HitTicks;
				owner.FuzzyStateMachine.RevertToPreviousState(owner, true);
				return;
			}

			Retreat(owner, true, true, true);
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
