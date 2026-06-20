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
using System.Linq;
using NUnit.Framework;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class FactionResolutionTest
	{
		static FactionInfo Faction(string name, string side, string members = null)
		{
			var yaml = $"Faction:\n\tInternalName: {name}\n\tName: {name}\n\tSide: {side}";
			if (members != null)
				yaml += $"\n\tRandomFactionMembers: {members}";

			return FieldLoader.Load<FactionInfo>(MiniYaml.FromString(yaml, "test").First().Value);
		}

		static List<FactionInfo> SampleFactions()
		{
			return
			[
				Faction("gdi", "TD"),
				Faction("nod", "TD"),
				Faction("allies", "RA"),
				Faction("soviet", "RA"),
				Faction("Random", "Random", "gdi, nod, allies, soviet"),
				Faction("Randomcnc", "Random", "gdi, nod"),
			];
		}

		[TestCase(TestName = "A Random pick never resolves to a manually-reserved faction.")]
		public void RandomAvoidsReservedFaction()
		{
			var factions = SampleFactions();
			var reserved = new HashSet<string> { "gdi" };

			for (var seed = 0; seed < 200; seed++)
			{
				var result = Player.ResolveFaction("Random", factions, new MersenneTwister(seed), true, new RandomFactionDeck(), reserved);
				Assert.That(result.InternalName, Is.Not.EqualTo("gdi"), $"Random resolved to the reserved faction (seed {seed}).");
			}
		}

		[TestCase(TestName = "Resolution falls back to the full pool when every member is reserved.")]
		public void FallsBackWhenAllReserved()
		{
			var factions = SampleFactions();
			var reserved = new HashSet<string> { "gdi", "nod" }; // exhausts Randomcnc's pool

			var result = Player.ResolveFaction("Randomcnc", factions, new MersenneTwister(0), true, new RandomFactionDeck(), reserved);
			Assert.That(new[] { "gdi", "nod" }, Does.Contain(result.InternalName));
		}

		[TestCase(TestName = "With no reservations the result is unchanged (regression).")]
		public void NoReservationsUnchanged()
		{
			var factions = SampleFactions();

			FactionInfo Resolve(IReadOnlySet<string> reserved) =>
				Player.ResolveFaction("Random", factions, new MersenneTwister(12345), true, new RandomFactionDeck(), reserved);

			Assert.That(Resolve(new HashSet<string>()).InternalName, Is.EqualTo(Resolve(null).InternalName));
		}

		[TestCase(TestName = "Multiple Random slots stay distinct and avoid the reserved faction.")]
		public void MultipleRandomSlotsDistinctAndAvoidReserved()
		{
			var factions = SampleFactions(); // Random pool: gdi, nod, allies, soviet
			var reserved = new HashSet<string> { "gdi" };
			var rng = new MersenneTwister(7);
			var deck = new RandomFactionDeck();

			var picks = Enumerable.Range(0, 3)
				.Select(_ => Player.ResolveFaction("Random", factions, rng, true, deck, reserved).InternalName)
				.ToList();

			Assert.That(picks, Does.Not.Contain("gdi"));
			Assert.That(picks.Distinct().Count(), Is.EqualTo(3), "Random slots should be a distinct permutation.");
			Assert.That(picks.OrderBy(x => x), Is.EqualTo(new[] { "allies", "nod", "soviet" }));
		}
	}
}
