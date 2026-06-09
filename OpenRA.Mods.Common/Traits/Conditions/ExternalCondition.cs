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
using System.Runtime.CompilerServices;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[RequireExplicitImplementation]
	public interface IConditionTimerWatcher
	{
		string Condition { get; }
		void Update(int duration, int remaining);
	}

	[Desc("Allows a condition to be granted from an external source (Lua, warheads, etc).")]
	public class ExternalConditionInfo : TraitInfo
	{
		[GrantedConditionReference]
		[FieldLoader.Require]
		public readonly string Condition = null;

		[Desc("If > 0, restrict the number of times that this condition can be granted by a single source.")]
		public readonly int SourceCap = 0;

		[Desc("If > 0, restrict the number of times that this condition can be granted by any source.")]
		public readonly int TotalCap = 0;

		public override object Create(ActorInitializer init) { return new ExternalCondition(this); }
	}

	public class ExternalCondition : INotifyCreated, INotifyOwnerChanged, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly struct TimedToken(int token, Actor self, object source, int duration)
		{
			public readonly int Expires = self.World.WorldTick + duration;
			public readonly int Token = token;
			public readonly object Source = source;
		}

		public readonly ExternalConditionInfo Info;
		readonly Dictionary<object, HashSet<int>> permanentTokens = [];

		// Tokens are sorted on insert/remove by ascending expiry time
		readonly List<TimedToken> timedTokens = [];
		IConditionTimerWatcher[] watchers;
		Actor self;
		int duration;
		int expires;

		// Idle ExternalConditions (those holding no live timed token) used to be visited by the global
		// ITick loop every frame - tens of thousands of no-op early-returns in a big battle. Instead
		// this trait stays out of that loop entirely (it no longer implements ITick) and registers with
		// a per-world ticker only while it actually holds a timed token, so only the handful of
		// conditions currently counting down pay any per-tick cost.
		bool registered;
		bool removed;

		public ExternalCondition(ExternalConditionInfo info)
		{
			Info = info;
		}

		public bool CanGrantCondition(object source)
		{
			if (source == null)
				return false;

			// Timed tokens do not count towards the source cap: the condition with the shortest
			// remaining duration can always be revoked to make room.
			if (Info.SourceCap > 0 &&
				permanentTokens.TryGetValue(source, out var permanentTokensForSource) &&
				permanentTokensForSource.Count >= Info.SourceCap)
				return false;

			if (Info.TotalCap > 0 &&
				permanentTokens.Values.Sum(t => t.Count) >= Info.TotalCap)
				return false;

			return true;
		}

		public int GrantCondition(Actor self, object source, int duration = 0, int remaining = 0)
		{
			if (!CanGrantCondition(source))
				return Actor.InvalidConditionToken;

			this.self = self;
			var token = self.GrantCondition(Info.Condition);
			permanentTokens.TryGetValue(source, out var permanent);

			// Callers can override the amount of time remaining by passing a value
			// between 1 and the duration
			if (remaining <= 0 || remaining > duration)
				remaining = duration;

			if (duration > 0)
			{
				// Check level caps
				if (Info.SourceCap > 0)
				{
					var timedCount = timedTokens.Count(t => t.Source == source);
					if ((permanent?.Count ?? 0) + timedCount >= Info.SourceCap)
					{
						// Get timed token from the same source with closest expiration.
						var expireIndex = timedTokens.FindIndex(t => t.Source == source);
						if (expireIndex >= 0)
						{
							var expireToken = timedTokens[expireIndex].Token;
							timedTokens.RemoveAt(expireIndex);
							if (self.TokenValid(expireToken))
								self.RevokeCondition(expireToken);
						}
					}
				}

				if (Info.TotalCap > 0)
				{
					var totalCount = permanentTokens.Values.Sum(t => t.Count) + timedTokens.Count;
					if (totalCount >= Info.TotalCap && timedTokens.Count > 0)
					{
						var expire = timedTokens[0].Token;
						if (self.TokenValid(expire))
							self.RevokeCondition(expire);

						timedTokens.RemoveAt(0);
					}
				}

				var timedToken = new TimedToken(token, self, source, remaining);
				var index = timedTokens.FindIndex(t => t.Expires >= timedToken.Expires);
				if (index >= 0)
					timedTokens.Insert(index, timedToken);
				else
				{
					timedTokens.Add(timedToken);

					// Track the duration and expiration for the longest remaining timer.
					expires = timedToken.Expires;
					this.duration = duration;
				}

				// Join the per-world ticker so the new timer is counted down. Idle conditions never
				// register, which is the whole point - they stay out of any per-tick work.
				if (!registered)
				{
					registered = true;
					ExternalConditionTicker.ForWorld(self.World).Register(this);
				}
			}
			else if (permanent == null)
				permanentTokens.Add(source, [token]);
			else
				permanent.Add(token);

			return token;
		}

		/// <summary>Revokes the external condition with the given token if it was granted by this trait.</summary>
		/// <returns><c>true</c> if the now-revoked condition was originally granted by this trait.</returns>
		public bool TryRevokeCondition(Actor self, object source, int token)
		{
			if (source == null)
				return false;

			if (permanentTokens.TryGetValue(source, out var permanentTokensForSource))
			{
				if (!permanentTokensForSource.Remove(token))
					return false;
			}
			else
			{
				var index = timedTokens.FindIndex(p => p.Token == token);
				if (index >= 0 && timedTokens[index].Source == source)
					timedTokens.RemoveAt(index);
				else
					return false;
			}

			if (self.TokenValid(token))
				self.RevokeCondition(token);

			return true;
		}

		// Driven by ExternalConditionTicker while this trait holds timed tokens (it no longer ticks via
		// the global ITick loop). Returns true while there is still a timer to count down; false tells
		// the ticker to drop it from the active set until a new timed token is granted.
		public bool TickTimed()
		{
			if (removed || timedTokens.Count == 0)
			{
				registered = false;
				return false;
			}

			// Remove expired tokens
			var worldTick = self.World.WorldTick;
			var count = 0;
			while (count < timedTokens.Count && timedTokens[count].Expires < worldTick)
			{
				var token = timedTokens[count].Token;
				if (self.TokenValid(token))
					self.RevokeCondition(token);

				count++;
			}

			if (count > 0)
			{
				timedTokens.RemoveRange(0, count);
				if (timedTokens.Count == 0)
				{
					// Notify watchers that all timers have expired.
					foreach (var w in watchers)
						w.Update(0, 0);

					registered = false;
					return false;
				}
			}

			// Watchers will be receiving notifications while the condition is enabled.
			// They will also be provided with the number of ticks before the last timer ends,
			// as well as the duration of the longest active instance.
			var remaining = expires - worldTick;
			foreach (var w in watchers)
				w.Update(duration, remaining);

			return true;
		}

		bool Notifies(IConditionTimerWatcher watcher) { return watcher.Condition == Info.Condition; }

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			foreach (var pair in self.World.ActorsWithTrait<INotifyProximityOwnerChanged>())
				pair.Trait.OnProximityOwnerChanged(self, oldOwner, newOwner);
		}

		void INotifyCreated.Created(Actor self)
		{
			this.self = self;
			watchers = self.TraitsImplementing<IConditionTimerWatcher>().Where(Notifies).ToArray();
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			// An actor can be removed and re-added (e.g. ChangeOwner from mind-control/chaos/deviator,
			// or transport load/unload). RemovedFromWorld latches 'removed', which would otherwise stay
			// set forever and stop TickTimed from ever revoking the timer -> a permanent, never-expiring
			// condition. Clear it on re-entry, and re-join the ticker if a timer is still pending and we
			// were dropped from the active set while out of world.
			removed = false;
			if (!registered && timedTokens.Count > 0)
			{
				registered = true;
				ExternalConditionTicker.ForWorld(self.World).Register(this);
			}
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			removed = true;
		}
	}

	// One lazily-created instance per world, added to the effects collection so it ticks in the
	// existing effect phase without touching World.Tick. It only ever holds ExternalConditions that
	// currently have a live timed token, replacing the previous per-actor ITick visit to every
	// ExternalCondition in the world.
	sealed class ExternalConditionTicker : IEffect
	{
		static readonly ConditionalWeakTable<World, ExternalConditionTicker> Instances = new();

		readonly List<ExternalCondition> active = [];
		readonly List<ExternalCondition> queued = [];
		bool ticking;

		public static ExternalConditionTicker ForWorld(World world)
		{
			return Instances.GetValue(world, CreateAndAttach);
		}

		static ExternalConditionTicker CreateAndAttach(World world)
		{
			var ticker = new ExternalConditionTicker();

			// The first timed grant can happen from inside the effect phase (e.g. a detonating
			// projectile-warhead), where the effects collection is mid-enumeration. Defer the actual
			// Add to frame end so we never mutate that collection while it's being ticked. Conditions
			// registered before the ticker is attached just start counting down on the next frame.
			world.AddFrameEndTask(w => w.Add(ticker));
			return ticker;
		}

		public void Register(ExternalCondition condition)
		{
			// Defer adds made while iterating so the active list isn't mutated mid-sweep.
			(ticking ? queued : active).Add(condition);
		}

		void IEffect.Tick(World world)
		{
			if (active.Count > 0)
			{
				ticking = true;

				// Forward sweep with in-place compaction: keep conditions that still report work and
				// drop the rest. Each TickTimed only touches its own actor's conditions, so the visit
				// order has no effect on the simulation - deterministic and identical across clients.
				var write = 0;
				for (var read = 0; read < active.Count; read++)
				{
					var condition = active[read];
					if (condition.TickTimed())
						active[write++] = condition;
				}

				if (write < active.Count)
					active.RemoveRange(write, active.Count - write);

				ticking = false;
			}

			if (queued.Count > 0)
			{
				active.AddRange(queued);
				queued.Clear();
			}
		}

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer r) { yield break; }
	}
}
