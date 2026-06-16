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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenRA.Primitives
{
	public sealed class SpatiallyPartitioned<T> : IDictionary<T, Rectangle>
	{
		static readonly Action<Dictionary<T, Rectangle>, T, Rectangle> AddItem = (bin, item, bounds) => bin.Add(item, bounds);
		static readonly Action<Dictionary<T, Rectangle>, T, Rectangle> RemoveItem = (bin, item, bounds) => bin.Remove(item);

		readonly int rows, cols, binSize;
		readonly Dictionary<T, Rectangle>[] itemBoundsBins;
		readonly Dictionary<T, Rectangle> itemBounds = [];

		// Decoupled rendering: ScreenMap (and other users of this type) are mutated on the sim thread as
		// actors move and READ on the main thread for mouse picking / culling. Without synchronisation those
		// concurrent reads iterate a Dictionary while it is being structurally modified -> "Collection was
		// modified". This type is render/UI-side only (NOT the sim's ActorMap), so a coarse lock here is cheap
		// (uncontended in the single-threaded case) and keeps all access consistent. The spatial queries
		// materialise their results under the lock instead of yielding lazily, so iteration never escapes it.
		readonly object sync = new();

		public SpatiallyPartitioned(int width, int height, int binSize)
		{
			this.binSize = binSize;
			rows = Exts.IntegerDivisionRoundingAwayFromZero(height, binSize);
			cols = Exts.IntegerDivisionRoundingAwayFromZero(width, binSize);
			itemBoundsBins = Exts.MakeArray(rows * cols, _ => new Dictionary<T, Rectangle>());
		}

		static void ValidateBounds(T item, Rectangle bounds)
		{
			if (bounds.Width == 0 || bounds.Height == 0)
				throw new ArgumentException($"Bounds of {item} are empty.", nameof(bounds));
		}

		public void Add(T item, Rectangle bounds)
		{
			ValidateBounds(item, bounds);
			lock (sync)
			{
				itemBounds.Add(item, bounds);
				MutateBins(item, bounds, AddItem);
			}
		}

		public Rectangle this[T item]
		{
			get { lock (sync) return itemBounds[item]; }
			set
			{
				ValidateBounds(item, value);

				lock (sync)
				{
					// SAFETY: Dictionary cannot be modified whilst the ref is alive.
					ref var bounds = ref CollectionsMarshal.GetValueRefOrAddDefault(itemBounds, item, out var exists);
					if (exists)
						MutateBins(item, bounds, RemoveItem);
					MutateBins(item, bounds = value, AddItem);
				}
			}
		}

		public bool Remove(T item)
		{
			lock (sync)
			{
				if (!itemBounds.Remove(item, out var bounds))
					return false;

				MutateBins(item, bounds, RemoveItem);
				return true;
			}
		}

		Dictionary<T, Rectangle> BinAt(int row, int col)
		{
			return itemBoundsBins[row * cols + col];
		}

		Rectangle BinBounds(int row, int col)
		{
			return new Rectangle(col * binSize, row * binSize, binSize, binSize);
		}

		void BoundsToBinRowsAndCols(Rectangle bounds, out int minRow, out int maxRow, out int minCol, out int maxCol)
		{
			var top = Math.Min(bounds.Top, bounds.Bottom);
			var bottom = Math.Max(bounds.Top, bounds.Bottom);
			var left = Math.Min(bounds.Left, bounds.Right);
			var right = Math.Max(bounds.Left, bounds.Right);

			minRow = Math.Max(0, top / binSize);
			minCol = Math.Max(0, left / binSize);
			maxRow = Math.Min(rows, Exts.IntegerDivisionRoundingAwayFromZero(bottom, binSize));
			maxCol = Math.Min(cols, Exts.IntegerDivisionRoundingAwayFromZero(right, binSize));
		}

		void MutateBins(T item, Rectangle bounds, Action<Dictionary<T, Rectangle>, T, Rectangle> action)
		{
			BoundsToBinRowsAndCols(bounds, out var minRow, out var maxRow, out var minCol, out var maxCol);

			for (var row = minRow; row < maxRow; row++)
				for (var col = minCol; col < maxCol; col++)
					action(BinAt(row, col), item, bounds);
		}

		public IEnumerable<T> At(int2 location)
		{
			var col = (location.X / binSize).Clamp(0, cols - 1);
			var row = (location.Y / binSize).Clamp(0, rows - 1);

			// Materialise under the lock so the caller never iterates the live bin (see note on 'sync').
			var result = new List<T>();
			lock (sync)
				foreach (var kvp in BinAt(row, col))
					if (kvp.Value.Contains(location))
						result.Add(kvp.Key);

			return result;
		}

		public IEnumerable<T> InBox(Rectangle box)
		{
			BoundsToBinRowsAndCols(box, out var minRow, out var maxRow, out var minCol, out var maxCol);

			// We want to return any items intersecting the box.
			// If the box covers multiple bins, we must handle items that are contained in multiple bins and avoid
			// returning them more than once. We shall use a set to track these.
			// PERF: If we are only looking inside one bin, we can avoid the cost of performing this tracking.
			var items = minRow >= maxRow || minCol >= maxCol ? null : new HashSet<T>();

			// Materialise under the lock so the caller never iterates the live bins (see note on 'sync').
			var result = new List<T>();
			lock (sync)
			{
				for (var row = minRow; row < maxRow; row++)
					for (var col = minCol; col < maxCol; col++)
					{
						var binBounds = BinBounds(row, col);
						foreach (var kvp in BinAt(row, col))
						{
							var item = kvp.Key;
							var bounds = kvp.Value;

							// If the item is in the bin, we must check it intersects the box before returning it.
							// We shall track it in the set of items seen so far to avoid returning it again if it appears
							// in another bin.
							// PERF: If the item is wholly contained within the bin, we can avoid the cost of tracking it.
							if (bounds.IntersectsWith(box) &&
								(items == null || binBounds.Contains(bounds) || items.Add(item)))
								result.Add(item);
						}
					}
			}

			return result;
		}

		public void Clear()
		{
			lock (sync)
			{
				itemBounds.Clear();
				foreach (var bin in itemBoundsBins)
					bin.Clear();
			}
		}

		public ICollection<T> Keys { get { lock (sync) return itemBounds.Keys.ToArray(); } }
		public ICollection<Rectangle> Values { get { lock (sync) return itemBounds.Values.ToArray(); } }
		public int Count { get { lock (sync) return itemBounds.Count; } }
		public bool ContainsKey(T item) { lock (sync) return itemBounds.ContainsKey(item); }
		public bool TryGetValue(T key, out Rectangle value) { lock (sync) return itemBounds.TryGetValue(key, out value); }
		public IEnumerator<KeyValuePair<T, Rectangle>> GetEnumerator()
		{
			// Snapshot under the lock so callers iterating all items don't race a concurrent mutation.
			List<KeyValuePair<T, Rectangle>> snapshot;
			lock (sync)
				snapshot = new List<KeyValuePair<T, Rectangle>>(itemBounds);
			return snapshot.GetEnumerator();
		}

		bool ICollection<KeyValuePair<T, Rectangle>>.IsReadOnly => false;
		void ICollection<KeyValuePair<T, Rectangle>>.Add(KeyValuePair<T, Rectangle> item)
		{
			lock (sync)
				((ICollection<KeyValuePair<T, Rectangle>>)itemBounds).Add(item);
		}

		bool ICollection<KeyValuePair<T, Rectangle>>.Contains(KeyValuePair<T, Rectangle> item)
		{
			lock (sync)
				return ((ICollection<KeyValuePair<T, Rectangle>>)itemBounds).Contains(item);
		}

		void ICollection<KeyValuePair<T, Rectangle>>.CopyTo(KeyValuePair<T, Rectangle>[] array, int arrayIndex)
		{
			lock (sync)
				((ICollection<KeyValuePair<T, Rectangle>>)itemBounds).CopyTo(array, arrayIndex);
		}

		bool ICollection<KeyValuePair<T, Rectangle>>.Remove(KeyValuePair<T, Rectangle> item)
		{
			lock (sync)
				return ((ICollection<KeyValuePair<T, Rectangle>>)itemBounds).Remove(item);
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
