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

using NUnit.Framework;
using OpenRA.Mods.Common.Traits.Render;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class WithIdleOverlayTest
	{
		[TestCase("fit-{actor}", "Example.Actor", "fit-Example.Actor")]
		[TestCase("{ACTOR}-fit-{actor}", "Example.Actor", "Example.Actor-fit-Example.Actor")]
		[TestCase("idle", "Example.Actor", "idle")]
		[TestCase(null, "Example.Actor", null)]
		public void ResolvesActorSequenceToken(string sequence, string actorName, string expected)
		{
			Assert.That(WithIdleOverlayInfo.ResolveSequence(sequence, actorName), Is.EqualTo(expected));
		}
	}
}
