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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Grants a condition when this building is infiltrated.")]
	class GrantConditionOnInfiltrationInfo : ConditionalTraitInfo
	{
		public readonly BitSet<TargetableType> Types;

		[FieldLoader.Require]
		[GrantedConditionReference]
		public readonly string Condition = null;

		[Desc("Use `TimedConditionBar` for visualization.")]
		public readonly int Duration = 0;

		public override object Create(ActorInitializer init) { return new GrantConditionOnInfiltration(this); }
	}

	class GrantConditionOnInfiltration : ConditionalTrait<GrantConditionOnInfiltrationInfo>, INotifyInfiltrated, INotifyCreated, ITick
	{
		ConditionManager conditionManager;
		int conditionToken = ConditionManager.InvalidConditionToken;
		int duration;
		IConditionTimerWatcher[] watchers;

		public GrantConditionOnInfiltration(GrantConditionOnInfiltrationInfo info)
			: base(info) { }

		void INotifyInfiltrated.Infiltrated(Actor self, Actor infiltrator, BitSet<TargetableType> types)
		{
			if (!Info.Types.Overlaps(types) || IsTraitDisabled)
				return;

			duration = Info.Duration;

			if (conditionToken == ConditionManager.InvalidConditionToken)
				conditionToken = conditionManager.GrantCondition(self, Info.Condition);
		}

		bool Notifies(IConditionTimerWatcher watcher) { return watcher.Condition == Info.Condition; }

		protected override void Created(Actor self)
		{
			conditionManager = self.Trait<ConditionManager>();
			watchers = self.TraitsImplementing<IConditionTimerWatcher>().Where(Notifies).ToArray();

			base.Created(self);
		}

		void ITick.Tick(Actor self)
		{
			if (conditionToken != ConditionManager.InvalidConditionToken && Info.Duration > 0)
			{
				if (--duration < 0)
				{
					conditionToken = conditionManager.RevokeCondition(self, conditionToken);
					foreach (var w in watchers)
						w.Update(0, 0);
				}
				else
					foreach (var w in watchers)
						w.Update(Info.Duration, duration);
			}
		}
	}
}
