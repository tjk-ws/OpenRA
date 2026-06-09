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
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA
{
	/// <summary>
	/// Deals out random faction members without repetition (a permutation) within a single
	/// player-creation pass. Each random faction (e.g. "Random", "RTF") gets its own pool keyed
	/// by its <see cref="FactionInfo.InternalName"/>. When a pool is exhausted it is refilled and
	/// reshuffled, so e.g. a pool ABCDE yields each of A-E once before any repeats.
	/// </summary>
	public sealed class RandomFactionDeck
	{
		readonly Dictionary<string, List<string>> remaining = new();

		/// <summary>
		/// Returns the next member of the given random faction's pool, removing it so it is not
		/// dealt again until the pool is exhausted and refilled. Consumes exactly one draw from
		/// <paramref name="playerRandom"/>, matching the RNG consumption of a plain random pick so
		/// that server and client player-creation passes stay in sync.
		/// </summary>
		public string Next(FactionInfo randomFaction, MersenneTwister playerRandom)
		{
			if (!remaining.TryGetValue(randomFaction.InternalName, out var pool) || pool.Count == 0)
			{
				pool = randomFaction.RandomFactionMembers.ToList();
				remaining[randomFaction.InternalName] = pool;
			}

			var index = playerRandom.Next(pool.Count);
			var faction = pool[index];
			pool.RemoveAt(index);
			return faction;
		}
	}
}
