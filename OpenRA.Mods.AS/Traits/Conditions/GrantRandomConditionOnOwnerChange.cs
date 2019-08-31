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
	[Desc("Grants a random condition from a predefined list to the actor when created." +
		"Rerandomized when the actor changes ownership.")]
	public class GrantRandomConditionOnOwnerChangeInfo : ITraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("List of conditions to grant from.")]
		public readonly string[] Conditions = null;

		public object Create(ActorInitializer init) { return new GrantRandomConditionOnOwnerChange(init.Self, this); }
	}

	public class GrantRandomConditionOnOwnerChange : INotifyCreated, INotifyOwnerChanged
	{
		readonly GrantRandomConditionOnOwnerChangeInfo info;

		ConditionManager conditionManager;
		int conditionToken = ConditionManager.InvalidConditionToken;

		public GrantRandomConditionOnOwnerChange(Actor self, GrantRandomConditionOnOwnerChangeInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			if (!info.Conditions.Any())
				return;

			var condition = info.Conditions.Random(self.World.SharedRandom);
			conditionManager = self.Trait<ConditionManager>();
			conditionToken = conditionManager.GrantCondition(self, condition);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			if (conditionToken != ConditionManager.InvalidConditionToken)
			{
				conditionManager.RevokeCondition(self, conditionToken);
				var condition = info.Conditions.Random(self.World.SharedRandom);
				conditionToken = conditionManager.GrantCondition(self, condition);
			}
		}
	}
}
