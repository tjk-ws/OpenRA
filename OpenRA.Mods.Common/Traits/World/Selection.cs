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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class SelectionInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new Selection(); }
	}

	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	public class Selection : ISelection, INotifyCreated, INotifyOwnerChanged, ITick, IGameSaveTraitData
	{
		public int Hash { get; private set; }

		// Decoupled rendering: the sim thread prunes the selection every tick (ITick.Tick RemoveWhere:
		// dead/fogged actors) while the main thread mutates it from input (Add/Remove/Combine/Clear) and widgets
		// enumerate it every frame. Guard the set itself; Actors hands out a snapshot so no caller ever iterates
		// the live set. Notification callbacks run OUTSIDE the lock (they call arbitrary order-generator/widget
		// code). Uncontended in the single-threaded case.
		public IReadOnlyCollection<Actor> Actors
		{
			get
			{
				lock (syncRoot)
					return actors.ToArray();
			}
		}

		readonly object syncRoot = new();
		readonly HashSet<Actor> actors = [];
		readonly List<Actor> rolloverActors = [];
		World world;

		INotifySelection[] worldNotifySelection;

		void INotifyCreated.Created(Actor self)
		{
			worldNotifySelection = self.TraitsImplementing<INotifySelection>().ToArray();
			world = self.World;
		}

		void UpdateHash()
		{
			// Not a real hash, but things checking this only care about checking when the selection has changed
			// For this purpose, having a false positive (forcing a refresh when nothing changed) is much better
			// than a false negative (selection state mismatch)
			Hash++;
		}

		// The live set must never leak to callbacks (they enumerate outside the lock), so every notification
		// passes a snapshot captured under the lock alongside the mutation it reports.
		void NotifySelectionChanged(Actor[] snapshot)
		{
			// Decoupled rendering: the SIM-thread ITick.Tick prune is the only off-main caller, and its
			// callbacks run main-thread-only client/UI code - order-generator SelectionChanged (e.g. Minelayer mutates
			// an in-place List) and INotifySelection widget mutators (e.g. ProductionQueueFromSelection touches
			// Ui.Root / the production widget). Marshal to the main thread so none of that runs on the sim thread.
			// Everything here is client-side (selection is local; the order-gen call is RunUnsynced) -> not a desync.
			// Input callers (Add/Remove/Combine/Clear) are already on the main thread -> inline, unchanged.
			if (Game.IsOnMainThread)
			{
				DoNotifySelectionChanged(snapshot);
				return;
			}

			// Harden the deferred (sim-prune) run:
			//  - the callbacks read LIVE world state (order-generator SelectionChanged; INotifySelection, e.g.
			//    ProductionQueueFromSelection enumerates the live ProductionQueue), so take the blocking
			//    WorldAccessLock - PerformDelayedActions drains on the main thread WITHOUT the world lock and can run
			//    concurrently with a mid-flight sim tick.
			//  - re-read the CURRENT selection at execution rather than replaying the prune-time `snapshot`, so a
			//    stale snapshot can't overwrite a newer input selection made between the prune and this deferred run.
			Game.RunAfterTick(() =>
			{
				Game.EnterWorldReadLock();
				try
				{
					Actor[] current;
					lock (syncRoot)
						current = actors.ToArray();

					DoNotifySelectionChanged(current);
				}
				finally
				{
					Game.ExitWorldReadLock();
				}
			});
		}

		void DoNotifySelectionChanged(Actor[] snapshot)
		{
			Sync.RunUnsynced(world, () => world.OrderGenerator.SelectionChanged(world, snapshot));
			foreach (var ns in worldNotifySelection)
				ns.SelectionChanged();
		}

		public virtual void Add(Actor a)
		{
			Actor[] snapshot;
			lock (syncRoot)
			{
				actors.Add(a);
				UpdateHash();
				snapshot = actors.ToArray();
			}

			foreach (var sel in a.TraitsImplementing<INotifySelected>())
				sel.Selected(a);

			NotifySelectionChanged(snapshot);
		}

		public virtual void Remove(Actor a)
		{
			Actor[] snapshot = null;
			lock (syncRoot)
			{
				if (actors.Remove(a))
				{
					UpdateHash();
					snapshot = actors.ToArray();
				}
			}

			if (snapshot != null)
				NotifySelectionChanged(snapshot);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor a, Player oldOwner, Player newOwner)
		{
			if (!Contains(a))
				return;

			// Remove the actor from the original owners selection
			// Call UpdateHash directly for everyone else so watchers can account for the owner change if needed
			if (oldOwner == world.LocalPlayer)
				Remove(a);
			else
				lock (syncRoot)
					UpdateHash();
		}

		public bool Contains(Actor a)
		{
			lock (syncRoot)
				return actors.Contains(a);
		}

		public virtual void Combine(World world, IEnumerable<Actor> newSelection, bool isCombine, bool isClick)
		{
			var newSelectionCollection = newSelection as IReadOnlyCollection<Actor>;
			newSelectionCollection ??= newSelection.ToList();

			Actor[] snapshot;
			lock (syncRoot)
			{
				if (isClick)
				{
					// TODO: select BEST, not FIRST
					var adjNewSelection = newSelectionCollection.Take(1);
					if (isCombine)
						actors.SymmetricExceptWith(adjNewSelection);
					else
					{
						actors.Clear();
						actors.UnionWith(adjNewSelection);
					}
				}
				else
				{
					if (isCombine)
						actors.UnionWith(newSelectionCollection);
					else
					{
						actors.Clear();
						actors.UnionWith(newSelectionCollection);
					}
				}

				UpdateHash();
				snapshot = actors.ToArray();
			}

			foreach (var a in newSelectionCollection)
				foreach (var sel in a.TraitsImplementing<INotifySelected>())
					sel.Selected(a);

			NotifySelectionChanged(snapshot);

			if (world.IsGameOver)
				return;

			// Play the selection voice from one of the selected actors
			foreach (var actor in snapshot.Intersect(newSelectionCollection))
			{
				if (actor.Owner != world.LocalPlayer || !actor.IsInWorld)
					continue;

				var selectable = actor.Info.TraitInfoOrDefault<ISelectableInfo>();
				if (selectable == null || !actor.HasVoice(selectable.Voice))
					continue;

				actor.PlayVoice(selectable.Voice);
				break;
			}
		}

		public void Clear()
		{
			Actor[] snapshot;
			lock (syncRoot)
			{
				actors.Clear();
				UpdateHash();
				snapshot = [];
			}

			NotifySelectionChanged(snapshot);
		}

		public void SetRollover(IEnumerable<Actor> rollover)
		{
			lock (syncRoot)
			{
				rolloverActors.Clear();
				rolloverActors.AddRange(rollover);
			}
		}

		public bool RolloverContains(Actor a)
		{
			lock (syncRoot)
				return rolloverActors.Contains(a);
		}

		void ITick.Tick(Actor self)
		{
			Actor[] snapshot = null;
			lock (syncRoot)
			{
				var removed = actors.RemoveWhere(a => !a.IsInWorld || (!a.Owner.IsAlliedWith(world.RenderPlayer) && world.FogObscures(a)));
				if (removed > 0)
				{
					UpdateHash();
					snapshot = actors.ToArray();
				}
			}

			if (snapshot != null)
				NotifySelectionChanged(snapshot);
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			return
			[
				new("Selection", FieldSaver.FormatValue(Actors.Select(a => a.ActorID).ToArray()))
			];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			var selectionNode = data.NodeWithKeyOrDefault("Selection");
			if (selectionNode != null)
			{
				var selected = FieldLoader.GetValue<uint[]>("Selection", selectionNode.Value.Value)
					.Select(self.World.GetActorById).Where(a => a != null);
				Combine(self.World, selected, false, false);
			}
		}
	}
}
