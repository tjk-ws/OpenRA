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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OpenRA.Primitives;
using OpenRA.Support;

namespace OpenRA
{
	static class SpanExts
	{
		public static int BinarySearchMany(this Span<Actor> span, uint searchFor)
		{
			var start = 0;
			var end = span.Length;
			while (start != end)
			{
				var mid = (start + end) / 2;
				if (span[mid].ActorID < searchFor)
					start = mid + 1;
				else
					end = mid;
			}

			return start;
		}
	}

	/// <summary>
	/// Provides efficient ways to query a set of actors by their traits.
	/// </summary>
	sealed class TraitDictionary
	{
		static readonly Func<Type, ITraitContainer> CreateTraitContainer = t =>
			(ITraitContainer)typeof(TraitContainer<>).MakeGenericType(t).GetConstructor(Type.EmptyTypes).Invoke(null);

		readonly Dictionary<Type, ITraitContainer> traits = [];

		ITraitContainer InnerGet(Type t)
		{
			return traits.GetOrAdd(t, CreateTraitContainer);
		}

		TraitContainer<T> InnerGet<T>()
		{
			return (TraitContainer<T>)InnerGet(typeof(T));
		}

		public void PrintReport()
		{
			Log.AddChannel("traitreport", "traitreport.log");
			foreach (var t in traits.OrderByDescending(t => t.Value.Queries).TakeWhile(t => t.Value.Queries > 0))
				Log.Write("traitreport", $"{t.Key.Name}: {t.Value.Queries}");
		}

		public void AddTrait(Actor actor, object val)
		{
			var t = val.GetType();

			foreach (var i in t.GetInterfaces())
				InnerAdd(actor, i, val);
			foreach (var tt in t.BaseTypes())
				InnerAdd(actor, tt, val);
		}

		void InnerAdd(Actor actor, Type t, object val)
		{
			InnerGet(t).Add(actor, val);
		}

		static void CheckDestroyed(Actor actor)
		{
			if (actor.Disposed)
				throw new InvalidOperationException($"Attempted to get trait from destroyed object ({actor})");
		}

		public T Get<T>(Actor actor)
		{
			CheckDestroyed(actor);
			return InnerGet<T>().Get(actor);
		}

		public T GetOrDefault<T>(Actor actor)
		{
			CheckDestroyed(actor);
			return InnerGet<T>().GetOrDefault(actor);
		}

		public IEnumerable<T> WithInterface<T>(Actor actor)
		{
			CheckDestroyed(actor);
			return InnerGet<T>().GetMultiple(actor.ActorID);
		}

		public IEnumerable<TraitPair<T>> ActorsWithTrait<T>()
		{
			return InnerGet<T>().All();
		}

		public IEnumerable<Actor> ActorsHavingTrait<T>()
		{
			return InnerGet<T>().Actors();
		}

		public IEnumerable<Actor> ActorsHavingTrait<T>(Func<T, bool> predicate)
		{
			return InnerGet<T>().Actors(predicate);
		}

		public void RemoveActor(Actor a)
		{
			foreach (var t in traits)
				t.Value.RemoveActor(a.ActorID);
		}

		public void ApplyToActorsWithTrait<T>(Action<Actor, T> action)
		{
			InnerGet<T>().ApplyToAll(action);
		}

		public void ApplyToActorsWithTraitTimed<T>(Action<Actor, T> action, string text)
		{
			InnerGet<T>().ApplyToAllTimed(action, text);
		}

		interface ITraitContainer
		{
			void Add(Actor actor, object trait);
			void RemoveActor(uint actor);

			int Queries { get; }
		}

		sealed class TraitContainer<T> : ITraitContainer
		{
			readonly List<Actor> actors = [];
			readonly List<T> traits = [];

			// Decoupled rendering: actors are added/removed on the SIM thread (spawn/death during the
			// world tick) while the MAIN thread enumerates this container for UI (ActorsWithTrait / TraitsImplementing)
			// and reads individual elements through unlocked Draw/UI paths. Both the structural writes and every read
			// take this lock: the whole-list enumerations snapshot under it, and the per-element Get/GetOrDefault/
			// GetMultiple lock too (CollectionsMarshal.AsSpan over a List being resized on another thread is undefined).
			// Uncontended in the single-threaded (decoupling-off) case.
			readonly object sync = new();

			public int Queries { get; private set; }

			public void Add(Actor actor, object trait)
			{
				lock (sync)
				{
					var actorsSpan = CollectionsMarshal.AsSpan(actors);
					var insertIndex = actorsSpan.BinarySearchMany(actor.ActorID + 1);
					actors.Insert(insertIndex, actor);
					traits.Insert(insertIndex, (T)trait);
				}
			}

			public T Get(Actor actor)
			{
				var result = GetOrDefault(actor);
				if (result == null)
					throw new InvalidOperationException($"Actor {actor.Info.Name} does not have trait of type `{typeof(T)}`");

				return result;
			}

			public T GetOrDefault(Actor actor)
			{
				// Decoupled rendering: the sim thread can structurally mutate actors/traits (Add/RemoveActor) while the MAIN
				// thread reads here via unlocked UI/Draw paths. CollectionsMarshal.AsSpan over a List being resized
				// on another thread is undefined (the span can dangle past a backing-array swap, or read mismatched
				// length/index pairs). So the per-element reads must take the same lock as the writes. (Under the
				// world lock both prepare and the sim tick already exclude each other; this lock covers the
				// genuinely-concurrent unlocked-Draw/UI callers. Uncontended in the single-threaded case.)
				lock (sync)
				{
					++Queries;
					var actorsSpan = CollectionsMarshal.AsSpan(actors);
					var index = actorsSpan.BinarySearchMany(actor.ActorID);
					if (index >= actorsSpan.Length || actorsSpan[index] != actor)
						return default;

					if (index + 1 < actorsSpan.Length && actorsSpan[index + 1] == actor)
						throw new InvalidOperationException($"Actor {actor.Info.Name} has multiple traits of type `{typeof(T)}`");

					return traits[index];
				}
			}

