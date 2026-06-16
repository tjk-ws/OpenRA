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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	public class RadarPingsInfo : TraitInfo
	{
		public readonly int FromRadius = 200;
		public readonly int ToRadius = 15;
		public readonly int ShrinkSpeed = 4;
		public readonly float RotationSpeed = 0.12f;

		public override object Create(ActorInitializer init) { return new RadarPings(this); }
	}

	public class RadarPings : ITick
	{
		public readonly List<RadarPing> Pings = [];
		readonly RadarPingsInfo info;

		// Decoupled rendering: the sim thread ticks/adds/removes pings while the main thread's
		// RadarWidget draws them. Guard the list so the UI never iterates it mid-modification; the UI reads
		// via PingsSnapshot(). Uncontended in the single-threaded case.
		readonly object syncRoot = new();

		// Decoupled rendering: written by the sim thread (Add) and read by the main thread (jump-to-last-event hotkey).
		// WPos? is a multi-field struct, so a direct cross-thread read can tear; guard it with syncRoot and expose
		// only the locked snapshot accessor below.
		WPos? lastPingPosition;

		public RadarPings(RadarPingsInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			lock (syncRoot)
			{
				for (var i = Pings.Count - 1; i >= 0; i--)
					if (!Pings[i].Tick())
						Pings.RemoveAt(i);
			}
		}

		public RadarPing Add(Func<bool> isVisible, WPos position, Color color, int duration)
		{
			var ping = new RadarPing(isVisible, position, color, duration,
				info.FromRadius, info.ToRadius, info.ShrinkSpeed, info.RotationSpeed);

			var visible = ping.IsVisible();
			lock (syncRoot)
			{
				if (visible)
					lastPingPosition = ping.Position;

				Pings.Add(ping);
			}

			return ping;
		}

		public void Remove(RadarPing ping)
		{
			lock (syncRoot)
				Pings.Remove(ping);
		}

		// Thread-safe snapshot for the UI/render thread.
		public RadarPing[] PingsSnapshot()
		{
			lock (syncRoot)
				return Pings.ToArray();
		}

		// Thread-safe single read of the last ping position for the UI/render thread.
		public WPos? LastPingPositionSnapshot()
		{
			lock (syncRoot)
				return lastPingPosition;
		}
	}

	public class RadarPing
	{
		public Func<bool> IsVisible;
		public WPos Position;
		public Color Color;
		public int Duration;
		public int FromRadius;
		public int ToRadius;
		public int ShrinkSpeed;
		public float RotationSpeed;

		int radius;
		float angle;
		int tick;

		public RadarPing(Func<bool> isVisible, WPos position, Color color, int duration,
			int fromRadius, int toRadius, int shrinkSpeed, float rotationSpeed)
		{
			IsVisible = isVisible;
			Position = position;
			Color = color;
			Duration = duration;
			FromRadius = fromRadius;
			ToRadius = toRadius;
			ShrinkSpeed = shrinkSpeed;
			RotationSpeed = rotationSpeed;

			radius = fromRadius;
		}

		public bool Tick()
		{
			if (++tick == Duration)
				return false;

			radius = Math.Max(radius - ShrinkSpeed, ToRadius);
			angle -= RotationSpeed;
			return true;
		}

		public IEnumerable<float2> Points(float2 center)
		{
			yield return center + radius * float2.FromAngle(angle);
			yield return center + radius * float2.FromAngle((float)(angle + 2 * Math.PI / 3));
			yield return center + radius * float2.FromAngle((float)(angle + 4 * Math.PI / 3));
		}
	}
}
