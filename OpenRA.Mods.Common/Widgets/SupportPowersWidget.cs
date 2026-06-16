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
using OpenRA.Mods.Common.Lint;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets
{
	public class SupportPowersWidget : Widget
	{
		[FluentReference]
		public string ReadyText = "";

		[FluentReference]
		public string HoldText = "";

		public readonly string OverlayFont = "TinyBold";

		public readonly int2 IconSize = new(64, 48);
		public readonly int IconMargin = 10;
		public readonly int2 IconSpriteOffset = int2.Zero;

		public readonly string TooltipContainer;
		public readonly string TooltipTemplate = "SUPPORT_POWER_TOOLTIP";

		// Note: LinterHotkeyNames assumes that these are disabled by default
		public readonly string HotkeyPrefix = null;
		public readonly int HotkeyCount = 0;

		public readonly string ClockAnimation = "clock";
		public readonly string ClockSequence = "idle";
		public readonly string ClockPalette = "chrome";

		public readonly bool Horizontal = false;

		public int IconCount { get; private set; }
		public event Action<int, int> OnIconCountChanged = (a, b) => { };

		readonly ModData modData;
		readonly WorldRenderer worldRenderer;
		readonly SupportPowerManager spm;

		Animation icon;
		Animation clock;
		Dictionary<Rectangle, SupportPowerIcon> icons = [];

		public SupportPowerIcon TooltipIcon { get; private set; }
		public Func<SupportPowerIcon> GetTooltipIcon;
		readonly Lazy<TooltipContainerWidget> tooltipContainer;
		HotkeyReference[] hotkeys;

		Rectangle eventBounds;
		public override Rectangle EventBounds => eventBounds;
		SpriteFont overlayFont;
		float2 iconOffset, holdOffset, readyOffset, timeOffset;

		[CustomLintableHotkeyNames]
		public static IEnumerable<string> LinterHotkeyNames(MiniYamlNode widgetNode, Action<string> emitError)
		{
			var prefix = "";
			var prefixNode = widgetNode.Value.NodeWithKeyOrDefault("HotkeyPrefix");
			if (prefixNode != null)
				prefix = prefixNode.Value.Value;

			var count = 0;
			var countNode = widgetNode.Value.NodeWithKeyOrDefault("HotkeyCount");
			if (countNode != null)
				count = FieldLoader.GetValue<int>("HotkeyCount", countNode.Value.Value);

			if (count == 0)
				return [];

			if (string.IsNullOrEmpty(prefix))
				emitError($"{widgetNode.Location} must define HotkeyPrefix if HotkeyCount > 0.");

			return Exts.MakeArray(count, i => prefix + (i + 1).ToStringInvariant("D2"));
		}

		[ObjectCreator.UseCtor]
		public SupportPowersWidget(ModData modData, World world, WorldRenderer worldRenderer)
		{
			this.modData = modData;
			this.worldRenderer = worldRenderer;
			GetTooltipIcon = () => TooltipIcon;
			spm = world.LocalPlayer.PlayerActor.Trait<SupportPowerManager>();
			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		public override void Initialize(WidgetArgs args)
		{
			base.Initialize(args);

			hotkeys = Exts.MakeArray(HotkeyCount,
				i => modData.Hotkeys[HotkeyPrefix + (i + 1).ToStringInvariant("D2")]);

			overlayFont = Game.Renderer.Fonts[OverlayFont];

			iconOffset = 0.5f * IconSize.ToFloat2() + IconSpriteOffset;

			HoldText = FluentProvider.GetMessage(HoldText);
			holdOffset = iconOffset - overlayFont.Measure(HoldText) / 2;
			ReadyText = FluentProvider.GetMessage(ReadyText);
			readyOffset = iconOffset - overlayFont.Measure(ReadyText) / 2;

			clock = new Animation(worldRenderer.World, ClockAnimation);
		}

		public class SupportPowerIcon
		{
			public SupportPowerInstance Power;
			public float2 Pos;
			public Sprite Sprite;
			public PaletteReference Palette;
			public PaletteReference IconClockPalette;
			public HotkeyReference Hotkey;

			// Decoupled rendering: IconOverlayTextOverride() is virtual; the C&C charge-drain override
			// enumerates the sim-mutated support-power Instances list, so it is captured under the world read lock
			// in RefreshIcons and Draw renders the cached value.
			public string CachedOverlayText;

			// Decoupled rendering: the support-power tooltip (SupportPowerTooltipLogic) runs during the
			// unlocked Ui.Draw and otherwise reads the live, sim-mutated SupportPowerInstance (Info/Instances/
			// TooltipTimeTextOverride all enumerate the Instances list). Capture everything it needs here under the
			// world read lock in RefreshIcons; the tooltip reads only these cached values.
			public SupportPowerInfo TooltipInfo;
			public int TooltipLevel;
			public int TooltipRemainingTicks;
			public string TooltipTimeText;
			public bool TooltipActive;
		}

		public void RefreshIcons()
		{
			icons = [];
			var powers = spm.Powers.Values.Where(p => !p.Disabled)
				.OrderBy(p => p.Info.SupportPowerPaletteOrder);

			var oldIconCount = IconCount;
			IconCount = 0;

			var rb = RenderBounds;
			foreach (var p in powers)
			{
				Rectangle rect;
				if (Horizontal)
					rect = new Rectangle(rb.X + IconCount * (IconSize.X + IconMargin), rb.Y, IconSize.X, IconSize.Y);
				else
					rect = new Rectangle(rb.X, rb.Y + IconCount * (IconSize.Y + IconMargin), IconSize.X, IconSize.Y);

				var level = p.GetLevel();
				if (level == 0)
					continue;

				icon = new Animation(worldRenderer.World, p.Info.IconImage);
				icon.Play(p.Info.Icons.First(i => i.Key == level).Value);

				var power = new SupportPowerIcon()
				{
					Power = p,
					Pos = new float2(rect.Location),
					Sprite = icon.Image,
					Palette = worldRenderer.Palette(p.Info.IconPalette),
					IconClockPalette = worldRenderer.Palette(ClockPalette),
					Hotkey = IconCount < HotkeyCount ? hotkeys[IconCount] : null,
					CachedOverlayText = p.IconOverlayTextOverride(),
					TooltipInfo = p.Info,
					TooltipLevel = level,
					TooltipRemainingTicks = p.RemainingTicks,
					TooltipTimeText = p.TooltipTimeTextOverride(),
					TooltipActive = p.Active,
				};

				icons.Add(rect, power);
				IconCount++;
			}

			eventBounds = icons.Keys.Union();

			if (oldIconCount != IconCount)
				OnIconCountChanged(oldIconCount, IconCount);
		}

		protected void ClickIcon(SupportPowerIcon clicked)
		{
			// Decoupled rendering: Target() sets up an order generator from live power/world state - wait
			// out any in-flight sim tick (one-shot input; matches original single-threaded timing). Covers both the
			// mouse and hotkey entry points.
			Game.EnterWorldReadLock();
			try
			{
				if (!clicked.Power.Active)
				{
					Game.Sound.PlayToPlayer(SoundType.UI, spm.Self.Owner, clicked.Power.Info.InsufficientPowerSound);
					Game.Sound.PlayNotification(spm.Self.World.Map.Rules, spm.Self.Owner, "Speech",
						clicked.Power.Info.InsufficientPowerSpeechNotification, spm.Self.Owner.Faction.InternalName);

					TextNotificationsManager.AddTransientLine(spm.Self.Owner, clicked.Power.Info.InsufficientPowerTextNotification);
				}
				else
					clicked.Power.Target();
			}
			finally
			{
				Game.ExitWorldReadLock();
			}
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (e.Event == KeyInputEvent.Down)
			{
				var a = icons.Values.FirstOrDefault(i => i.Hotkey != null && i.Hotkey.IsActivatedBy(e));

				if (a != null)
				{
					ClickIcon(a);
					return true;
				}
			}

			return false;
		}

		public override void Draw()
		{
			timeOffset = iconOffset - overlayFont.Measure(WidgetUtils.FormatTime(0, worldRenderer.World.Timestep)) / 2;

			// Icons
			Game.Renderer.EnableAntialiasingFilter();
			foreach (var p in icons.Values)
			{
				WidgetUtils.DrawSpriteCentered(p.Sprite, p.Palette, p.Pos + iconOffset);

				// Charge progress
				var sp = p.Power;
				clock.PlayFetchIndex(ClockSequence,
					() => sp.TotalTicks == 0 ? clock.CurrentSequence.Length - 1 : (sp.TotalTicks - sp.RemainingTicks)
					* (clock.CurrentSequence.Length - 1) / sp.TotalTicks);

				clock.Tick();
				WidgetUtils.DrawSpriteCentered(clock.Image, p.IconClockPalette, p.Pos + iconOffset);
			}

			Game.Renderer.DisableAntialiasingFilter();

			// Overlay
			foreach (var p in icons.Values)
			{
				var customText = p.CachedOverlayText;
				if (customText != null)
				{
					var customOffset = iconOffset - overlayFont.Measure(customText) / 2;
					overlayFont.DrawTextWithContrast(customText,
						p.Pos + customOffset,
						Color.White, Color.Black, 1);
				}
				else if (p.Power.Ready)
					overlayFont.DrawTextWithContrast(ReadyText,
						p.Pos + readyOffset,
						Color.White, Color.Black, 1);
				else if (!p.Power.Active)
					overlayFont.DrawTextWithContrast(HoldText,
						p.Pos + holdOffset,
						Color.White, Color.Black, 1);
				else
					overlayFont.DrawTextWithContrast(WidgetUtils.FormatTime(p.Power.RemainingTicks, worldRenderer.World.Timestep),
						p.Pos + timeOffset,
						Color.White, Color.Black, 1);
			}
		}

		public override void Tick()
		{
			// Decoupled rendering: RefreshIcons enumerates the live, sim-mutated Powers dictionary.
			// Refresh only when the sim thread is not mid-tick; otherwise keep this frame's icons and retry next
			// frame. Draw reads only the icon cache plus scalar power fields, so it needs no lock.
			// TODO: Only do this when the powers have changed
			if (!Game.TryEnterWorldReadLock())
				return;

			try
			{
				RefreshIcons();

				// Decoupled rendering: re-resolve from the live cursor position every tick (not just on
				// mouse-move) so the freshly-rebuilt icon under a STATIONARY cursor is picked up for this tick's
				// cached data - including when a power's icon disappears for a tick and then reappears. Resolves to
				// null when no icon is hovered (the tooltip's own visibility is gated by MouseEntered/Exited).
				TooltipIcon = icons.Where(i => i.Key.Contains(Viewport.LastMousePos)).Select(i => i.Value).FirstOrDefault();
			}
			finally
			{
				Game.ExitWorldReadLock();
			}
		}

		public override void MouseEntered()
		{
			if (TooltipContainer == null)
				return;

			tooltipContainer.Value.SetTooltip(TooltipTemplate,
				new WidgetArgs()
				{
					{ "world", worldRenderer.World },
					{ "player", spm.Self.Owner },
					{ "getTooltipIcon", GetTooltipIcon },
					{ "playerResources", worldRenderer.World.LocalPlayer.PlayerActor.Trait<PlayerResources>() }
				});
		}

		public override void MouseExited()
		{
			if (TooltipContainer == null)
				return;

			tooltipContainer.Value.RemoveTooltip();
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			if (mi.Event == MouseInputEvent.Move)
			{
				var icon = icons.Where(i => i.Key.Contains(mi.Location))
					.Select(i => i.Value).FirstOrDefault();

				TooltipIcon = icon;
				return false;
			}

			if (mi.Event != MouseInputEvent.Down)
				return false;

			var clicked = icons.Where(i => i.Key.Contains(mi.Location))
				.Select(i => i.Value).FirstOrDefault();

			if (clicked != null)
				ClickIcon(clicked);

			return true;
		}
	}
}