			public IEnumerable<T> GetMultiple(uint actor)
			{
				// Snapshot the matching traits under the lock (was a lazy custom enumerator over the live lists,
				// which is unsafe once another thread can structurally mutate them - see GetOrDefault).
				lock (sync)
				{
					++Queries;
					var result = new List<T>();
					var actorsSpan = CollectionsMarshal.AsSpan(actors);
					var index = actorsSpan.BinarySearchMany(actor);
					while (index < actorsSpan.Length && actorsSpan[index].ActorID == actor)
					{
						result.Add(traits[index]);
						index++;
					}

					return result;
				}
			}

			public IEnumerable<TraitPair<T>> All()
			{
				// Snapshot under the lock: a concurrent sim-thread Add/RemoveActor must not be able to modify the
				// lists while a (possibly main-thread) caller iterates them. The custom struct enumerator was used
				// for speed when this was single-threaded; the materialised copy is the cost of thread safety.
				++Queries;
				lock (sync)
				{
					var result = new List<TraitPair<T>>(actors.Count);
					for (var i = 0; i < actors.Count; i++)
						result.Add(new TraitPair<T>(actors[i], traits[i]));
					return result;
				}
			}

			public IEnumerable<Actor> Actors()
			{
				++Queries;
				lock (sync)
				{
					var result = new List<Actor>();
					Actor last = null;
					for (var i = 0; i < actors.Count; i++)
					{
						var current = actors[i];
						if (current == last)
							continue;

						result.Add(current);
						last = current;
					}

					return result;
				}
			}

			public IEnumerable<Actor> Actors(Func<T, bool> predicate)
			{
				++Queries;

				// Snapshot under the lock, then run the caller-supplied predicate OUTSIDE it: the predicate is
				// arbitrary code that could enter another trait container, so holding this lock across it would risk
				// an ABBA deadlock between two containers across the sim/main threads.
				Actor[] actorsCopy;
				T[] traitsCopy;
				lock (sync)
				{
					actorsCopy = actors.ToArray();
					traitsCopy = traits.ToArray();
				}

				var result = new List<Actor>();
				Actor last = null;
				for (var i = 0; i < actorsCopy.Length; i++)
				{
					var current = actorsCopy[i];
					if (current == last || !predicate(traitsCopy[i]))
						continue;

					result.Add(current);
					last = current;
				}

				return result;
			}

			readonly struct AllEnumerable : IEnumerable<TraitPair<T>>
			{
				readonly TraitContainer<T> container;
				public AllEnumerable(TraitContainer<T> container) { this.container = container; }
				public IEnumerator<TraitPair<T>> GetEnumerator() { return new AllEnumerator(container); }
				System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
			}

			struct AllEnumerator : IEnumerator<TraitPair<T>>
			{
				readonly List<Actor> actors;
				readonly List<T> traits;
				int index;
				public AllEnumerator(TraitContainer<T> container)
					: this()
				{
					actors = container.actors;
					traits = container.traits;
					Reset();
				}

				public void Reset() { index = -1; }
				public bool MoveNext() { return ++index < actors.Count; }
				public readonly TraitPair<T> Current => new(actors[index], traits[index]);
				readonly object System.Collections.IEnumerator.Current => Current;
				public readonly void Dispose() { }
			}

			public void RemoveActor(uint actor)
			{
				lock (sync)
				{
					var actorsSpan = CollectionsMarshal.AsSpan(actors);
					var startIndex = actorsSpan.BinarySearchMany(actor);
					if (startIndex >= actorsSpan.Length || actorsSpan[startIndex].ActorID != actor)
						return;

					var endIndex = startIndex + 1;
					while (endIndex < actorsSpan.Length && actorsSpan[endIndex].ActorID == actor)
						endIndex++;

					var count = endIndex - startIndex;
					actors.RemoveRange(startIndex, count);
					traits.RemoveRange(startIndex, count);
				}
			}

			public void ApplyToAll(Action<Actor, T> action)
			{
				// Decoupled rendering: callers include the UNLOCKED render Draw (IRenderAboveWorld / IRenderShroud), which can
				// run while the sim thread structurally mutates actors/traits. Snapshot the pair list under the lock,
				// then invoke actions outside it (so the callbacks - which issue GL - don't hold the lock). The sim's
				// hot ITick path uses ApplyToAllTimed (same thread as the writes), so it stays lock-free below.
				Actor[] actorsCopy;
				T[] traitsCopy;
				lock (sync)
				{
					actorsCopy = actors.ToArray();
					traitsCopy = traits.ToArray();
				}

				for (var i = 0; i < actorsCopy.Length; i++)
					action(actorsCopy[i], traitsCopy[i]);
			}

			public void ApplyToAllTimed(Action<Actor, T> action, string text)
			{
				var start = PerfTickLogger.GetTimestamp();
				var actorsSpan = CollectionsMarshal.AsSpan(actors);
				var traitsSpan = CollectionsMarshal.AsSpan(traits);

				for (var i = 0; i < actorsSpan.Length; i++)
				{
					var actor = actorsSpan[i];
					var trait = traitsSpan[i];
					action(actor, trait);

					start = PerfTickLogger.LogLongTick(start, text, trait);
				}
			}
		}
	}
}
