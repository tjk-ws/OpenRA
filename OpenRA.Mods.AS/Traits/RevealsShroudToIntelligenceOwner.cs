#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public class RevealsShroudToIntelligenceOwnerInfo : RevealsShroudInfo
	{
		[FieldLoader.Require]
		[Desc("Types of intelligence this trait requires.")]
		public readonly HashSet<string> Types = new HashSet<string>();

		public override object Create(ActorInitializer init) { return new RevealsShroudToIntelligenceOwner(this); }
	}

	public class RevealsShroudToIntelligenceOwner : RevealsShroud, INotifyAddedToWorld, ITick
	{
		public readonly RevealsShroudToIntelligenceOwnerInfo RSTIOInfo;
		public List<Player> IntelOwners = new List<Player>();

		readonly Shroud.SourceType rstiotype;

		public RevealsShroudToIntelligenceOwner(RevealsShroudToIntelligenceOwnerInfo info)
			: base(info)
		{
			RSTIOInfo = info;
			rstiotype = info.RevealGeneratedShroud ? Shroud.SourceType.Visibility
				: Shroud.SourceType.PassiveVisibility;
		}

		protected override void AddCellsToPlayerShroud(Actor self, Player p, PPos[] uv)
		{
			p.Shroud.AddSource(this, rstiotype, uv);
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld)
				return;

			if (self.Owner.NonCombatant)
				return;

			if (!IntelOwners.Any())
				return;

			var centerPosition = self.CenterPosition;
			var projectedPos = centerPosition - new WVec(0, centerPosition.Z, centerPosition.Z);
			var projectedLocation = self.World.Map.CellContaining(projectedPos);
			var traitDisabled = IsTraitDisabled;
			var range = Range;

			if (cachedLocation == projectedLocation && cachedRange == range && traitDisabled == cachedTraitDisabled)
				return;

			cachedRange = range;
			cachedLocation = projectedLocation;
			cachedTraitDisabled = traitDisabled;

			var cells = ProjectedCells(self);
			foreach (var p in self.World.Players)
			{
				RemoveCellsFromPlayerShroud(self, p);
				if (IntelOwners.Contains(p))
					AddCellsToPlayerShroud(self, p, cells);
			}
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (!self.IsInWorld)
				return;

			if (self.Owner.NonCombatant)
				return;

			var centerPosition = self.CenterPosition;
			var projectedPos = centerPosition - new WVec(0, centerPosition.Z, centerPosition.Z);
			cachedLocation = self.World.Map.CellContaining(projectedPos);
			cachedTraitDisabled = IsTraitDisabled;
			var cells = ProjectedCells(self);

			foreach (var p in self.World.Players)
			{
				var hasIntel = self.World.ActorsWithTrait<GivesIntelligence>()
					.Where(t => t.Actor.Owner == p && t.Trait.Info.Types.Overlaps(RSTIOInfo.Types) && !t.Trait.IsTraitDisabled).Any();

				if (hasIntel)
				{
					RemoveCellsFromPlayerShroud(self, p);
					AddCellsToPlayerShroud(self, p, cells);

					IntelOwners.Add(p);
				}
			}
		}

		public void AddCellsToIntelligenceOwnerShroud(Actor self, Player p, PPos[] uv)
		{
			AddCellsToPlayerShroud(self, p, uv);
		}

		public void RemoveCellsFromIntelligenceOwnerShroud(Actor self, Player p)
		{
			RemoveCellsFromPlayerShroud(self, p);
		}

		public PPos[] GetIntelligenceProjectedCells(Actor self)
		{
			return ProjectedCells(self);
		}
	}
}
