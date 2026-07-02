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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Runtime.ExceptionServices;
using System.Threading;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Server;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA
{
	[IncludeStaticFluentReferences(typeof(Server.Server), typeof(Player), typeof(UnitOrders), typeof(OrderManager))]
	public static class Game
	{
		[FluentReference("filename")]
		const string SavedScreenshot = "notification-saved-screenshot";

		public const int TimestepJankThreshold = 250; // Don't catch up for delays larger than 250ms

		public static InstalledMods Mods { get; private set; }
		public static ExternalMods ExternalMods { get; private set; }

		public static ModData ModData;
		public static Settings Settings;
		public static CursorManager Cursor;
		public static bool HideCursor;
		public static bool SkipContentPrompt;

		static WorldRenderer worldRenderer;
		static string modLaunchWrapper;

		internal static OrderManager OrderManager;
		static Server.Server server;

		public static MersenneTwister CosmeticRandom = new(); // not synced

		public static Renderer Renderer;
		public static Sound Sound;

		public static string EngineVersion { get; private set; }
		public static LocalPlayerProfile LocalPlayerProfile;

		static bool takeScreenshot = false;
		static Benchmark benchmark = null;

		public static event Action OnShellmapLoaded = () => { };

		public static OrderManager JoinServer(ConnectionTarget endpoint, string password, bool recordReplay = true)
		{
			var newConnection = new NetworkConnection(endpoint);
			if (recordReplay)
				newConnection.StartRecording(() => TimestampedFilename());

			var om = new OrderManager(newConnection);
			JoinInner(om);
			CurrentServerSettings.Password = password;
			CurrentServerSettings.Target = endpoint;

			lastConnectionState = ConnectionState.PreConnecting;
			ConnectionStateChanged(OrderManager, password, newConnection);

			return om;
		}

		public static string TimestampedFilename(bool includemilliseconds = false, string extra = "")
		{
			var format = includemilliseconds ? "yyyy-MM-ddTHHmmssfffZ" : "yyyy-MM-ddTHHmmssZ";
			return ModData.Manifest.Id + extra + "-" + DateTime.UtcNow.ToString(format, CultureInfo.InvariantCulture);
		}

		static void JoinInner(OrderManager om)
		{
			// Decoupled rendering: quiesce the background sim thread before disposing/replacing the global OrderManager. The
			// direct JoinServer/JoinReplay entry points don't necessarily go through Disconnect() first, so a sim
			// tick could be mid-TickWorld on the OrderManager about to be disposed. Runs on the main thread, so
			// decoupling can't re-enable before the swap. No-op when decoupling is off.
			StopSimTicking();

			// Refresh static classes before the game starts.
			TextNotificationsManager.Clear();
			UnitOrders.Clear();

			// HACK: The shellmap World and OrderManager are owned by the main menu's WorldRenderer instead of Game.
			// This allows us to switch Game.OrderManager from the shellmap to the new network connection when joining
			// a lobby, while keeping the OrderManager that runs the shellmap intact.
			// A matching check in World.Dispose (which is called by WorldRenderer.Dispose) makes sure that we dispose
			// the shellmap's OM when a lobby game actually starts.
			if (OrderManager?.World == null || OrderManager.World.Type != WorldType.Shellmap)
				OrderManager?.Dispose();

			OrderManager = om;
		}

		public static void JoinReplay(string replayFile)
		{
			JoinInner(new OrderManager(new ReplayConnection(replayFile)));
		}

		static void JoinLocal()
		{
			JoinInner(new OrderManager(new EchoConnection()));

			// Add a spectator client for the local player
			// On the shellmap this player is controlling the map via scripted orders
			OrderManager.LobbyInfo.Clients.Add(new Session.Client
			{
				Index = OrderManager.Connection.LocalClientId,
				Name = Settings.Player.Name,
				PreferredColor = Settings.Player.Color,
				Color = Settings.Player.Color,
				Faction = "Random",
				SpawnPoint = 0,
				Team = 0,
				State = Session.ClientState.Ready
			});
		}

		// More accurate replacement for Environment.TickCount
		static readonly Stopwatch Stopwatch = Stopwatch.StartNew();
		public static long RunTime => Stopwatch.ElapsedMilliseconds;

		// Wall-clock cost of the most recent world.Tick() on the main (netplay) world, in ms — i.e. the
		// deterministic simulation step ONLY. The lockstep network wait happens outside world.Tick()
		// (TryTick gates it), so this excludes time spent blocked on remote orders and is a clean
		// sim-compute signal for adaptive game-speed (multiplayer). 0 until the first
		// ticked frame. Read-only to mods; nothing acts on it unless an adaptive-speed driver is active.
		public static long LastWorldTickTimeMs { get; private set; }

		public static int RenderFrame = 0;

		// Decoupled rendering: serializes the sim thread's world tick against the main thread's world rendering / prepare.
		// The main thread TryEnters it (never blocks); the sim thread holds it for the duration of a world tick.
		static readonly object WorldAccessLock = new();

		// Decoupled rendering: lets UI widgets (in other assemblies) read world state consistently. A widget TryEnters this
		// lock around its world-reads; if the sim thread holds it (mid-tick), TryEnter returns false and the widget
		// skips its refresh this frame, keeping its last consistent state instead of reading a half-updated world.
		// It never blocks, so it does not reintroduce stutter. When decoupling is off the sim thread never holds
		// the lock, so TryEnter always succeeds immediately and behaviour is unchanged.
		public static bool TryEnterWorldReadLock()
		{
			return Monitor.TryEnter(WorldAccessLock);
		}

		// Blocking variant for INPUT handlers (one-shot events like a click, not per-frame refreshes): waits for
		// the in-flight sim tick to finish, bounded by one tick duration. This matches the original single-threaded
		// behaviour, where input handling also ran strictly between ticks - a click never raced the sim there either.
		public static void EnterWorldReadLock()
		{
			Monitor.Enter(WorldAccessLock);
		}

		public static void ExitWorldReadLock()
		{
			Monitor.Exit(WorldAccessLock);
		}

		// True while the world simulation is being ticked on the background sim thread instead of inline on the
		// main thread (threaded GL + DecoupledRendering + an in-progress game). Read by both threads.
		internal static volatile bool decoupledRunning;

		static Thread simThread;

		// An unhandled exception on the sim thread would otherwise hard-terminate the process with no OpenRA log
		// (the fatal-error handler only wraps the main thread). We capture it here and re-throw it on the main
		// thread so it flows through the normal exception-logging / crash-dialog path.
		static volatile ExceptionDispatchInfo simThreadException;

		public static int NetFrameNumber => OrderManager.NetFrameNumber;
		public static int LocalTick => OrderManager.LocalFrameNumber;

		public static event Action<ConnectionTarget> OnRemoteDirectConnect = _ => { };
		public static event Action<OrderManager, string, NetworkConnection> ConnectionStateChanged = (om, pass, conn) => { };
		static ConnectionState lastConnectionState = ConnectionState.PreConnecting;
		public static int LocalClientId => OrderManager.Connection.LocalClientId;

		public static void RemoteDirectConnect(ConnectionTarget endpoint)
		{
			OnRemoteDirectConnect(endpoint);
		}

		// Hacky workaround for orderManager visibility
		public static Widget OpenWindow(World world, string widget)
		{
			return Ui.OpenWindow(widget, new WidgetArgs() { { "world", world }, { "orderManager", OrderManager }, { "worldRenderer", worldRenderer } });
		}

		// Who came up with the great idea of making these things
		// impossible for the things that want them to access them directly?
		public static Widget OpenWindow(string widget, WidgetArgs args)
		{
			return Ui.OpenWindow(widget, new WidgetArgs(args)
			{
				{ "world", worldRenderer.World },
				{ "orderManager", OrderManager },
				{ "worldRenderer", worldRenderer },
			});
		}

		// Load a widget with world, orderManager, worldRenderer args, without adding it to the widget tree
		public static Widget LoadWidget(World world, string id, Widget parent, WidgetArgs args)
		{
			return ModData.WidgetLoader.LoadWidget(new WidgetArgs(args)
			{
				{ "modData", ModData },
				{ "world", world },
				{ "orderManager", OrderManager },
				{ "worldRenderer", worldRenderer },
			}, parent, id);
		}

		public static event Action LobbyInfoChanged = () => { };

		internal static void SyncLobbyInfo()
		{
			LobbyInfoChanged();
		}

		public static event Action BeforeGameStart = () => { };
		public static event Action AfterGameStart = () => { };
		internal static void StartGame(string uid, WorldType type)
		{
			var preview = ModData.MapCache[uid];
			if (preview.Status != MapStatus.Available)
				throw new InvalidDataException($"Invalid map uid: {uid}");

			StartGame(preview.ToMap(), type);
		}

		internal static void StartGame(Map map, WorldType type)
		{
			// Decoupled rendering: stop the sim thread ticking the world we're about to dispose/replace (avoids a teardown race).
			StopSimTicking();

			// drop RunAfterTick callbacks captured against the old world/UI before we dispose it (see Disconnect).
			delayedActions.Clear();

			// Dispose of the old world before creating a new one.
			worldRenderer?.Dispose();

			Cursor.SetCursor(null);
			BeforeGameStart();

			using (new PerfTimer("NewWorld"))
			{
				using (new PerfTimer("NewWorld.PrepareMap"))
					ModData.PrepareMap(map);

				// The depth buffer needs to be initialized with enough range to cover:
				//  - the height of the screen
				//  - the z-offset of tiles from MaxTerrainHeight below the bottom of the screen (pushed into view)
				//  - additional z-offset from actors on top of MaxTerrainHeight terrain
				//  - a small margin so that tiles rendered partially above the top edge of the screen aren't pushed behind the clip plane
				// We need an offset of mapGrid.MaximumTerrainHeight * mapGrid.TileSize.Height / 2 to cover the terrain height
				// and choose to use mapGrid.MaximumTerrainHeight * mapGrid.TileSize.Height / 4 for each of the actor and top-edge cases
				var margin = 0;
				if (map.Grid.EnableDepthBuffer)
					margin = map.Rules.TerrainInfo.TileSize.Height * map.Grid.MaximumTerrainHeight;

				Renderer.SetDepthMargin(margin);

				using (new PerfTimer("NewWorld.WorldCtor"))
					OrderManager.World = new World(map, ModData, OrderManager, type);
			}

			OrderManager.World.GameOver += FinishBenchmark;

			using (new PerfTimer("NewWorldRenderer"))
				worldRenderer = new WorldRenderer(ModData, OrderManager.World);

			// Proactively collect memory during loading to reduce peak memory.
			using (new PerfTimer("GC.Collect (pre-LoadComplete)"))
				GC.Collect();

			using (new PerfTimer("LoadComplete"))
				OrderManager.World.LoadComplete(worldRenderer);

			// Proactively collect memory during loading to reduce peak memory.
			using (new PerfTimer("GC.Collect (post-LoadComplete)"))
				GC.Collect();

			if (OrderManager.GameStarted)
				return;

			Ui.MouseFocusWidget = null;
			Ui.KeyboardFocusWidget = null;

			OrderManager.StartGame();
			worldRenderer.RefreshPalette();
			Cursor.SetCursor(ChromeMetrics.Get<string>("DefaultCursor"));

			// Now loading is completed, now is the ideal time to run a GC and compact the LOH.
			// - All the temporary garbage created during loading can be collected.
			// - Live objects are likely to live for the length of the game or longer,
			//   thus promoting them into a higher generation is not an issue.
			// - We can remove any fragmentation in the LOH caused by temporary loading garbage.
			// - A loading screen is visible, so a delay won't matter to the user.
			//   Much better to clean up now then to drop frames during gameplay for GC pauses.
			GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect();

			// PostLoadComplete is designed for anything that should trigger at the very end of loading.
			// e.g. audio notifications that the game is starting.
			OrderManager.World.PostLoadComplete(worldRenderer);

			AfterGameStart();
		}

		public static void RestartGame()
		{
			var replay = OrderManager.Connection as ReplayConnection;
			var replayName = replay?.Filename;
			var lobbyInfo = OrderManager.LobbyInfo;

			// Reseed the RNG so this isn't an exact repeat of the last game
			lobbyInfo.GlobalSettings.RandomSeed = CosmeticRandom.Next();

			// Note: the map may have been changed on disk outside the game, changing its UID.
			// Use the updated UID if we have tracked the update instead of failing.
			lobbyInfo.GlobalSettings.Map = ModData.MapCache.GetUpdatedMap(lobbyInfo.GlobalSettings.Map);
			if (lobbyInfo.GlobalSettings.Map == null)
			{
				Disconnect();
				Ui.ResetAll();
				LoadShellMap();
				return;
			}

			var orders = new[]
			{
					Order.Command($"sync_lobby {lobbyInfo.Serialize()}"),
					Order.Command("startgame")
			};

			// Disconnect from the current game
			Disconnect();
			Ui.ResetAll();

			// Restart the game with the same replay/mission
			if (replay != null)
				JoinReplay(replayName);
			else
				CreateAndStartLocalServer(lobbyInfo.GlobalSettings.Map, orders);
		}

		public static void CreateAndStartLocalServer(string mapUID, IEnumerable<Order> setupOrders)
		{
			OrderManager om = null;

			void LobbyReady()
			{
				LobbyInfoChanged -= LobbyReady;
				foreach (var o in setupOrders)
					om.IssueOrder(o);
			}

			LobbyInfoChanged += LobbyReady;

			om = JoinServer(CreateLocalServer(mapUID), "");
		}

		public static bool IsHost
		{
			get
			{
				var id = OrderManager.Connection.LocalClientId;
				var client = OrderManager.LobbyInfo.ClientWithIndex(id);
				return client != null && client.IsAdmin;
			}
		}

		static Modifiers modifiers;
		public static Modifiers GetModifierKeys() { return modifiers; }
		internal static void HandleModifierKeys(Modifiers mods) { modifiers = mods; }

		public static void InitializeSettings(Arguments args)
		{
			Settings = new Settings(Path.Combine(Platform.SupportDir, "settings.yaml"), args);
		}

		public static RunStatus InitializeAndRun(string[] args)
		{
			Initialize(new Arguments(args));

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();
			return Run();
		}

		static void Initialize(Arguments args)
		{
			var engineDirArg = args.GetValue("Engine.EngineDir", null);
			if (!string.IsNullOrEmpty(engineDirArg))
				Platform.OverrideEngineDir(engineDirArg);

			var supportDirArg = args.GetValue("Engine.SupportDir", null);
			if (!string.IsNullOrEmpty(supportDirArg))
				Platform.OverrideSupportDir(supportDirArg);

			Console.WriteLine($"Platform is {Platform.CurrentPlatform} ({Platform.CurrentArchitecture})");

			// Load the engine version as early as possible so it can be written to exception logs
			try
			{
				EngineVersion = File.ReadAllText(Path.Combine(Platform.EngineDir, "VERSION")).Trim();
			}
			catch { }

			if (string.IsNullOrEmpty(EngineVersion))
				EngineVersion = "Unknown";

			Console.WriteLine($"Engine version is {EngineVersion}");
			Console.WriteLine($"Runtime: {Platform.RuntimeVersion}");

			// Special case handling of Game.Mod argument: if it matches a real filesystem path
			// then we use this to override the mod search path, and replace it with the mod id
			var modID = args.GetValue("Game.Mod", null);
			var explicitModPaths = Array.Empty<string>();
			if (modID != null && (File.Exists(modID) || Directory.Exists(modID)))
			{
				explicitModPaths = [modID];
				modID = Path.GetFileNameWithoutExtension(modID);
			}

			InitializeSettings(args);

			Log.AddChannel("perf", "perf.log");
			Log.AddChannel("debug", "debug.log");
			Log.AddChannel("server", "server.log", true);
			Log.AddChannel("sound", "sound.log");
			Log.AddChannel("graphics", "graphics.log");
			Log.AddChannel("geoip", "geoip.log");
			Log.AddChannel("nat", "nat.log");
			Log.AddChannel("client", "client.log");

			Nat.Initialize();

			var modSearchArg = args.GetValue("Engine.ModSearchPaths", null);
			var modSearchPaths = modSearchArg != null ?
				FieldLoader.GetValue<ImmutableArray<string>>("Engine.ModsPath", modSearchArg) :
				[Path.Combine(Platform.EngineDir, "mods")];

			Mods = new InstalledMods(modSearchPaths, explicitModPaths);
			Console.WriteLine("Internal mods:");
			foreach (var mod in Mods)
				Console.WriteLine($"\t{mod.Key} ({mod.Value.Metadata.Version})");

			modLaunchWrapper = args.GetValue("Engine.LaunchWrapper", null);

			ExternalMods = new ExternalMods();

			if (modID == null)
				throw new InvalidOperationException("Game.Mod argument missing.");

			if (Mods.TryGetValue(modID, out var manifest))
			{
				var launchPath = args.GetValue("Engine.LaunchPath", null);
				var launchArgs = new List<string>();

				// Sanitize input from platform-specific launchers
				// Process.Start requires paths to not be quoted, even if they contain spaces
				if (launchPath != null && launchPath[0] == '"' && launchPath[^1] == '"')
					launchPath = launchPath[1..^1];

				// Metadata registration requires an explicit launch path
				if (launchPath != null)
					ExternalMods.Register(Mods[modID], launchPath, launchArgs, ModRegistration.User);

				ExternalMods.ClearInvalidRegistrations(ModRegistration.User);
			}
			else
				throw new InvalidOperationException($"Unknown or invalid mod '{modID}'.");

			Console.WriteLine("External mods:");
			foreach (var mod in ExternalMods)
				Console.WriteLine($"\t{mod.Key} ({mod.Value.Version})");

			var platforms = new[] { Settings.Game.Platform, "Default", null };
			foreach (var p in platforms)
			{
				if (p == null)
					throw new InvalidOperationException("Failed to initialize platform-integration library. Check graphics.log for details.");

				Settings.Game.Platform = p;
				try
				{
					var platform = CreatePlatform(p);
					Renderer = new Renderer(platform, Settings.Graphics, manifest.RendererConstants.VertexBatchSize);
					Sound = new Sound(platform, Settings.Sound);

					break;
				}
				catch (Exception e)
				{
					Log.Write("graphics", $"{e}");
					Console.WriteLine("Renderer initialization failed. Check graphics.log for details.");

					Renderer?.Dispose();

					Sound?.Dispose();
				}
			}

			InitializeMod(manifest, args);
		}

		public static IPlatform CreatePlatform(string platformName)
		{
			var rendererPath = Path.Combine(Platform.BinDir, "OpenRA.Platforms." + platformName + ".dll");

			var loader = new AssemblyLoader(rendererPath);
			var platformType = loader.LoadDefaultAssembly().GetTypes().SingleOrDefault(t => typeof(IPlatform).IsAssignableFrom(t));

			if (platformType == null)
				throw new InvalidOperationException("Platform dll must include exactly one IPlatform implementation.");

			return (IPlatform)platformType.GetConstructor(Type.EmptyTypes).Invoke(null);
		}

		public static void InitializeMod(Manifest manifest, Arguments args)
		{
			// Clear static state if we have switched mods
			LobbyInfoChanged = () => { };
			ConnectionStateChanged = (om, p, conn) => { };
			BeforeGameStart = () => { };
			OnRemoteDirectConnect = endpoint => { };
			delayedActions = new ActionQueue();

			Ui.ResetAll();

			StopSimTicking();
			worldRenderer?.Dispose();
			worldRenderer = null;
			server?.Shutdown();
			OrderManager?.Dispose();

			if (ModData != null)
			{
				ModData.ModFiles.UnmountAll();
				ModData.Dispose();
			}

			ModData = null;

			Console.WriteLine($"Loading mod: {manifest.Id}");

			Sound.StopVideo();

			ModData = new ModData(manifest, Mods, true);

			LocalPlayerProfile = new LocalPlayerProfile(Path.Combine(Platform.SupportDir, Settings.Game.AuthProfile), ModData.GetOrCreate<PlayerDatabase>());

			if (!ModData.LoadScreen.BeforeLoad(ModData))
				return;

			ModData.InitializeLoaders(ModData.DefaultFileSystem);
			Renderer.InitializeFonts(ModData);

			using (new PerfTimer("LoadMaps"))
				ModData.MapCache.LoadMaps(ModData);

			Cursor?.Dispose();
			Cursor = new CursorManager(ModData);

			var metadata = ModData.Manifest.Metadata;
			if (!string.IsNullOrEmpty(metadata.WindowTitleTranslated))
				Renderer.Window.SetWindowTitle(metadata.WindowTitleTranslated);

			PerfHistory.Items["render"].HasNormalTick = false;
			PerfHistory.Items["batches"].HasNormalTick = false;
			PerfHistory.Items["render_world"].HasNormalTick = false;
			PerfHistory.Items["render_widgets"].HasNormalTick = false;
			PerfHistory.Items["render_flip"].HasNormalTick = false;
			PerfHistory.Items["terrain_lighting"].HasNormalTick = false;

			JoinLocal();

			ModData.LoadScreen.StartGame(args);
		}

		public static void LoadEditor(string uid)
		{
			JoinLocal();
			StartGame(uid, WorldType.Editor);
		}

		public static void LoadEditor(Map map)
		{
			JoinLocal();
			StartGame(map, WorldType.Editor);
		}

		public static void LoadShellMap()
		{
			var shellmap = ChooseShellmap();
			using (new PerfTimer("StartGame"))
			{
				StartGame(shellmap, WorldType.Shellmap);
				OnShellmapLoaded();
			}
		}

		static string ChooseShellmap()
		{
			var shellmaps = ModData.MapCache
				.Where(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Shellmap))
				.Select(m => m.Uid);

			var shellmap = shellmaps.RandomOrDefault(CosmeticRandom);
			if (shellmap == null)
				throw new InvalidDataException("No valid shellmaps available");

			return shellmap;
		}

		public static void SwitchToExternalMod(ExternalMod mod, string[] launchArguments = null, Action onFailed = null)
		{
			try
			{
				var path = mod.LaunchPath;
				var args = launchArguments != null ? mod.LaunchArgs.Append(launchArguments) : mod.LaunchArgs;
				if (modLaunchWrapper != null)
				{
					path = modLaunchWrapper;
					args = new[] { mod.LaunchPath }.Concat(args);
				}

				var p = Process.Start(path, args.Select(a => "\"" + a + "\"").JoinWith(" "));
				if (p == null || p.HasExited)
					onFailed();
				else
				{
					p.Close();
					Exit();
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", "Failed to switch to external mod.");
				Log.Write("debug", "Error was: " + e.Message);
				onFailed();
			}
		}

		static RunStatus state = RunStatus.Running;
		public static event Action OnQuit = () => { };

		// Note: These delayed actions should only be used by widgets or disposing objects
		// - things that depend on a particular world should be queuing them on the world actor.
		static volatile ActionQueue delayedActions = new();

		public static void RunAfterTick(Action a) { delayedActions.Add(a, RunTime); }
		public static void RunAfterDelay(int delayMilliseconds, Action a) { delayedActions.Add(a, RunTime + delayMilliseconds); }

		// Decoupled rendering: true when the world is ticked on the background sim thread (toggle on +
		// threaded GL + in-progress game). When false the world ticks inline on the main thread (main IS the sim
		// thread), so callbacks that must run "on the sim thread" can run inline instead of being marshaled.
		public static bool IsDecoupledRunning => decoupledRunning;

		// Decoupled rendering: true on the main (render/UI/input) thread, false on the background sim
		// thread. Sim-thread code that must touch main-thread-only state (the widget tree, order generators, Lua UI
		// globals) checks this and marshals the mutation via RunAfterTick. True before ModData exists (startup is on
		// the main thread). Public so OpenRA.Mods.Common can reach it (ModData.IsOnMainThread is internal).
		public static bool IsOnMainThread => ModData == null || ModData.IsOnMainThread;

		static void TakeScreenshotInner()
		{
			using (new PerfTimer("Renderer.SaveScreenshot"))
			{
				var mod = ModData.Manifest.Metadata;
				var directory = Path.Combine(Platform.SupportDir, "Screenshots", ModData.Manifest.Id, mod.Version);
				Directory.CreateDirectory(directory);

				var filename = TimestampedFilename(true);
				var path = Path.Combine(directory, $"{filename}.png");
				Log.Write("debug", "Taking screenshot " + path);

				Renderer.SaveScreenshot(path);
				TextNotificationsManager.Debug(FluentProvider.GetMessage(SavedScreenshot, "filename", filename));
			}
		}

		static void InnerLogicTick(OrderManager orderManager, bool tickWorld = true)
		{
			// Split into the UI half and the world/simulation half. They are independent (gated on separate
			// LastTickTimes) and target different threads under Decoupled rendering: TickUi stays on
			// the main render+UI+input thread, TickWorld moves to the background sim thread. Computed from a
			// single 'tick' timestamp so the split is behaviour-identical to the previous combined method.
			// When tickWorld is false the world is ticked elsewhere (the sim thread), so only UI is advanced here.
			var tick = RunTime;
			TickUi(orderManager, tick);
			if (tickWorld)
				TickWorld(orderManager, tick);
		}

		// UI + cursor ticking. Decoupled rendering: runs on the main (render+UI+input) thread.
		static void TickUi(OrderManager orderManager, long tick)
		{
			var world = orderManager.World;

			if (Ui.LastTickTime.ShouldAdvance(tick))
			{
				Ui.LastTickTime.AdvanceTickTime(tick);
				Sync.RunUnsynced(world, Ui.Tick);
				Cursor.Tick();
			}
		}

		// Order processing + world simulation. Decoupled rendering: runs on the background sim thread.
		static void TickWorld(OrderManager orderManager, long tick)
		{
			var world = orderManager.World;

			if (orderManager.LastTickTime.ShouldAdvance(tick))
			{
				if (orderManager.GameStarted && orderManager.LocalFrameNumber == 0)
					PerfHistory.Reset(); // Remove history that occurred whilst the new game was loading.

				using (var sample = new PerfSample("tick_time"))
				{
					orderManager.LastTickTime.AdvanceTickTime(tick);

					Sound.Tick();

					Sync.RunUnsynced(world, orderManager.TickImmediate);

					if (world == null)
					{
						if (orderManager.GameStarted)
							PerfHistory.Reset(); // Remove old history when a new game starts.
						return;
					}

					if (orderManager.TryTick())
					{
						Sync.RunUnsynced(world, () => world.OrderGenerator.Tick(world));

						// Time the simulation step in isolation (excludes the lockstep wait, which TryTick
						// already cleared) to expose a clean sim-compute signal for adaptive game-speed.
						var worldTickStart = RunTime;
						world.Tick();
						if (world == OrderManager.World)
							LastWorldTickTimeMs = RunTime - worldTickStart;

						PerfHistory.Tick(!world.Paused);
					}

					// Wait until we have done our first world Tick before TickRendering
					if (orderManager.LocalFrameNumber > 0)
						Sync.RunUnsynced(world, () => world.TickRender(worldRenderer));
				}

				benchmark?.Tick(LocalTick);
			}
		}

		static void LogicTick()
		{
			PerformDelayedActions();

			if (OrderManager.Connection is NetworkConnection nc && nc.ConnectionState != lastConnectionState)
			{
				lastConnectionState = nc.ConnectionState;
				ConnectionStateChanged(OrderManager, null, nc);
			}

			// Decoupled rendering: the world simulation is ticked on the background sim thread, so the
			// main thread only advances UI here. Otherwise (default) tick UI + world inline as before.
			var tickWorldInline = !decoupledRunning;
			InnerLogicTick(OrderManager, tickWorldInline);
			if (worldRenderer != null && OrderManager.World != worldRenderer.World)
				InnerLogicTick(worldRenderer.World.OrderManager, tickWorldInline);
		}

		// The background sim thread: ticks ONLY the world simulation (TickWorld), serialized against the
		// main thread's world rendering via WorldAccessLock. Runs for the app lifetime; only does work while
		// decoupledRunning (threaded GL + DecoupledRendering + an in-progress game). When it is not running,
		// the main thread ticks the world inline as usual, so there is never a double tick. TickWorld self-gates
		// on LastTickTime, so the lock is held only for the duration of an actual tick.
		static void SimThreadLoop()
		{
			while (state == RunStatus.Running)
			{
				// Only take the lock when a world tick is actually due. TickWorld self-gates on LastTickTime, but
				// checking that INSIDE the lock would mean acquiring it every iteration (~1ms), which starves the
				// main thread's TryEnter and causes render stutter. So peek lock-free here, then re-check
				// decoupledRunning under the lock so a world teardown (StopSimTicking) can't race a tick.
				if (decoupledRunning && OrderManager.LastTickTime.ShouldAdvance(RunTime))
				{
					try
					{
						lock (WorldAccessLock)
						{
							if (decoupledRunning)
							{
								var tick = RunTime;
								var om = OrderManager;
								var wr = worldRenderer;
								if (om.World != null)
									TickWorld(om, tick);
								if (wr != null && om.World != wr.World && wr.World != null)
									TickWorld(wr.World.OrderManager, tick);
							}
						}
					}
					catch (Exception e)
					{
						// Hand the exception to the main thread, which re-throws it into the normal fatal-error path
						// (exception log + crash dialog). Stop ticking so we don't spin on a broken world.
						simThreadException = ExceptionDispatchInfo.Capture(e);
						decoupledRunning = false;
						return;
					}
				}

				// Poll at tick resolution while decoupled; idle near-dormant when off (the main thread ticks the
				// world inline then, so this thread has nothing to do and should not burn a core polling a flag).
				Thread.Sleep(decoupledRunning ? 1 : 16);
			}
		}

		// Decoupled rendering: stop the background sim thread from ticking the world before it is disposed or swapped.
		// Setting decoupledRunning=false prevents any new tick from starting; taking WorldAccessLock once is a
		// barrier that waits out a tick already in progress. After this returns, the main thread can safely
		// dispose/replace the world; decoupledRunning is recomputed by the loop once a new game is in progress.
		static void StopSimTicking()
		{
			decoupledRunning = false;
			lock (WorldAccessLock) { }
		}

		public static void PerformDelayedActions()
		{
			delayedActions.PerformActions(RunTime);
		}

		public static void TakeScreenshot()
		{
			takeScreenshot = true;
		}

		static void RenderTick()
		{
			using (new PerfSample("render"))
			{
				++RenderFrame;

				// Mark the render phase so render-only visibility checks (Cloak.IsVisible) can take a
				// cheap cached path. Cleared at the end of the block; the sim tick always runs with this
				// false, so determinism of the simulation path is untouched.
				if (worldRenderer != null)
					worldRenderer.World.IsRenderTick = true;

				// worldRenderer is null during the initial install/download screen; world rendering is disabled
				// while the loading screen is displayed.
				var canDrawWorld = worldRenderer != null && !worldRenderer.World.IsLoadingGameSave;

				// drawWorld stays false on a decoupled UI-only frame (the sim thread was mid-tick so we couldn't
				// take the world lock); we then keep the world buffer from a previous frame and refresh only the UI.
				var drawWorld = false;

				// Prepare renderables (i.e. render voxels) before calling BeginFrame
				using (new PerfSample("render_prepare"))
				{
					worldRenderer?.BeginFrame();

					if (canDrawWorld)
					{
						// Decoupled rendering: hold WorldAccessLock ONLY around the world-reading prepare — NOT the later
						// Draw/present/vsync. Holding it across the whole render starved the sim thread for whole
						// frames (measured tick gaps up to 124ms -> lurchy motion). TryEnter never blocks: if the
						// sim is mid-tick we skip world prepare this frame and fall back to a UI-only frame.
						if (!decoupledRunning)
						{
							worldRenderer.Viewport.Tick();
							worldRenderer.PrepareRenderables();
							drawWorld = true;
						}
						else if (Monitor.TryEnter(WorldAccessLock))
						{
							try
							{
								worldRenderer.Viewport.Tick();
								worldRenderer.PrepareRenderables();
								drawWorld = true;
							}
							finally
							{
								Monitor.Exit(WorldAccessLock);
							}
						}
					}

					Ui.PrepareRenderables();
					worldRenderer?.EndFrame();
				}

				// Use worldRenderer.World instead of OrderManager.World to avoid a rendering mismatch while processing orders
				if (drawWorld)
				{
					Renderer.BeginWorld(worldRenderer.Viewport.CenterLocation, worldRenderer.Viewport.ViewportSize);
					Sound.SetListenerPosition(worldRenderer.Viewport.CenterPosition, worldRenderer.Viewport.WorldHalfWidth);
					using (new PerfSample("render_world"))
						worldRenderer.Draw();
				}

				using (new PerfSample("render_widgets"))
				{
					Renderer.BeginUI(compositeRetainedWorld: !drawWorld);

					if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
						worldRenderer.DrawAnnotations();

					Ui.Draw();

					if (HideCursor)
						Cursor?.SetCursor(null);
					else
					{
						Cursor?.SetCursor(Ui.Root.GetCursorOuter(Viewport.LastMousePos) ?? "default");
						Cursor?.Render(Renderer);
					}
				}

				using (new PerfSample("render_flip"))
					Renderer.EndFrame(new DefaultInputHandler(OrderManager.World));

				if (takeScreenshot)
				{
					takeScreenshot = false;
					TakeScreenshotInner();
				}
			}

			if (worldRenderer != null)
				worldRenderer.World.IsRenderTick = false;

			var isActive = !(worldRenderer?.World.Paused ?? true);
			PerfHistory.Items["render"].Tick(isActive);
			PerfHistory.Items["batches"].Tick(isActive);
			PerfHistory.Items["render_world"].Tick(isActive);
			PerfHistory.Items["render_widgets"].Tick(isActive);
			PerfHistory.Items["render_flip"].Tick(isActive);
			PerfHistory.Items["terrain_lighting"].Tick(isActive);
		}

		static void Loop()
		{
			// The game loop mainly does two things: logic updates and
			// drawing on the screen.
			// ---
			// We ideally want the logic to run every 'Timestep' ms and
			// rendering to be done at 'MaxFramerate', so 1000 / MaxFramerate ms.
			// Any additional free time is used in 'Sleep' so we don't
			// consume more CPU/GPU resources than necessary.
			// ---
			// In case logic or rendering takes more time than the ideal
			// and we're getting behind, we can skip rendering some frames
			// but there's a fail-safe minimum FPS to make sure the screen
			// gets updated at least that often.
			// ---
			// TODO: Separate world/UI rendering
			// It would be nice to separate the world rendering from the UI rendering
			// so that we can update the UI more often than the world. This would
			// help make the game playable (mouse/controls) even in low world
			// framerates.
			// It's not possible at the moment because the render buffer is cleared
			// before rendering and we don't keep the last rendered world buffer.

			// When the logic has fallen behind by this much, skip the pending
			// updates and start fresh.
			// For example, if we want to update logic every 10 ms but each loop
			// temporarily takes 100 ms, the 'nextLogic' timestamp will be too low
			// and the current timestamp ('now') will have moved on. Even if the
			// update time returns to normal, it will take a long time to catch up
			// (if ever).
			// This also means that the 'logicInterval' cannot be longer than this
			// value.
			const int MaxLogicTicksBehind = 250;

			// Try to maintain at least this many FPS during replays, even if it slows down logic.
			// However, if the user has enabled a framerate limit that is even lower
			// than this, then that limit will be used.
			const int MinReplayFps = 10;

			// Timestamps for when the next logic and rendering should run
			var nextLogic = RunTime;
			var nextRender = RunTime;
			var forcedNextRender = RunTime;
			var renderBeforeNextTick = false;

			// Decoupled rendering: start the background sim thread. It only ticks the world while decoupledRunning is set
			// (threaded GL + DecoupledRendering + an in-progress game); otherwise it idles and the main thread
			// ticks the world inline as usual.
			simThread = new Thread(SimThreadLoop)
			{
				Name = "Cameo Sim Thread",
				IsBackground = true
			};
			simThread.Start();

			while (state == RunStatus.Running)
			{
				// Re-throw any exception the sim thread captured, so it flows through the normal main-thread
				// fatal-error path (exception log + crash dialog) instead of vanishing.
				simThreadException?.Throw();

				var logicInterval = Ui.Timestep;
				var logicWorld = worldRenderer?.World;

				// ReplayTimestep = 0 means the replay is paused: we need to keep logicInterval as UI.Timestep to avoid breakage
				if (logicWorld != null && (!logicWorld.IsReplay || logicWorld.ReplayTimestep != 0))
					logicInterval = logicWorld == OrderManager.World ? OrderManager.SuggestedTimestep : logicWorld.Timestep;

				// Ideal time between screen updates
				var renderInterval = logicInterval;
				if (!Settings.Graphics.CapFramerateToGameFps)
				{
					var maxFramerate = Settings.Graphics.CapFramerate ? Settings.Graphics.MaxFramerate.Clamp(1, 1000) : 1000;
					renderInterval = 1000 / maxFramerate;
				}

				// Tick as fast as possible while restoring game saves, capping rendering at 5 FPS
				if (OrderManager.World != null && OrderManager.World.IsLoadingGameSave)
				{
					logicInterval = 1;
					renderInterval = 200;
				}

				var now = RunTime;

				// Decoupled rendering: decide whether the world is ticked on the sim thread this iteration. Requires a threaded
				// GL context (so the sim and render threads can both feed the GL queue), the setting enabled, and
				// an in-progress game. When false, the main thread ticks the world inline (default behaviour).
				var shouldDecouple = Settings.Graphics.DecoupledRendering
					&& Renderer.Context.IsThreaded
					&& OrderManager.World != null
					&& OrderManager.GameStarted
					&& !OrderManager.World.IsLoadingGameSave;

				// On the true->false edge (toggle off mid-game, game end, save load) a sim tick may still be in
				// flight under WorldAccessLock. Barrier it out BEFORE the main thread resumes inline ticking, or the
				// two threads can run World.Tick (RNG, orders, sync) concurrently. StopSimTicking sets the flag false
				// and waits out the in-flight tick. The false->true edge needs no barrier (nothing is in flight).
				if (decoupledRunning && !shouldDecouple)
					StopSimTicking();
				else
					decoupledRunning = shouldDecouple;

				// If the logic has fallen behind too much, skip it and catch up
				if (now - nextLogic > MaxLogicTicksBehind)
					nextLogic = now;

				// When's the next update (logic or render)
				var nextUpdate = Math.Min(nextLogic, nextRender);
				if (now >= nextUpdate)
				{
					var forceRender = renderBeforeNextTick || now >= forcedNextRender;

					if (now >= nextLogic && !renderBeforeNextTick)
					{
						nextLogic += logicInterval;

						LogicTick();

						// Force at least one render per tick during regular gameplay
						if (OrderManager.World != null && !OrderManager.World.IsLoadingGameSave && !OrderManager.World.IsReplay)
							renderBeforeNextTick = true;
					}

					var haveSomeTimeUntilNextLogic = now < nextLogic;
					var isTimeToRender = now >= nextRender;
					if (!Renderer.WindowIsSuspended)
					{
						if (isTimeToRender || forceRender)
						{
							if (haveSomeTimeUntilNextLogic || forceRender)
							{
								// Decoupled rendering: RenderTick takes the world lock itself, only around the world-reading prepare phase
								// (not the whole render), so a slow render frame can't starve the sim thread.
								RenderTick();
							}

							nextRender = now + renderInterval;

							// Pick the minimum allowed FPS (the lower between 'minReplayFPS'
							// and the user's max frame rate) and convert it to maximum time
							// allowed between screen updates.
							// We do this before rendering to include the time rendering takes
							// in this interval.
							var maxRenderInterval = Math.Max(1000 / MinReplayFps, renderInterval);
							forcedNextRender = now + maxRenderInterval;

							renderBeforeNextTick = false;
						}
					}
					else
					{
						// Simulate a render tick if it was time to render but we skip actually rendering
						if (isTimeToRender || forceRender)
						{
							// Make sure that nextUpdate is set to a proper minimum interval
							nextRender = now + renderInterval;

							// Still process SDL events to allow a restore to come through
							Renderer.Window.PumpInput(new NullInputHandler());

							// Ensure that we still logic tick despite not rendering
							renderBeforeNextTick = false;
						}
						else
						{
							// Avoid busy wait.
							Thread.Sleep((int)(nextRender - now));
						}
					}
				}
				else
					Thread.Sleep((int)(nextUpdate - now));
			}
		}

		static RunStatus Run()
		{
			if (Settings.Graphics.MaxFramerate < 1)
			{
				Settings.Graphics.MaxFramerate = new GraphicSettings().MaxFramerate;
				Settings.Graphics.CapFramerate = false;
			}

			try
			{
				Loop();
			}
			finally
			{
				// Decoupled rendering: stop the background sim thread BEFORE disposing OrderManager. If Loop() threw on the main
				// thread, a sim tick may still be mid-TickWorld touching OrderManager/world/global state; the barrier
				// in StopSimTicking waits it out. Loop() has exited, so nothing re-enables decoupledRunning here.
				StopSimTicking();

				// Ensure that the active replay is properly saved
				OrderManager?.Dispose();
			}

			worldRenderer?.Dispose();
			ModData.Dispose();
			ChromeProvider.Deinitialize();

			Sound.Dispose();
			Renderer.Dispose();

			OnQuit();

			return state;
		}

		public static void Exit()
		{
			state = RunStatus.Success;
		}

		public static void Disconnect()
		{
			// Decoupled rendering: quiesce the background sim thread before tearing down the world. Restart()/leave call this
			// mid-game with decoupledRunning still true (shouldDecouple does not gate on game-over), so a sim tick
			// could be mid-TickWorld on the very world/OrderManager about to be disposed. StopSimTicking() barriers
			// out the in-flight tick first; this runs on the main thread, so nothing re-enables decoupling before the
			// disposes below. No-op when decoupling is off.
			StopSimTicking();

			// drop any callbacks queued via RunAfterTick (marshaled notifications / SelectionChanged / mission
			// text / GameOver UI) before we dispose the world/UI. They captured the OLD world/widgets and would
			// otherwise fire in a later LogicTick against disposed state (Restart/leave reaches here, then Ui.ResetAll).
			// Safe: the sim is quiesced by the barrier above, so nothing is concurrently enqueuing, and these are
			// display-only client callbacks.
			delayedActions.Clear();

			OrderManager.World?.TraitDict.PrintReport();

			OrderManager.Dispose();
			CloseServer();
			JoinLocal();
		}

		public static void CloseServer()
		{
			server?.Shutdown();
		}

		public static T CreateObject<T>(string name)
		{
			return ModData.ObjectCreator.CreateObject<T>(name);
		}

		public static ConnectionTarget CreateServer(ServerSettings settings)
		{
			var endpoints = new List<IPEndPoint>
			{
				new(IPAddress.IPv6Any, settings.ListenPort),
				new(IPAddress.Any, settings.ListenPort)
			};
			server = new Server.Server(endpoints, settings, ModData, ServerType.Multiplayer);

			return server.GetEndpointForLocalConnection();
		}

		public static ConnectionTarget CreateLocalServer(string map, bool isSkirmish = false)
		{
			var settings = new ServerSettings()
			{
				Name = "Skirmish Game",
				Map = map,
				AdvertiseOnline = false,
				AdvertiseOnLocalNetwork = !isSkirmish
			};

			// Always connect to local games using the same loopback connection
			// Exposing multiple endpoints introduces a race condition on the client's PlayerIndex (sometimes 0, sometimes 1)
			// This would break the Restart button, which relies on the PlayerIndex always being the same for local servers
			var endpoints = new List<IPEndPoint>
			{
				new(IPAddress.Loopback, 0)
			};
			server = new Server.Server(endpoints, settings, ModData, isSkirmish ? ServerType.Skirmish : ServerType.Local);

			return server.GetEndpointForLocalConnection();
		}

		public static bool IsCurrentWorld(World world)
		{
			return OrderManager != null && OrderManager.World == world && !world.Disposing;
		}

		public static bool SetClipboardText(string text)
		{
			return Renderer.Window.SetClipboardText(text);
		}

		public static void BenchmarkMode(string prefix)
		{
			benchmark = new Benchmark(prefix);
		}

		public static void LoadMap(string launchMap)
		{
			var orders = new List<Order>
			{
				Order.Command("option gamespeed default"),
				Order.Command($"state {Session.ClientState.Ready}")
			};

			var map = ModData.MapCache.SingleOrDefault(m => m.Uid == launchMap || Path.GetFileName(m.Path) == launchMap);
			if (map == null)
				throw new ArgumentException($"Could not find map '{launchMap}'.");

			CreateAndStartLocalServer(map.Uid, orders);
		}

		public static void FinishBenchmark()
		{
			if (benchmark != null)
			{
				benchmark.Write();
				Exit();
			}
		}
	}

	public static class CurrentServerSettings
	{
		public static string Password;
		public static ConnectionTarget Target;
		public static ExternalMod ServerExternalMod;
	}
}
