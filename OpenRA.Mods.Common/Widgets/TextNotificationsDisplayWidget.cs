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
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets
{
	public class TextNotificationsDisplayWidget : Widget
	{
		public readonly int DisplayDurationMs = 0;
		public readonly int ItemSpacing = 4;
		public readonly int BottomSpacing = 0;
		public readonly int LogLength = 8;
		public readonly bool HideOverflow = true;

		[Desc("Colour the text is drawn in during the \"on\" phase of a flashing notification.")]
		public readonly Color FlashColor = Color.White;

		[Desc("Font swapped in during the \"on\" phase of a flashing notification (e.g. a bold sibling).",
			"Leave empty to keep the normal font and rely on FlashUnderline / FlashColor alone.")]
		public readonly string FlashFont = "Bold";

		[Desc("Draw a rule under the text during the \"on\" phase of a flashing notification.")]
		public readonly bool FlashUnderline = false;

		[Desc("Duration in milliseconds of one flash cycle (one \"on\" phase plus one \"off\" phase).")]
		public readonly int FlashIntervalMs = 500;

		[Desc("Duration in milliseconds of the \"on\" phase within each flash cycle.")]
		public readonly int FlashOnMs = 250;

		[Desc("How many times a flashing notification flashes before settling to its normal appearance.")]
		public readonly int FlashCount = 3;

		[Desc("Pulse the whole line's background block bright during the \"on\" phase, so the entire line flashes.")]
		public readonly bool FlashHighlight = false;

		[Desc("Id of the ColorBlock child in the line template whose colour is pulsed when FlashHighlight is set.")]
		public readonly string FlashHighlightBlock = "BACKGROUND";

		[Desc("Colour the background block is pulsed to during the \"on\" phase when FlashHighlight is set.")]
		public readonly Color FlashBackgroundColor = Color.White;

		[Desc("Colour the text is drawn in during the \"on\" phase when FlashHighlight is set,",
			"for contrast against the bright block (overrides FlashColor in highlight mode).")]
		public readonly Color FlashTextColor = Color.Black;

		public string ChatTemplate = "CHAT_LINE_TEMPLATE";
		public string SystemTemplate = "SYSTEM_LINE_TEMPLATE";
		public string MissionTemplate = "CHAT_LINE_TEMPLATE";
		public string FeedbackTemplate = "TRANSIENT_LINE_TEMPLATE";
		public string TransientsTemplate = "TRANSIENT_LINE_TEMPLATE";
		readonly Dictionary<TextNotificationPool, Widget> templates = [];

		readonly List<long> expirations = [];

		sealed class FlashLine
		{
			public readonly LabelWidget Label;
			public readonly string BaseFont;
			public readonly long Start;

			public FlashLine(LabelWidget label, string baseFont, long start)
			{
				Label = label;
				BaseFont = baseFont;
				Start = start;
			}
		}

		readonly List<FlashLine> flashing = [];

		// Total flash lifetime; after this the line settles back to its normal appearance.
		long FlashDurationMs => (long)FlashIntervalMs * FlashCount;

		bool FlashActive(long start) => Game.RunTime - start < FlashDurationMs;

		// Square wave: "on" for the first FlashOnMs of every FlashIntervalMs cycle, "off" for the remainder.
		bool FlashOn(long start)
		{
			var elapsed = Game.RunTime - start;
			return elapsed < FlashDurationMs && elapsed % FlashIntervalMs < FlashOnMs;
		}

		Rectangle overflowDrawBounds = Rectangle.Empty;
		public override Rectangle EventBounds => Rectangle.Empty;

		public override void Initialize(WidgetArgs args)
		{
			base.Initialize(args);

			templates.Add(TextNotificationPool.Chat, Ui.LoadWidget(ChatTemplate, null, []));
			templates.Add(TextNotificationPool.System, Ui.LoadWidget(SystemTemplate, null, []));
			templates.Add(TextNotificationPool.Mission, Ui.LoadWidget(MissionTemplate, null, []));
			templates.Add(TextNotificationPool.Feedback, Ui.LoadWidget(FeedbackTemplate, null, []));
			templates.Add(TextNotificationPool.Transients, Ui.LoadWidget(TransientsTemplate, null, []));

			// HACK: Assume that all templates use the same font
			var lineHeight = Game.Renderer.Fonts[templates[TextNotificationPool.Chat].Get<LabelWidget>("TEXT").Font].Measure("").Y;
			var wholeLines = (int)Math.Floor((double)((Bounds.Height - BottomSpacing) / lineHeight));
			var visibleChildrenHeight = wholeLines * lineHeight;

			var y = RenderOrigin.Y + Bounds.Height - visibleChildrenHeight;
			overflowDrawBounds = new Rectangle(RenderOrigin.X, y, Bounds.Width, visibleChildrenHeight);
		}

		public override void DrawOuter()
		{
			if (!IsVisible() || Children.Count == 0)
				return;

			var mostRecentMessageOverflows = Bounds.Height < Children[^1].Bounds.Height;

			if (mostRecentMessageOverflows && HideOverflow)
				Game.Renderer.EnableScissor(overflowDrawBounds);

			var bounds = Bounds.ToRectangle();
			for (var i = Children.Count - 1; i >= 0; i--)
			{
				if (!HideOverflow || mostRecentMessageOverflows || bounds.Contains(Children[i].Bounds.ToRectangle()))
					Children[i].DrawOuter();

				if (mostRecentMessageOverflows)
					break;
			}

			if (mostRecentMessageOverflows && HideOverflow)
				Game.Renderer.DisableScissor();

			if (FlashUnderline)
				DrawFlashUnderlines();
		}

		void DrawFlashUnderlines()
		{
			foreach (var f in flashing)
			{
				if (!FlashOn(f.Start))
					continue;

				if (!Game.Renderer.Fonts.TryGetValue(f.Label.Font, out var font))
					continue;

				var text = f.Label.GetText();
				if (string.IsNullOrEmpty(text))
					continue;

				var size = font.Measure(text);
				var origin = f.Label.RenderOrigin;

				// TEXT uses VAlign Middle; place the rule just below the glyphs.
				var y = origin.Y + (f.Label.Bounds.Height + size.Y) / 2 + 1;
				WidgetUtils.FillRectWithColor(new Rectangle(origin.X, y, size.X, 1), FlashColor);
			}
		}

		public void AddNotification(TextNotification notification)
		{
			var notificationWidget = templates[notification.Pool].Clone();
			WidgetUtils.SetupTextNotification(notificationWidget, notification, Bounds.Width, false);

			if (notification.Flash && Game.Settings.Game.FlashTransientNotifications)
			{
				var textLabel = notificationWidget.GetOrNull<LabelWidget>("TEXT");
				if (textLabel != null)
				{
					var start = Game.RunTime;
					var baseColor = textLabel.GetColor;
					var onColor = FlashHighlight ? FlashTextColor : FlashColor;
					textLabel.GetColor = () => FlashOn(start) ? onColor : baseColor();
					flashing.Add(new FlashLine(textLabel, textLabel.Font, start));

					// Whole-line flash: pulse the line's background block between its base colour and
					// FlashBackgroundColor. The block draws before the text, so the bright fill lands
					// behind the (contrast-recoloured) text with no draw-order change. The getter reads
					// FlashOn, so it self-settles to the base colour once the flash finishes.
					if (FlashHighlight && !string.IsNullOrEmpty(FlashHighlightBlock))
					{
						var block = notificationWidget.GetOrNull<ColorBlockWidget>(FlashHighlightBlock);
						if (block != null)
						{
							var baseBlockColor = block.GetColor;
							block.GetColor = () => FlashOn(start) ? FlashBackgroundColor : baseBlockColor();
						}
					}
				}
			}

			if (Children.Count == 0)
				notificationWidget.Bounds.Y = Bounds.Bottom - notificationWidget.Bounds.Height - BottomSpacing;
			else
			{
				foreach (var line in Children)
					line.Bounds.Y -= notificationWidget.Bounds.Height + ItemSpacing;

				var lastLine = Children[^1];
				notificationWidget.Bounds.Y = lastLine.Bounds.Bottom + ItemSpacing;
			}

			AddChild(notificationWidget);
			expirations.Add(Game.RunTime + DisplayDurationMs);

			while (Children.Count > LogLength)
				RemoveNotification();
		}

		public void RemoveMostRecentNotification()
		{
			if (Children.Count == 0)
				return;

			var mostRecentChild = Children[^1];

			RemoveChild(mostRecentChild);
			expirations.RemoveAt(expirations.Count - 1);

			for (var i = Children.Count - 1; i >= 0; i--)
				Children[i].Bounds.Y += mostRecentChild.Bounds.Height + ItemSpacing;
		}

		void RemoveNotification()
		{
			if (Children.Count == 0)
				return;

			RemoveChild(Children[0]);
			expirations.RemoveAt(0);
		}

		public override void Tick()
		{
			// Drive the bold font swap and prune finished flashes. Runs regardless of DisplayDurationMs.
			for (var i = flashing.Count - 1; i >= 0; i--)
			{
				var f = flashing[i];
				if (!FlashActive(f.Start))
				{
					if (!string.IsNullOrEmpty(FlashFont))
						f.Label.Font = f.BaseFont;

					flashing.RemoveAt(i);
					continue;
				}

				if (!string.IsNullOrEmpty(FlashFont))
					f.Label.Font = FlashOn(f.Start) ? FlashFont : f.BaseFont;
			}

			if (DisplayDurationMs == 0)
				return;

			// This takes advantage of the fact that recentLines is ordered by expiration, from sooner to later
			while (Children.Count > 0 && Game.RunTime >= expirations[0])
				RemoveNotification();
		}
	}
}
