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
	[Desc("Tracks number of actors killed by this actor.")]
	public class KillCounterInfo : TraitInfo
	{
		[Desc("Player relationships that count toward kills.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Neutral | PlayerRelationship.Enemy;

		public override object Create(ActorInitializer init) { return new KillCounter(this); }
	}

	public class KillCounter : INotifyAppliedDamage
	{
		readonly KillCounterInfo info;

		public int Kills;

		public KillCounter(KillCounterInfo info)
		{
			this.info = info;
		}

		void INotifyAppliedDamage.AppliedDamage(Actor self, Actor damaged, AttackInfo e)
		{
			// Don't notify suicides
			if (e.DamageState == DamageState.Dead && damaged != e.Attacker && info.ValidRelationships.HasRelationship(damaged.Owner.RelationshipWith(self.Owner)))
			{
				Kills++;
			}
		}
	}
}
