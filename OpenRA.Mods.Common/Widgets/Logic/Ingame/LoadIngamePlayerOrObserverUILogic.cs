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

using OpenRA.Mods.Common.Scripting;
using OpenRA.Mods.Common.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class LoadIngamePlayerOrObserverUILogic : ChromeLogic
	{
		bool loadingObserverWidgets = false;

		[ObjectCreator.UseCtor]
		public LoadIngamePlayerOrObserverUILogic(Widget widget, World world)
		{
			var ingameRoot = widget.Get("INGAME_ROOT");
			var worldRoot = ingameRoot.Get("WORLD_ROOT");
			var menuRoot = ingameRoot.Get("MENU_ROOT");
			var playerRoot = worldRoot.Get("PLAYER_ROOT");

			if (world.LocalPlayer == null)
				Game.LoadWidget(world, "OBSERVER_WIDGETS", playerRoot, []);
			else
			{
				var playerWidgets = Game.LoadWidget(world, "PLAYER_WIDGETS", playerRoot, []);
				var sidebarTicker = playerWidgets.Get<LogicTickerWidget>("SIDEBAR_TICKER");
				var objectives = world.LocalPlayer.PlayerActor.Info.TraitInfoOrDefault<MissionObjectivesInfo>();

				sidebarTicker.OnTick = () =>
				{
					// Switch to observer mode after win/loss
					if (world.LocalPlayer.WinState != WinState.Undefined && !loadingObserverWidgets)
					{
						loadingObserverWidgets = true;
						Game.RunAfterDelay(objectives?.GameOverDelay ?? 0, () =>
						{
							if (!Game.IsCurrentWorld(world))
								return;

							playerRoot.RemoveChildren();
							Game.LoadWidget(world, "OBSERVER_WIDGETS", playerRoot, []);
						});
					}
				};
			}

			Game.LoadWidget(world, "DEBUG_WIDGETS", worldRoot, []);
			Game.LoadWidget(world, "TRANSIENTS_PANEL", worldRoot, []);

			world.GameOver += () =>
			{
				// Decoupled rendering: World.GameOver fires from World.EndGame during the sim tick, but this body mutates the
				// widget tree (CloseWindow / RemoveChildren), opens an FMV window, and clicks a UI button - all
				// main-thread-only. Marshal it to the main thread (inline when already there; OFF path unaffected).
				void OnGameOver()
				{
					// besides the widget mutations (main-thread, via the marshal), this reads LIVE world state
					// (WorldActor traits, LocalPlayer.WinState) and runs Sync.RunUnsynced. It drains on the main thread
					// while the sim may be mid-tick, so take the world read lock around it - same hardening as the
					// Selection deferred path. Inline-on-main / OFF still acquires it (uncontended).
					Game.EnterWorldReadLock();
					try
					{
						Ui.CloseWindow();
						menuRoot.RemoveChildren();

						if (world.LocalPlayer != null)
						{
							var scriptContext = world.WorldActor.TraitOrDefault<LuaScript>();
							var missionData = world.WorldActor.Info.TraitInfoOrDefault<MissionDataInfo>();
							if (missionData != null && !(scriptContext != null && scriptContext.FatalErrorOccurred))
							{
								var video = world.LocalPlayer.WinState == WinState.Won ? missionData.WinVideo : missionData.LossVideo;
								if (!string.IsNullOrEmpty(video))
									Media.PlayFMVFullscreen(world, video, () => { });
							}
						}

						var optionsButton = playerRoot.GetOrNull<MenuButtonWidget>("OPTIONS_BUTTON");
						if (optionsButton != null)
							Sync.RunUnsynced(world, optionsButton.OnClick);
					}
					finally
					{
						Game.ExitWorldReadLock();
					}
				}

				if (Game.IsOnMainThread)
					OnGameOver();
				else
					Game.RunAfterTick(OnGameOver);
			};
		}
	}
}
