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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Applies continuous changes to a named PhysicalState of actors within range.")]
	public class PhysicalStateAuraInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Name of the PhysicalState to modify on nearby actors.")]
		public readonly string PhysicalStateName = null;

		[Desc("Amount to add per tick to the PhysicalState. Can be negative to subtract.")]
		public readonly int AmountPerTick = 1;

		[Desc("The range in which to affect actors.")]
		public readonly WDist Range = WDist.FromCells(3);

		[Desc("The maximum vertical range above terrain to affect actors.",
		"Ignored if 0 (actors are affected regardless of vertical distance).")]
		public readonly WDist MaximumVerticalOffset = WDist.Zero;

		[Desc("What player relationships are affected.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Enemy;

		[Desc("Whether to affect the actor that has this aura.")]
		public readonly bool AffectsParent = false;

		[Desc("Tick interval for applying the effect.")]
		public readonly int Interval = 25;

		[Desc("If > 0, only move the proximity trigger when the source moves by more than this distance.",
		"Reduces per-tick ActorMap re-scans for mobile aura sources. Matches RevealsShroud's default.")]
		public readonly WDist MoveRecalculationThreshold = new(256);

		[Desc("Optional distance-falloff table that lets ONE aura reproduce a multi-tier gradient.",
		"When set, the proximity trigger uses the largest FalloffRanges entry, and each affected actor",
		"receives the SUM of FalloffAmounts whose paired FalloffRanges is >= the actor's horizontal",
		"distance from the source. Overrides Range and AmountPerTick when non-empty.")]
		public readonly WDist[] FalloffRanges = null;

		[Desc("Per-band amounts paired one-to-one with FalloffRanges. Must be the same length.")]
		public readonly int[] FalloffAmounts = null;

		public bool UseFalloff { get; private set; }
		public WDist FalloffMaxRange { get; private set; }
		long[] falloffRangesSq;

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (FalloffRanges == null || FalloffRanges.Length == 0)
				return;

			if (FalloffAmounts == null || FalloffAmounts.Length != FalloffRanges.Length)
				throw new YamlException($"{nameof(PhysicalStateAura)}: FalloffRanges and FalloffAmounts must have the same number of entries.");

			UseFalloff = true;
			falloffRangesSq = FalloffRanges.Select(r => (long)r.Length * r.Length).ToArray();
			FalloffMaxRange = FalloffRanges.Aggregate(WDist.Zero, (max, r) => r > max ? r : max);
		}

		// Sum of every band whose range covers the given horizontal distance, reproducing the old
		// stacked-tier step gradient (e.g. 4 / 12 / 24 / 40 / 60) from a single aura.
		public int AmountForDistanceSq(long horizontalDistanceSq)
		{
			var total = 0;
			for (var i = 0; i < falloffRangesSq.Length; i++)
				if (falloffRangesSq[i] >= horizontalDistanceSq)
					total += FalloffAmounts[i];

			return total;
		}

		public override object Create(ActorInitializer init) { return new PhysicalStateAura(init.Self, this); }
	}

	public class PhysicalStateAura : ConditionalTrait<PhysicalStateAuraInfo>,
		ITick, INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyOtherProduction, INotifyMoving
	{
		readonly Actor self;
		readonly HashSet<Actor> affectedActors = new();

		int proximityTrigger;
		int tickCount;
		WPos cachedPosition;
		WDist cachedRange;
		WDist desiredRange;
		WDist cachedVRange;
		WDist desiredVRange;

		WDist EffectiveRange => Info.UseFalloff ? Info.FalloffMaxRange : Info.Range;

		public PhysicalStateAura(Actor self, PhysicalStateAuraInfo info)
			: base(info)
		{
			this.self = self;
			cachedRange = WDist.Zero;
			cachedVRange = WDist.Zero;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			cachedPosition = self.CenterPosition;
			proximityTrigger = self.World.ActorMap.AddProximityTrigger(cachedPosition, cachedRange, cachedVRange, ActorEntered, ActorExited);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			self.World.ActorMap.RemoveProximityTrigger(proximityTrigger);
			affectedActors.Clear();
		}

		protected override void TraitEnabled(Actor self)
		{
			desiredRange = EffectiveRange;
			desiredVRange = Info.MaximumVerticalOffset;
		}

		protected override void TraitDisabled(Actor self)
		{
			desiredRange = WDist.Zero;
			desiredVRange = WDist.Zero;
			affectedActors.Clear();
		}

		void ITick.Tick(Actor self)
		{
			var pos = self.CenterPosition;
			var rangeChanged = desiredRange != cachedRange || desiredVRange != cachedVRange;

			// Only move the trigger once the source has travelled past the threshold, sparing a
			// per-tick ActorMap re-scan (and the resulting Dirty re-scan) for every mobile aura source.
			var movedEnough = pos != cachedPosition && (Info.MoveRecalculationThreshold.Length <= 0
				|| (pos - cachedPosition).LengthSquared > Info.MoveRecalculationThreshold.LengthSquared);

			if (rangeChanged || movedEnough)
			{
				cachedPosition = pos;
				cachedRange = desiredRange;
				cachedVRange = desiredVRange;
				self.World.ActorMap.UpdateProximityTrigger(proximityTrigger, cachedPosition, cachedRange, cachedVRange);
			}

			if (IsTraitDisabled || (!Info.UseFalloff && Info.AmountPerTick == 0))
				return;

			if (++tickCount >= Info.Interval)
			{
				tickCount = 0;
				ApplyEffect();
			}
		}

		void INotifyMoving.MovementTypeChanged(Actor self, MovementType type)
		{
			// Snap the trigger to the exact stop position so a resting aura is never left offset
			// by the sub-threshold remainder skipped while the source was moving.
			if (type == MovementType.None && self.IsInWorld && self.CenterPosition != cachedPosition)
			{
				cachedPosition = self.CenterPosition;
				cachedRange = desiredRange;
				cachedVRange = desiredVRange;
				self.World.ActorMap.UpdateProximityTrigger(proximityTrigger, cachedPosition, cachedRange, cachedVRange);
			}
		}

		void ApplyEffect()
		{
			var actorsToRemove = new List<Actor>();

			foreach (var actor in affectedActors)
			{
				if (actor.Disposed || actor.IsDead)
				{
					actorsToRemove.Add(actor);
					continue;
				}

				var physicalState = actor.TraitsImplementing<PhysicalState>()
					.FirstOrDefault(ps => ps.Name == Info.PhysicalStateName);

				if (physicalState == null)
					continue;

				var amount = Info.UseFalloff
					? Info.AmountForDistanceSq((actor.CenterPosition - self.CenterPosition).HorizontalLengthSquared)
					: Info.AmountPerTick;

				if (amount != 0)
					physicalState.ApplyChange(amount, self);
			}

			foreach (var actor in actorsToRemove)
				affectedActors.Remove(actor);
		}

		void ActorEntered(Actor a)
		{
			if (a.Disposed || self.Disposed)
				return;

			if (a == self && !Info.AffectsParent)
				return;

			if (affectedActors.Contains(a))
				return;

			var relationship = self.Owner.RelationshipWith(a.Owner);
			if (!Info.ValidRelationships.HasRelationship(relationship))
				return;

			var physicalState = a.TraitsImplementing<PhysicalState>()
				.FirstOrDefault(ps => ps.Name == Info.PhysicalStateName);

			if (physicalState != null)
				affectedActors.Add(a);
		}

		void ActorExited(Actor a)
		{
			affectedActors.Remove(a);
		}

		void INotifyOtherProduction.UnitProducedByOther(Actor self, Actor producer, Actor produced, string productionType, TypeDictionary init)
		{
			if (produced.OccupiesSpace == null)
				return;

			if (IsTraitDisabled)
				return;

			if ((produced.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= EffectiveRange.LengthSquared)
			{
				if (!Info.ValidRelationships.HasRelationship(self.Owner.RelationshipWith(produced.Owner)))
					return;

				var physicalState = produced.TraitsImplementing<PhysicalState>()
					.FirstOrDefault(ps => ps.Name == Info.PhysicalStateName);

				if (physicalState != null)
					affectedActors.Add(produced);
			}
		}
	}
}
