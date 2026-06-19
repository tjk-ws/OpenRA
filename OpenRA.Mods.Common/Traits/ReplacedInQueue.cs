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

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Lists the actors this actor may be replaced by in production queues (e.g. when a unit is upgraded).",
		"When this actor becomes unbuildable while one of the listed actors is buildable in the same queue,",
		"queued items are migrated to that actor instead of being cancelled and refunded.")]
	public class ReplacedInQueueInfo : TraitInfo
	{
		[FieldLoader.Require]
		[Desc("Actors that may replace this one in a production queue.")]
		public readonly HashSet<string> Actors = [];

		public override object Create(ActorInitializer init) { return new ReplacedInQueue(this); }
	}

	public class ReplacedInQueue
	{
		public readonly ReplacedInQueueInfo Info;

		public ReplacedInQueue(ReplacedInQueueInfo info)
		{
			Info = info;
		}
	}
}
