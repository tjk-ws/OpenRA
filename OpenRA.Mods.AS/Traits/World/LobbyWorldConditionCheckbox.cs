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
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Enables a condition on the world actor if the checkbox is enabled.")]
	public class LobbyWorldConditionCheckboxInfo : TraitInfo, ILobbyOptions
	{
		[FieldLoader.Require]
		[Desc("Internal id for this checkbox.")]
		public readonly string ID = null;

		[FieldLoader.Require]
		[Desc("Display name for this checkbox.")]
		public readonly string Label = null;

		[Desc("Description name for this checkbox.")]
		public readonly string Description = null;

		[Desc("Default value of the checkbox in the lobby.")]
		public readonly bool Enabled = false;

		[Desc("Prevent the checkbox from being changed from its default value.")]
		public readonly bool Locked = false;

		[Desc("Display the checkbox in the lobby.")]
		public readonly bool Visible = true;

		[Desc("Display order for the checkbox in the lobby.")]
		public readonly int DisplayOrder = 0;

		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("The condition to grant when this checkbox is enabled.")]
		public readonly string Condition = "";

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(Ruleset rules)
		{
			yield return new LobbyBooleanOption(ID, Label, Description,
				Visible, DisplayOrder, Enabled, Locked);
		}

		public override object Create(ActorInitializer init) { return new LobbyWorldConditionCheckbox(this); }
	}

	public class LobbyWorldConditionCheckbox : INotifyCreated
	{
		readonly LobbyWorldConditionCheckboxInfo info;
		HashSet<string> prerequisites = new HashSet<string>();

		public LobbyWorldConditionCheckbox(LobbyWorldConditionCheckboxInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			var enabled = self.World.LobbyInfo.GlobalSettings.OptionOrDefault(info.ID, info.Enabled);
			if (!enabled)
				return;

			self.GrantCondition(info.Condition);
		}
	}
}
