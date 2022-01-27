#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Shows overlay of ProductionIconOverlayManager with matching types when defined prerequisites are granted.")]
	public class WithProductionIconOverlayInfo : TraitInfo, Requires<GainsExperienceInfo>
	{
		public readonly string[] Prerequisites = Array.Empty<string>();

		[FieldLoader.Require]
		public readonly string[] Types = Array.Empty<string>();

		public override object Create(ActorInitializer init) { return new WithProductionIconOverlay(this); }
	}

	public class WithProductionIconOverlay
	{
		public WithProductionIconOverlay(WithProductionIconOverlayInfo info) { }
	}
}
