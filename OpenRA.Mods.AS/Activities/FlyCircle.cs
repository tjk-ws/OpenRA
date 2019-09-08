#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.AS.Activities
{
	public class FlyCircle : Activity
	{
		readonly Aircraft aircraft;
		int remainingTicks;

		public FlyCircle(Actor self, int ticks = -1)
		{
			aircraft = self.Trait<Aircraft>();
			remainingTicks = ticks;
		}

		public override bool Tick(Actor self)
		{
			if (remainingTicks == 0 || (NextActivity != null && remainingTicks < 0))
				return true;

			if (aircraft.ForceLanding || IsCanceling)
				return true;

			if (remainingTicks > 0)
				remainingTicks--;

			if (!aircraft.Info.CanHover)
			{
				// We can't possibly turn this fast
				var desiredFacing = aircraft.Facing + 64;

				// This override is necessary, otherwise aircraft with CanSlide would circle sideways
				var move = aircraft.FlyStep(aircraft.Facing);

				Fly.FlyTick(self, aircraft, desiredFacing, aircraft.Info.CruiseAltitude, move);
			}

			return false;
		}
	}
}
