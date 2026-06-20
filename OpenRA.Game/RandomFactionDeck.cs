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
		/// <param name="reserved">
		/// Faction internal names to avoid dealing if possible (e.g. factions a player has manually
		/// picked). The pick falls back to the full remaining pool when every option is reserved, so
		/// resolution always succeeds. Must be identical across the server and client passes to keep
		/// the RNG draw in sync.
		/// </param>
		public string Next(FactionInfo randomFaction, MersenneTwister playerRandom, IReadOnlySet<string> reserved = null)
		{
			if (!remaining.TryGetValue(randomFaction.InternalName, out var pool) || pool.Count == 0)
			{
				pool = randomFaction.RandomFactionMembers.ToList();
				remaining[randomFaction.InternalName] = pool;
			}

			var candidates = reserved == null || reserved.Count == 0
				? pool
				: pool.Where(f => !reserved.Contains(f)).ToList();
			if (candidates.Count == 0)
				candidates = pool;

			var faction = candidates[playerRandom.Next(candidates.Count)];
			pool.Remove(faction);
			return faction;
		}
	}
}
