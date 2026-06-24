#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Gives a condition to the actor for a limited time.")]
	public class GrantTimedConditionInfo : PausableConditionalTraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("The condition to grant.")]
		public readonly string Condition = null;

		[Desc("Number of ticks to wait before revoking the condition.")]
		public readonly int Duration = 50;

		[Desc("Only revoke the condition once its Duration elapses, never early because RequiresCondition",
			"stopped being met. Lets the timer run to completion even if the granting condition flickers",
			"off and back on, instead of being revoked and re-granted (which would reset the countdown).")]
		public readonly bool RevokeAfterDurationOnly = false;

		public override object Create(ActorInitializer init) { return new GrantTimedCondition(this); }
	}

	public class GrantTimedCondition : PausableConditionalTrait<GrantTimedConditionInfo>, ITick, ISync, INotifyCreated
	{
		readonly GrantTimedConditionInfo info;
		int token = Actor.InvalidConditionToken;
		IConditionTimerWatcher[] watchers;

		[VerifySync]
		public int Ticks { get; private set; }

		public GrantTimedCondition(GrantTimedConditionInfo info)
			: base(info)
		{
			this.info = info;
			Ticks = info.Duration;
		}

		protected override void Created(Actor self)
		{
			watchers = self.TraitsImplementing<IConditionTimerWatcher>().Where(Notifies).ToArray();

			base.Created(self);
		}

		void GrantCondition(Actor self, string condition)
		{
			if (string.IsNullOrEmpty(condition))
				return;

			if (token == Actor.InvalidConditionToken)
			{
				Ticks = info.Duration;
				token = self.GrantCondition(condition);
			}
		}

		void RevokeCondition(Actor self)
		{
			if (token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}

		void ITick.Tick(Actor self)
		{
			var hasToken = token != Actor.InvalidConditionToken;

			// Normally the condition is revoked the instant RequiresCondition stops being met. With
			// RevokeAfterDurationOnly a granted condition instead always runs its full Duration and is
			// revoked only by the timer below, so a trigger that flickers off can't cut the timer short
			// (nor reset it on the way back on).
			if (IsTraitDisabled && hasToken && !info.RevokeAfterDurationOnly)
			{
				RevokeCondition(self);
				return;
			}

			if (IsTraitPaused)
				return;

			// When disabled, only keep counting if RevokeAfterDurationOnly is letting an active token
			// finish its Duration; otherwise there is nothing to do.
			if (IsTraitDisabled && !(info.RevokeAfterDurationOnly && hasToken))
				return;

			foreach (var w in watchers)
				w.Update(info.Duration, Ticks);

			if (!hasToken)
				return;

			if (--Ticks < 0)
				RevokeCondition(self);
		}

		protected override void TraitEnabled(Actor self)
		{
			GrantCondition(self, info.Condition);
		}

		bool Notifies(IConditionTimerWatcher watcher) { return watcher.Condition == Info.Condition; }
	}
}
