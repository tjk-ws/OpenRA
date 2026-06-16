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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets
{
	public class ObserverSupportPowerIconsWidget : Widget
	{
		public readonly string TooltipTemplate = "SUPPORT_POWER_TOOLTIP";
		public readonly string TooltipContainer;
		readonly World world;
		readonly WorldRenderer worldRenderer;
		readonly Dictionary<string, Animation> clocks;

		readonly Lazy<TooltipContainerWidget> tooltipContainer;

		public Func<SupportPowersWidget.SupportPowerIcon> GetTooltipIcon;
		public SupportPowersWidget.SupportPowerIcon TooltipIcon { get; private set; }

		public int IconWidth = 32;
		public int IconHeight = 24;
		public int IconSpacing = 1;

		public string ClockAnimation = "clock";
		public string ClockSequence = "idle";
		public string ClockPalette = "chrome";
		public Func<Player> GetPlayer;

		readonly List<SupportPowersWidget.SupportPowerIcon> supportPowerIconsIcons = [];
		readonly List<Rectangle> supportPowerIconsBounds = [];
		Animation icon;
		int lastIconIdx;
		int currentTooltipToken;

		// Decoupled rendering: the live power state is snapshotted under the world read lock at the top
		// of Draw (Powers dictionary enumeration, Info - which walks the live Instances list - and GetLevel are
		// all unsafe against a concurrent sim tick). The GL loop renders from this cache plus scalar field reads
		// (RemainingTicks/TotalTicks/Disabled/Ready are plain fields/bools - safe). If the sim holds the lock we
		// render last frame's snapshot.
		readonly List<(SupportPowerInstance Power, string Key, SupportPowerInfo Info, int Level)> cachedPowers = [];

		[ObjectCreator.UseCtor]
		public ObserverSupportPowerIconsWidget(World world, WorldRenderer worldRenderer)
		{
			this.world = world;
			this.worldRenderer = worldRenderer;
			clocks = [];

			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		protected ObserverSupportPowerIconsWidget(ObserverSupportPowerIconsWidget other)
			: base(other)
		{
			GetPlayer = other.GetPlayer;
			icon = other.icon;
			world = other.world;
			worldRenderer = other.worldRenderer;
			clocks = other.clocks;

			IconWidth = other.IconWidth;
			IconHeight = other.IconHeight;
			IconSpacing = other.IconSpacing;

			ClockAnimation = other.ClockAnimation;
			ClockSequence = other.ClockSequence;
			ClockPalette = other.ClockPalette;

			TooltipIcon = other.TooltipIcon;
			GetTooltipIcon = () => TooltipIcon;

			TooltipTemplate = other.TooltipTemplate;
			TooltipContainer = other.TooltipContainer;

			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		public override void Draw()
		{
			supportPowerIconsIcons.Clear();
			supportPowerIconsBounds.Clear();

			var player = GetPlayer();
			if (player == null)
				return;

			// Snapshot the live power state under the world read lock (see cachedPowers); keep last frame's
			// snapshot if the sim thread is mid-tick.
			if (Game.TryEnterWorldReadLock())
			{
				try
				{
					cachedPowers.Clear();
					foreach (var kv in player.PlayerActor.Trait<SupportPowerManager>().Powers
						.Where(x => !x.Value.Disabled && x.Value.GetLevel() != 0)
						.OrderBy(p => p.Value.Info.SupportPowerPaletteOrder))
						cachedPowers.Add((kv.Value, kv.Key, kv.Value.Info, kv.Value.GetLevel()));
				}
				finally
				{
					Game.ExitWorldReadLock();
				}
			}

			foreach (var power in cachedPowers)
			{
				if (!clocks.ContainsKey(power.Key))
					clocks.Add(power.Key, new Animation(world, ClockAnimation));
			}

			Bounds.Width = cachedPowers.Count * (IconWidth + IconSpacing);

			Game.Renderer.EnableAntialiasingFilter();

			var iconSize = new float2(IconWidth, IconHeight);
			var iconIdx = 0;
			foreach (var power in cachedPowers)
			{
				var idx = iconIdx++;
				var item = power.Power;
				if (item == null || power.Info == null || power.Info.Icons == null)
					continue;

				var level = power.Level;
				icon = new Animation(worldRenderer.World, power.Info.IconImage);
				icon.Play(power.Info.Icons.First(i => i.Key == level).Value);
				var location = new float2(RenderBounds.Location) + new float2(idx * (IconWidth + IconSpacing), 0);

				supportPowerIconsIcons.Add(new SupportPowersWidget.SupportPowerIcon { Power = item, Pos = location });
				supportPowerIconsBounds.Add(new Rectangle((int)location.X, (int)location.Y, (int)iconSize.X, (int)iconSize.Y));

				WidgetUtils.DrawSpriteCentered(icon.Image, worldRenderer.Palette(power.Info.IconPalette), location + 0.5f * iconSize, 0.5f);

				var clock = clocks[power.Key];
				clock.PlayFetchIndex(ClockSequence,
					() => item.TotalTicks == 0 ? 0 : ((item.TotalTicks - item.RemainingTicks)
						* (clock.CurrentSequence.Length - 1) / item.TotalTicks));
				clock.Tick();
				WidgetUtils.DrawSpriteCentered(clock.Image, worldRenderer.Palette(ClockPalette), location + 0.5f * iconSize, 0.5f);
			}

			Game.Renderer.DisableAntialiasingFilter();

			var tiny = Game.Renderer.Fonts["Tiny"];
			foreach (var icon in supportPowerIconsIcons)
			{
				var text = GetOverlayForItem(icon.Power, world.Timestep);
				tiny.DrawTextWithContrast(text,
					icon.Pos + new float2(16, 12) - new float2(tiny.Measure(text).X / 2, 0),
					Color.White, Color.Black, 1);
			}
		}

		static string GetOverlayForItem(SupportPowerInstance item, int timestep)
		{
			if (item.Disabled) return "ON HOLD";
			if (item.Ready) return "READY";
			return WidgetUtils.FormatTime(item.RemainingTicks, timestep);
		}

		public override ObserverSupportPowerIconsWidget Clone()
		{
			return new ObserverSupportPowerIconsWidget(this);
		}

		public override void Tick()
		{
			if (TooltipContainer == null)
				return;

			if (Ui.MouseOverWidget != this)
			{
				if (TooltipIcon != null)
				{
					tooltipContainer.Value.RemoveTooltip(currentTooltipToken);
					lastIconIdx = 0;
					TooltipIcon = null;
				}

				return;
			}

			if (TooltipIcon != null &&
				lastIconIdx < supportPowerIconsBounds.Count &&
				supportPowerIconsIcons[lastIconIdx].Power == TooltipIcon.Power &&
				supportPowerIconsBounds[lastIconIdx].Contains(Viewport.LastMousePos))
				return;

			for (var i = 0; i < supportPowerIconsBounds.Count; i++)
			{
				if (!supportPowerIconsBounds[i].Contains(Viewport.LastMousePos))
					continue;

				lastIconIdx = i;
				TooltipIcon = supportPowerIconsIcons[i];
				currentTooltipToken = tooltipContainer.Value.SetTooltip(TooltipTemplate,
					new WidgetArgs()
					{
						{ "world", worldRenderer.World },
						{ "player", GetPlayer() },
						{ "getTooltipIcon", GetTooltipIcon },
						{ "playerResources", GetPlayer().PlayerActor.Trait<PlayerResources>() }
					});
				return;
			}

			TooltipIcon = null;
		}
	}
}
