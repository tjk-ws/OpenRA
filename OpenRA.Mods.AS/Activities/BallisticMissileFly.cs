#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Activities;
using OpenRA.Mods.AS.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Activities
{
	public class BallisticMissileFly : Activity
	{
		readonly BallisticMissile bm;
		readonly WPos initPos;
		readonly WPos targetPos;
		int length;
		int ticks;
		int facing;

		public BallisticMissileFly(Actor self, Target t, BallisticMissile bm)
		{
			this.bm = bm;

			initPos = self.CenterPosition;
			targetPos = t.CenterPosition; // fixed position == no homing
			length = Math.Max((targetPos - initPos).Length / this.bm.Info.Speed, 1);
			facing = (targetPos - initPos).Yaw.Facing;
		}

		int GetEffectiveFacing()
		{
			var at = (float)ticks / (length - 1);
			var attitude = bm.Info.LaunchAngle.Tan() * (1 - 2 * at) / (4 * 1024);

			var u = (facing % 128) / 128f;
			var scale = 512 * u * (1 - u);

			return (int)(facing < 128
				? facing - scale * attitude
				: facing + scale * attitude);
		}

		public void FlyToward(Actor self, BallisticMissile bm)
		{
			var pos = WPos.LerpQuadratic(initPos, targetPos, bm.Info.LaunchAngle, ticks, length);
			bm.SetPosition(self, pos);
			bm.Facing = GetEffectiveFacing();
		}

		public override bool Tick(Actor self)
		{
			var d = targetPos - self.CenterPosition;

			// The next move would overshoot, so consider it close enough
			var move = bm.FlyStep(bm.Facing);

			// Destruct so that Explodes will be called
			if (d.HorizontalLengthSquared < move.HorizontalLengthSquared)
			{
				Queue(new CallFunc(() => self.Kill(self, bm.Info.DamageTypes)));
				return true;
			}

			FlyToward(self, bm);
			ticks++;
			return false;
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			yield return Target.FromPos(targetPos);
		}
	}
}
