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

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public class ActorStatOverrideInfo : ConditionalTraitInfo, Requires<ActorStatValuesInfo>
	{
		[Desc("Types of stats to show.")]
		public readonly ActorStatContent[] Stats = { ActorStatContent.Armor, ActorStatContent.Sight };

		public override object Create(ActorInitializer init) { return new ActorStatOverride(init, this); }
	}

	public class ActorStatOverride : ConditionalTrait<ActorStatOverrideInfo>
	{
		readonly ActorStatValues asv;

		public ActorStatOverride(ActorInitializer init, ActorStatOverrideInfo info)
			: base(info)
		{
			asv = init.Self.Trait<ActorStatValues>();
		}

		protected override void TraitEnabled(Actor self) { asv.CalculateStats(); }
		protected override void TraitDisabled(Actor self) { asv.CalculateStats(); }
	}
}
