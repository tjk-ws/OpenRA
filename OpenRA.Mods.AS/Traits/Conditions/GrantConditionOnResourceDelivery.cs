#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Grants a condition when this refinery receives resources.")]
	public class GrantConditionOnResourceDeliveryInfo : PausableConditionalTraitInfo, Requires<RefineryInfo>
	{
		[GrantedConditionReference, FieldLoader.Require]
		[Desc("The condition to grant.")]
		public readonly string Condition = null;

		[FieldLoader.Require]
		public readonly int Duration;

		public override object Create(ActorInitializer init) { return new GrantConditionOnResourceDelivery(init.Self, this); }
	}

	public class GrantConditionOnResourceDelivery : PausableConditionalTrait<GrantConditionOnResourceDeliveryInfo>, ITick, INotifyCreated, IRefineryResourceDelivered
	{
		readonly GrantConditionOnResourceDeliveryInfo info;

		ConditionManager manager;
		int token = ConditionManager.InvalidConditionToken;

		int ticks;

		public GrantConditionOnResourceDelivery(Actor self, GrantConditionOnResourceDeliveryInfo info)
			: base(info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			manager = self.Trait<ConditionManager>();
		}

		void IRefineryResourceDelivered.ResourceGiven(Actor self, int amount)
		{
			if (IsTraitDisabled)
				return;

			ticks = info.Duration;

			if (token == ConditionManager.InvalidConditionToken)
				token = manager.GrantCondition(self, info.Condition);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || IsTraitPaused || --ticks > 0)
				return;

			if (token != ConditionManager.InvalidConditionToken)
				token = manager.RevokeCondition(self, token);
		}
	}
}
