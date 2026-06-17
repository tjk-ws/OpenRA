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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace OpenRA.Server
{
	public class OrderBuffer
	{
		const int NumberOfFrames = 20;
		const int Interval = 1000;

		// Limit the TickScale to maximum of 10%
		const float MaxTickScale = 1.1f;

		const int EmptyValue = -1;

		Stopwatch gameTimer;
		long nextUpdate = 0;

		readonly ConcurrentDictionary<int, long> timestamps = [];
		readonly ConcurrentDictionary<int, Queue<long>> deltas = [];

		int timestep;
		int ticksPerInterval;
		int baselinePlayer;
		List<int> players;

		// --- Adaptive game-speed (Stage C Prong C): host-authoritative ABSOLUTE pacing ---
		// The admin/host runs the adaptive-speed controller on its own sim-compute and submits an
		// absolute pacing scale (AdaptivePacingScale order). The server sanitizes it (SetHostAbsoluteScale)
		// and folds it into the per-player broadcast as max(relative, absolute) — so every client slows to
		// the host's sustainable pace while the existing per-player RELATIVE scale still corrects jitter.
		// This term may run deeper than the relative MaxTickScale because it is host-MEASURED (not
		// client-claimable) and admin-gated. 4.0 = quarter speed; 4.0*40ms = 160ms paced timestep, safely
		// under the loop's 250ms MaxLogicTicksBehind. All four fields below are touched ONLY on the
		// ServerThread (GetTickScales + SetHostAbsoluteScale via InterpretServerOrder) → no locking needed.
		const float MaxAbsoluteScale = 4f;

		// Fail-safe: if the host stops reporting (disconnect / crash / host-migration) while a slowdown is in
		// effect, release it after this long so the game can't stay stuck slow with no authority to lift it.
		// The host re-sends a heartbeat well inside this window (AdaptiveGameSpeedHostInfo.HeartbeatMs).
		const long HostScaleExpiryMs = 3000;

		readonly Dictionary<int, float> lastRelative = [];
		float hostAbsoluteScale = 1f;
		long lastHostScaleTime;
		bool absoluteDirty;
		bool relativeDirty;

		public void AddOrderTimestamp(int playerIndex)
		{
			timestamps[playerIndex] = gameTimer.ElapsedMilliseconds;

			if (timestamps.Values.All(t => t != EmptyValue))
			{
				var baseline = timestamps[baselinePlayer];

				foreach (var (p, q) in timestamps)
				{
					var dt = baseline - q;

					var playerDeltas = deltas[p];
					playerDeltas.Enqueue(dt);

					if (playerDeltas.Count > NumberOfFrames)
						playerDeltas.Dequeue();

					timestamps[p] = EmptyValue;
				}
			}
		}

		public void Start(GameSpeed gameSpeed, IEnumerable<int> players)
		{
			timestep = gameSpeed.Timestep;
			ticksPerInterval = Interval / timestep;

			this.players = players.ToList();
			baselinePlayer = this.players[0];

			foreach (var player in this.players)
			{
				timestamps.TryAdd(player, EmptyValue);
				deltas.TryAdd(player, new Queue<long>());
				lastRelative[player] = 1f;
			}

			gameTimer = Stopwatch.StartNew();
			nextUpdate = gameTimer.ElapsedMilliseconds + Interval;
		}

		// Sanitize and store the host's absolute pacing scale. Called on the ServerThread from
		// InterpretServerOrder AFTER the admin check — the float itself is still untrusted input, so reject
		// NaN/Inf and clamp into [1, MaxAbsoluteScale]. A real change is flagged so the next GetTickScales
		// pushes it immediately instead of waiting up to Interval ms for the relative refresh.
		public void SetHostAbsoluteScale(float scale)
		{
			if (float.IsNaN(scale) || float.IsInfinity(scale))
				return;

			// Refresh the liveness timestamp on EVERY accepted report (incl. unchanged heartbeats), but only
			// flag a push when the value actually moved.
			lastHostScaleTime = gameTimer.ElapsedMilliseconds;

			var sanitized = scale.Clamp(1f, MaxAbsoluteScale);
			if (sanitized != hostAbsoluteScale)
			{
				hostAbsoluteScale = sanitized;
				absoluteDirty = true;
			}
		}

		public IEnumerable<(int PlayerIndex, float TickScale)> GetTickScales()
		{
			if (players == null)
				yield break;

			var now = gameTimer.ElapsedMilliseconds;

			// Fail-safe release if the host went quiet while a slowdown was active.
			if (hostAbsoluteScale != 1f && now - lastHostScaleTime > HostScaleExpiryMs)
			{
				hostAbsoluteScale = 1f;
				absoluteDirty = true;
			}

			if (now >= nextUpdate)
			{
				nextUpdate = now + Interval;

				// Refresh the per-player RELATIVE scale (order-arrival lag) once every player has a full
				// sample window. Cached into lastRelative so it can be re-merged with the host-absolute on a
				// push-on-change broadcast between intervals.
				if (!deltas.IsEmpty && deltas.Values.All(q => q.Count == NumberOfFrames))
				{
					var medians = deltas.Select(d => (PlayerIndex: d.Key, Delta: Median(d.Value.ToArray()))).ToList();

					// We need to check if we have a connection slower than our baseline and then use that as our offset.
					var minValue = medians.MinBy(p => p.Delta).Delta;
					var offset = minValue < 0 ? Math.Abs(minValue) : 0;

					foreach (var (playerIndex, delta) in medians)
					{
						var deltaPerTick = (delta + offset) / (float)ticksPerInterval;
						var tickScale = (timestep + deltaPerTick) / timestep;
						lastRelative[playerIndex] = tickScale.Clamp(1f, MaxTickScale);
					}

					relativeDirty = true;
				}
			}

			// Broadcast only when something changed: a fresh relative scale, or a new host-absolute scale
			// (which pushes immediately). When neither changed — and when no host-absolute is ever submitted
			// (hostAbsoluteScale stays 1, absoluteDirty never set) — this is behaviourally identical to the
			// stock per-player relative broadcast: max(relative, 1) == relative, emitted on the same cadence.
			if (!relativeDirty && !absoluteDirty)
				yield break;

			relativeDirty = false;
			absoluteDirty = false;

			foreach (var player in players)
			{
				var relative = lastRelative.TryGetValue(player, out var r) ? r : 1f;
				yield return (player, Math.Max(relative, hostAbsoluteScale));
			}
		}

		static long Median(long[] a)
		{
			Array.Sort(a);
			var n = a.Length;

			if (n % 2 != 0)
				return a[n / 2];

			return (a[(n - 1) / 2] + a[n / 2]) / 2;
		}

		public void RemovePlayer(int player)
		{
			players.Remove(player);
			if (player == baselinePlayer && players.Count > 0)
			{
				var newBaseline = players[0];
				Interlocked.Exchange(ref baselinePlayer, newBaseline);
			}

			timestamps.TryRemove(player, out _);
			deltas.TryRemove(player, out _);
		}
	}
}
