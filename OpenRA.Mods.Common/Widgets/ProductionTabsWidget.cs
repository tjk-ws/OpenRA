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
using System.Globalization;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets
{
	public class ProductionTab
	{
		public string Name;
		public ProductionQueue Queue;

		// Decoupled rendering: per-frame render/UI reads (tab visibility, done-highlight, group alert,
		// type-button enabled state) must not enumerate the live sim-mutated queue lists. These are refreshed by
		// ProductionTabGroup.RefreshCachedState from the widget's Tick, under the world read lock.
		public bool CachedVisible;
		public bool CachedAnyDone;
	}

	public class ProductionTabGroup
	{
		public List<ProductionTab> Tabs = [];
		public string Group;
		public int NextQueueName = 1;

		// Cached by RefreshCachedState (called under the world read lock); read every frame by the
		// production-type button image lambda, so it must not touch live queue state.
		public bool Alert { get; private set; }

		public void RefreshCachedState()
		{
			var alert = false;
			foreach (var t in Tabs)
			{
				t.CachedVisible = t.Queue.AlwaysVisible || t.Queue.BuildableItems().Any();
				t.CachedAnyDone = t.Queue.AllQueued().Any(i => i.Done);
				alert |= t.CachedAnyDone;
			}

			Alert = alert;
		}

		public void Update(IEnumerable<ProductionQueue> allQueues)
		{
			var queues = allQueues.Where(q => q.Enabled && q.Info.Group == Group && (q.BuildableItems().Any() || q.AlwaysVisible)).ToList();
			var tabs = new List<ProductionTab>();
			var largestUsedName = 0;

			// Remove stale queues
			foreach (var t in Tabs)
			{
				if (!queues.Contains(t.Queue))
					continue;

				tabs.Add(t);
				queues.Remove(t.Queue);
				largestUsedName = Math.Max(int.Parse(t.Name, NumberFormatInfo.CurrentInfo), largestUsedName);
			}

			NextQueueName = largestUsedName + 1;

			// Add new queues
			foreach (var queue in queues)
				tabs.Add(new ProductionTab()
				{
					Name = NextQueueName++.ToString(NumberFormatInfo.CurrentInfo),
					Queue = queue
				});
			Tabs = tabs;
		}
	}

	public class ProductionTabsWidget : Widget
	{
		readonly World world;

		public readonly string PaletteWidget = null;
		public readonly string TypesContainer = null;
		public readonly string BackgroundContainer = null;

		public readonly int TabWidth = 30;
		public readonly int ArrowWidth = 20;

		public readonly string ClickSound = ChromeMetrics.Get<string>("ClickSound");
		public readonly string ClickDisabledSound = ChromeMetrics.Get<string>("ClickDisabledSound");

		public readonly HotkeyReference PreviousProductionTabKey = new();
		public readonly HotkeyReference NextProductionTabKey = new();

		public readonly Dictionary<string, ProductionTabGroup> Groups;

		public string ArrowButton = "button";
		public string TabButton = "button";

		public string Background = "panel-black";
		public string Decorations = "scrollpanel-decorations";
		public readonly string DecorationScrollLeft = "left";
		public readonly string DecorationScrollRight = "right";
		CachedTransform<(bool Disabled, bool Pressed, bool Hover, bool Focused, bool Highlighted), Sprite> getLeftArrowImage;
		CachedTransform<(bool Disabled, bool Pressed, bool Hover, bool Focused, bool Highlighted), Sprite> getRightArrowImage;

		public readonly Color TabColor = Color.White;
		public readonly Color TabColorDone = Color.Gold;

		int contentWidth = 0;
		float listOffset = 0;
		bool leftPressed = false;
		bool rightPressed = false;
		SpriteFont font;
		Rectangle leftButtonRect;
		Rectangle rightButtonRect;
		readonly Lazy<ProductionPaletteWidget> paletteWidget;
		string queueGroup;

		readonly List<(ProductionQueue Queue, bool Enabled)> cachedProductionQueueEnabledStates = [];

		// Set from the sim thread by ActorChanged; consumed by Tick on the main thread under the world read lock.
		volatile bool queuesDirty = true;

		[ObjectCreator.UseCtor]
		public ProductionTabsWidget(World world)
		{
			this.world = world;

			Groups = world.Map.Rules.Actors.Values.SelectMany(a => a.TraitInfos<ProductionQueueInfo>())
				.Select(q => q.Group).Distinct().ToDictionary(g => g, g => new ProductionTabGroup() { Group = g });

			// Only visible if the production palette has icons to display
			IsVisible = () => queueGroup != null && Groups[queueGroup].Tabs.Count > 0;

			paletteWidget = Exts.Lazy(() => Ui.Root.Get<ProductionPaletteWidget>(PaletteWidget));
		}

		public override void Initialize(WidgetArgs args)
		{
			base.Initialize(args);

			var rb = RenderBounds;
			leftButtonRect = new Rectangle(rb.X, rb.Y, ArrowWidth, rb.Height);
			rightButtonRect = new Rectangle(rb.Right - ArrowWidth, rb.Y, ArrowWidth, rb.Height);
			font = Game.Renderer.Fonts["TinyBold"];

			getLeftArrowImage = WidgetUtils.GetCachedStatefulImage(Decorations, DecorationScrollLeft);
			getRightArrowImage = WidgetUtils.GetCachedStatefulImage(Decorations, DecorationScrollRight);
		}

		public bool SelectNextTab(bool reverse)
		{
			if (queueGroup == null)
				return true;

			// Prioritize alerted queues (cached state - see ProductionTab.CachedAnyDone)
			var queues = Groups[queueGroup].Tabs
					.OrderByDescending(t => t.CachedAnyDone ? 1 : 0)
					.Select(t => t.Queue)
					.ToList();

			if (reverse) queues.Reverse();

			CurrentQueue = queues.SkipWhile(q => q != CurrentQueue)
				.Skip(1).FirstOrDefault() ?? queues.FirstOrDefault();

			return true;
		}

		public void PickUpCompletedBuilding()
		{
			// This is called from ProductionTabsLogic
			paletteWidget.Value.PickUpCompletedBuilding();
		}

		public string QueueGroup
		{
			get => queueGroup;

			set
			{
				listOffset = 0;
				queueGroup = value;
				SelectNextTab(false);
			}
		}

		public ProductionQueue CurrentQueue
		{
			get => paletteWidget.Value.CurrentQueue;

			set
			{
				paletteWidget.Value.CurrentQueue = value;
				queueGroup = value?.Info.Group;

				// TODO: Scroll tabs so selected queue is visible
			}
		}

		public override void Draw()
		{
			var tabs = Groups[queueGroup].Tabs.Where(t => t.CachedVisible).ToList();

			if (tabs.Count == 0)
				return;

			var rb = RenderBounds;

			var leftDisabled = listOffset >= 0;
			var leftHover = Ui.MouseOverWidget == this && leftButtonRect.Contains(Viewport.LastMousePos);
			var rightDisabled = listOffset <= Bounds.Width - rightButtonRect.Width - leftButtonRect.Width - contentWidth;
			var rightHover = Ui.MouseOverWidget == this && rightButtonRect.Contains(Viewport.LastMousePos);

			WidgetUtils.DrawPanel(Background, rb);
			ButtonWidget.DrawBackground(ArrowButton, leftButtonRect, leftDisabled, leftPressed, leftHover, false);
			ButtonWidget.DrawBackground(ArrowButton, rightButtonRect, rightDisabled, rightPressed, rightHover, false);

			var leftArrowImage = getLeftArrowImage.Update((leftDisabled, leftPressed, leftHover, false, false));
			WidgetUtils.DrawSprite(leftArrowImage,
				new float2(
					leftButtonRect.Left + (int)((leftButtonRect.Width - leftArrowImage.Size.X) / 2),
					leftButtonRect.Top + (int)((leftButtonRect.Height - leftArrowImage.Size.Y) / 2)));

			var rightArrowImage = getRightArrowImage.Update((rightDisabled, rightPressed, rightHover, false, false));
			WidgetUtils.DrawSprite(rightArrowImage,
				new float2(
					rightButtonRect.Left + (int)((rightButtonRect.Width - rightArrowImage.Size.X) / 2),
					rightButtonRect.Top + (int)((rightButtonRect.Height - rightArrowImage.Size.Y) / 2)));

			// Draw tab buttons
			Game.Renderer.EnableScissor(new Rectangle(leftButtonRect.Right, rb.Y + 1, rightButtonRect.Left - leftButtonRect.Right - 1, rb.Height));
			var origin = new int2(leftButtonRect.Right - 1 + (int)listOffset, leftButtonRect.Y);
			contentWidth = 0;

			foreach (var tab in tabs)
			{
				var rect = new Rectangle(origin.X + contentWidth, origin.Y, TabWidth, rb.Height);
				var hover = !leftHover && !rightHover && Ui.MouseOverWidget == this && rect.Contains(Viewport.LastMousePos);
				var highlighted = tab.Queue == CurrentQueue;
				ButtonWidget.DrawBackground(TabButton, rect, false, false, hover, highlighted);
				contentWidth += TabWidth - 1;

				var textSize = font.Measure(tab.Name);
				var position = new int2(rect.X + (rect.Width - textSize.X) / 2, rect.Y + (rect.Height - textSize.Y) / 2);
				font.DrawTextWithContrast(tab.Name, position, tab.CachedAnyDone ? TabColorDone : TabColor, Color.Black, 1);
			}

			Game.Renderer.DisableScissor();
		}

		void Scroll(int amount)
		{
			listOffset += amount * Game.Settings.Game.UIScrollSpeed;
			listOffset = Math.Min(0, Math.Max(Bounds.Width - rightButtonRect.Width - leftButtonRect.Width - contentWidth, listOffset));
		}

		// Is added to world.ActorAdded by the SidebarLogic handler.
		// Decoupled rendering: world.ActorAdded/ActorRemoved fire on the SIM thread (actor spawn/death
		// during the world tick), so this must not touch widget state directly - it only marks the queue list
		// dirty; the actual refresh happens in Tick under the world read lock on the main thread.
		public void ActorChanged(Actor a)
		{
			// Ignore non-production actors and actors owned by non-local player
			if (!a.Info.HasTraitInfo<ProductionQueueInfo>() || a.Owner != a.World.LocalPlayer)
				return;

			queuesDirty = true;
		}

		void RefreshQueues()
		{
			var queues = world.ActorsWithTrait<ProductionQueue>()
				.Where(p => p.Actor.Owner == p.Actor.World.LocalPlayer && p.Actor.IsInWorld)
				.Select(p => p.Trait);

			cachedProductionQueueEnabledStates.Clear();
			foreach (var queue in queues)
				cachedProductionQueueEnabledStates.Add((queue, queue.Enabled));

			foreach (var g in Groups.Values)
				g.Update(cachedProductionQueueEnabledStates.Select(t => t.Queue));

			if (queueGroup == null)
				return;

			// Queue destroyed, was last of type: switch to a new group
			if (Groups[queueGroup].Tabs.Count == 0)
				QueueGroup = Groups.Where(g => g.Value.Tabs.Count > 0)
					.Select(g => g.Key).FirstOrDefault();

			// Queue destroyed, others of same type: switch to another tab
			else if (!Groups[queueGroup].Tabs.Select(t => t.Queue).Contains(CurrentQueue))
				SelectNextTab(false);
		}

		public override void Tick()
		{
			if (leftPressed) Scroll(1);
			if (rightPressed) Scroll(-1);

			// Decoupled rendering: inspect/refresh queue state only when the sim thread is not mid-tick,
			// so tabs aren't added/removed from a half-updated world (which made the sidebar change on its own).
			// Non-blocking; scrolling above stays responsive regardless. Lock is always free when decoupling is off.
			if (!Game.TryEnterWorldReadLock())
				return;

			try
			{
				// Deferred queue-list refresh: ActorChanged (sim thread) only marks dirty, we apply it here.
				if (queuesDirty)
				{
					queuesDirty = false;
					RefreshQueues();
				}

				// It is possible that production queues get enabled/disabled during their lifetime.
				// This makes sure every enabled production queue always has its tab associated with it.
				var shouldUpdateQueues = false;
				for (var i = 0; i < cachedProductionQueueEnabledStates.Count; i++)
				{
					var (queue, enabled) = cachedProductionQueueEnabledStates[i];

					if (queue.Enabled != enabled)
					{
						shouldUpdateQueues = true;

						// Refresh queue.Enabled value in cache
						cachedProductionQueueEnabledStates[i] = (queue, queue.Enabled);
					}
				}

				if (shouldUpdateQueues)
					foreach (var g in Groups.Values)
						g.Update(cachedProductionQueueEnabledStates.Select(t => t.Queue));

				// Refresh the per-tab/per-group cached render state (visibility, done-highlight, alert) that
				// Draw, SelectNextTab and the production-type buttons read instead of the live queue lists.
				foreach (var g in Groups.Values)
					g.RefreshCachedState();
			}
			finally
			{
				Game.ExitWorldReadLock();
			}
		}

		public override bool YieldMouseFocus(MouseInput mi)
		{
			leftPressed = rightPressed = false;
			return base.YieldMouseFocus(mi);
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			if (mi.Event == MouseInputEvent.Scroll)
			{
				Scroll(mi.Delta.Y);
				return true;
			}

			if (mi.Button != MouseButton.Left)
				return true;

			if (mi.Event == MouseInputEvent.Down && !TakeMouseFocus(mi))
				return true;

			if (!HasMouseFocus)
				return true;

			if (HasMouseFocus && mi.Event == MouseInputEvent.Up)
				return YieldMouseFocus(mi);

			leftPressed = leftButtonRect.Contains(mi.Location);
			rightPressed = rightButtonRect.Contains(mi.Location);
			var leftDisabled = listOffset >= 0;
			var rightDisabled = listOffset <= Bounds.Width - rightButtonRect.Width - leftButtonRect.Width - contentWidth;

			if (leftPressed || rightPressed)
			{
				if ((leftPressed && !leftDisabled) || (rightPressed && !rightDisabled))
					Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickSound, null);
				else
					Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickDisabledSound, null);
			}

			// Check production tabs
			var offsetloc = mi.Location - new int2(leftButtonRect.Right - 1 + (int)listOffset, leftButtonRect.Y);
			if (offsetloc.X > 0 && offsetloc.X < contentWidth)
			{
				// Decoupled rendering: switching the queue refreshes the palette's icons from live queue state - wait out any
				// in-flight sim tick (one-shot input; matches original single-threaded timing).
				Game.EnterWorldReadLock();
				try
				{
					CurrentQueue = Groups[queueGroup].Tabs[offsetloc.X / (TabWidth - 1)].Queue;
				}
				finally
				{
					Game.ExitWorldReadLock();
				}

				Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickSound, null);
			}

			return true;
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (e.Event != KeyInputEvent.Down)
				return false;

			if (PreviousProductionTabKey.IsActivatedBy(e) || NextProductionTabKey.IsActivatedBy(e))
			{
				Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickSound, null);

				// Decoupled rendering: SelectNextTab switches the queue, refreshing palette icons from live queue state.
				Game.EnterWorldReadLock();
				try
				{
					return SelectNextTab(PreviousProductionTabKey.IsActivatedBy(e));
				}
				finally
				{
					Game.ExitWorldReadLock();
				}
			}

			return false;
		}
	}
}
