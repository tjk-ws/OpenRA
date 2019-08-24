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

namespace OpenRA.Mods.AS.Warheads
{
	[Desc("Interacts with the TemporaryOwnerManager trait.")]
	public class ChangeOwnerASWarhead : WarheadAS
	{
		[Desc("Duration of the owner change (in ticks). Set to 0 to make it permanent.")]
		public readonly int Duration = 0;

		[Desc("The condition to apply. Must be included in the target actor's ExternalConditions list.")]
		public readonly string Condition = null;

		public readonly WDist Range = WDist.FromCells(1);

		public override void DoImpact(Target target, Target guidedTarget, Actor firedBy, IEnumerable<int> damageModifiers)
		{
			var actors = target.Type == TargetType.Actor ? new[] { target.Actor } :
				firedBy.World.FindActorsInCircle(target.CenterPosition, Range);

			foreach (var a in actors)
			{
				if (!IsValidAgainst(a, firedBy))
					continue;

				if (Duration == 0)
					a.ChangeOwner(firedBy.Owner); // Permanent
				else
				{
					var tempOwnerManager = a.TraitOrDefault<TemporaryOwnerManager>();
					if (tempOwnerManager == null)
						continue;

					tempOwnerManager.ChangeOwner(a, firedBy.Owner, Duration);
				}

				var external = a.TraitsImplementing<ExternalCondition>()
					.FirstOrDefault(t => t.Info.Condition == Condition && t.CanGrantCondition(a, firedBy));

				if (external != null)
					external.GrantCondition(a, firedBy, Duration);

				// Stop shooting, you have new enemies
				a.CancelActivity();
			}
		}
	}
}
