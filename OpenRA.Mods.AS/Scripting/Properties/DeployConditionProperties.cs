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

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Scripting;

namespace OpenRA.Mods.AS.Scripting
{
	[ScriptPropertyGroup("General")]
	public class DeployConditionProperties : ScriptActorProperties
	{
		readonly GrantConditionOnDeploy[] gcods;

		public DeployConditionProperties(ScriptContext context, Actor self)
			: base(context, self)
		{
			gcods = self.TraitsImplementing<GrantConditionOnDeploy>().ToArray();
		}

		[Desc("Deploy the actor.")]
		public void Deploy()
		{
			foreach (var gcod in gcods)
				if (!gcod.IsTraitDisabled && !gcod.IsTraitPaused)
					gcod.Deploy();
		}

		[Desc("Undeploy the actor.")]
		public void Undeploy()
		{
			foreach (var gcod in gcods)
				if (!gcod.IsTraitDisabled && !gcod.IsTraitPaused)
					gcod.Undeploy();
		}
	}
}
