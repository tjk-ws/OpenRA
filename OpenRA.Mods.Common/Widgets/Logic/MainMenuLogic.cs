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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenRA.Mods.Common.FileSystem;
using OpenRA.Network;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class MainMenuLogic : ChromeLogic
	{
		[FluentReference]
		const string LoadingNews = "label-loading-news";

		[FluentReference("message")]
		const string NewsRetrivalFailed = "label-news-retrieval-failed";

		[FluentReference("message")]
		const string NewsParsingFailed = "label-news-parsing-failed";

		[FluentReference("author", "datetime")]
		const string AuthorDateTime = "label-author-datetime";

		protected enum MenuType { Main, Singleplayer, Extras, MapEditor, StartupPrompts, None }

		protected enum MenuPanel { None, Missions, Skirmish, Multiplayer, MapEditor, Replays, GameSaves }

		protected MenuType menuType = MenuType.Main;
		readonly Widget rootMenu;
		readonly ScrollPanelWidget newsPanel;
		readonly int maxNewsHeight;
		readonly Widget newsTemplate;
		readonly LabelWidget newsStatus;
		readonly ModData modData;

		// Update news once per game launch
		static bool fetchedNews;

		protected static MenuPanel lastGameState = MenuPanel.None;

		bool newsOpen;

		void SwitchMenu(MenuType type)
		{
			menuType = type;

			DiscordService.UpdateStatus(DiscordState.InMenu);

			// Update button mouseover
			Game.RunAfterTick(Ui.ResetTooltips);
		}

		[ObjectCreator.UseCtor]
		public MainMenuLogic(Widget widget, World world, ModData modData)
		{
			this.modData = modData;

			rootMenu = widget;

			// Menu buttons
			var mainMenu = widget.Get("MAIN_MENU");
			mainMenu.IsVisible = () => menuType == MenuType.Main;

			mainMenu.Get<ButtonWidget>("SINGLEPLAYER_BUTTON").OnClick = () => SwitchMenu(MenuType.Singleplayer);

			mainMenu.Get<ButtonWidget>("MULTIPLAYER_BUTTON").OnClick = OpenMultiplayerPanel;

			var contentButton = mainMenu.GetOrNull<ButtonWidget>("CONTENT_BUTTON");
			if (contentButton != null)
			{
				var contentInstaller = modData.FileSystemLoader as IFileSystemExternalContent;
				contentButton.Disabled = contentInstaller == null;
				contentButton.OnClick = () => contentInstaller?.ManageContent(modData);
			}

			mainMenu.Get<ButtonWidget>("SETTINGS_BUTTON").OnClick = () =>
			{
				SwitchMenu(MenuType.None);
				Game.OpenWindow("SETTINGS_PANEL", new WidgetArgs
				{
					{ "onExit", () => SwitchMenu(MenuType.Main) }
				});
			};

			mainMenu.Get<ButtonWidget>("EXTRAS_BUTTON").OnClick = () => SwitchMenu(MenuType.Extras);

			mainMenu.Get<ButtonWidget>("QUIT_BUTTON").OnClick = Game.Exit;

			// Singleplayer menu
			var singleplayerMenu = widget.Get("SINGLEPLAYER_MENU");
			singleplayerMenu.IsVisible = () => menuType == MenuType.Singleplayer;

			var missionsButton = singleplayerMenu.Get<ButtonWidget>("MISSIONS_BUTTON");
			missionsButton.OnClick = () => OpenMissionBrowserPanel(modData.MapCache.PickLastModifiedMap(MapVisibility.MissionSelector));

			var hasCampaign = modData.Manifest.Missions.Length > 0;
			var hasMissions = modData.MapCache
				.Any(p => p.Status == MapStatus.Available && p.Visibility.HasFlag(MapVisibility.MissionSelector));

			missionsButton.Disabled = !hasCampaign && !hasMissions;

			var hasMaps = modData.MapCache.Any(p => p.Visibility.HasFlag(MapVisibility.Lobby));
			var skirmishButton = singleplayerMenu.Get<ButtonWidget>("SKIRMISH_BUTTON");
			skirmishButton.OnClick = StartSkirmishGame;
			skirmishButton.Disabled = !hasMaps;

			var loadButton = singleplayerMenu.Get<ButtonWidget>("LOAD_BUTTON");
			loadButton.IsDisabled = () => !LoadGameBrowserLogic.IsLoadPanelEnabled(modData.Manifest);
			loadButton.OnClick = OpenGameSaveBrowserPanel;

			var encyclopediaButton = singleplayerMenu.GetOrNull<ButtonWidget>("ENCYCLOPEDIA_BUTTON");
			if (encyclopediaButton != null)
				encyclopediaButton.OnClick = OpenEncyclopediaPanel;

			singleplayerMenu.Get<ButtonWidget>("BACK_BUTTON").OnClick = () => SwitchMenu(MenuType.Main);

			// Extras menu
			var extrasMenu = widget.Get("EXTRAS_MENU");
			extrasMenu.IsVisible = () => menuType == MenuType.Extras;

			extrasMenu.Get<ButtonWidget>("REPLAYS_BUTTON").OnClick = OpenReplayBrowserPanel;

			extrasMenu.Get<ButtonWidget>("MUSIC_BUTTON").OnClick = () =>
			{
				SwitchMenu(MenuType.None);
				Ui.OpenWindow("MUSIC_PANEL", new WidgetArgs
				{
					{ "onExit", () => SwitchMenu(MenuType.Extras) },
					{ "world", world }
				});
			};

			extrasMenu.Get<ButtonWidget>("MAP_EDITOR_BUTTON").OnClick = () => SwitchMenu(MenuType.MapEditor);

			var assetBrowserButton = extrasMenu.GetOrNull<ButtonWidget>("ASSETBROWSER_BUTTON");
			if (assetBrowserButton != null)
				assetBrowserButton.OnClick = () =>
				{
					SwitchMenu(MenuType.None);
					Game.OpenWindow("ASSETBROWSER_PANEL", new WidgetArgs
					{
						{ "onExit", () => SwitchMenu(MenuType.Extras) },
					});
				};

			extrasMenu.Get<ButtonWidget>("CREDITS_BUTTON").OnClick = () =>
			{
				SwitchMenu(MenuType.None);
				Ui.OpenWindow("CREDITS_PANEL", new WidgetArgs
				{
					{ "onExit", () => SwitchMenu(MenuType.Extras) },
				});
			};

			extrasMenu.Get<ButtonWidget>("BACK_BUTTON").OnClick = () => SwitchMenu(MenuType.Main);

			// Map editor menu
			var mapEditorMenu = widget.Get("MAP_EDITOR_MENU");
			mapEditorMenu.IsVisible = () => menuType == MenuType.MapEditor;

			// Loading into the map editor
			Game.BeforeGameStart += RemoveShellmapUI;

			var onSelect = new Action<string>(uid =>
			{
				if (modData.MapCache[uid].Status != MapStatus.Available)
					SwitchMenu(MenuType.Extras);
				else
					LoadMapIntoEditor(modData.MapCache[uid].Uid);
			});

			var newMapButton = widget.Get<ButtonWidget>("NEW_MAP_BUTTON");
			newMapButton.OnClick = () =>
			{
				SwitchMenu(MenuType.None);
				Game.OpenWindow("NEW_MAP_BG", new WidgetArgs()
				{
					{ "onSelect", onSelect },
					{ "onExit", () => SwitchMenu(MenuType.MapEditor) }
				});
			};

			var loadMapButton = widget.Get<ButtonWidget>("LOAD_MAP_BUTTON");
			loadMapButton.OnClick = () =>
			{
				SwitchMenu(MenuType.None);
				Game.OpenWindow("MAPCHOOSER_PANEL", new WidgetArgs()
				{
					{ "initialMap", null },
					{ "initialGeneratedMap", (MapGenerationArgs)null },
					{ "remoteMapPool", null },
					{ "initialTab", MapClassification.User },
					{ "onExit", () => SwitchMenu(MenuType.MapEditor) },
					{ "onSelect", onSelect },
					{ "onSelectGenerated", null },
					{ "filter", MapVisibility.Lobby | MapVisibility.Shellmap | MapVisibility.MissionSelector },
				});
			};

			loadMapButton.Disabled = !hasMaps;

			mapEditorMenu.Get<ButtonWidget>("BACK_BUTTON").OnClick = () => SwitchMenu(MenuType.Extras);

			var newsBG = widget.GetOrNull("NEWS_BG");
			if (newsBG != null)
			{
				newsBG.IsVisible = () => Game.Settings.Game.FetchNews && menuType != MenuType.None && menuType != MenuType.StartupPrompts;

				newsPanel = Ui.LoadWidget<ScrollPanelWidget>("NEWS_PANEL", null, []);
				newsTemplate = newsPanel.Get("NEWS_ITEM_TEMPLATE");
				newsPanel.RemoveChild(newsTemplate);
				maxNewsHeight = newsPanel.Bounds.Height;

				newsStatus = newsPanel.Get<LabelWidget>("NEWS_STATUS");
				SetNewsStatus(FluentProvider.GetMessage(LoadingNews));
			}

			Game.OnRemoteDirectConnect += OnRemoteDirectConnect;

			// Check for updates in the background
			var webServices = modData.GetOrCreate<WebServices>();
			if (Game.Settings.Debug.CheckVersion)
				webServices.CheckModVersion();

			var updateLabel = rootMenu.GetOrNull("UPDATE_NOTICE");
			if (updateLabel != null)
				updateLabel.IsVisible = () => !newsOpen && menuType != MenuType.None &&
					menuType != MenuType.StartupPrompts &&
					webServices.ModVersionStatus == ModVersionStatus.Outdated;

			menuType = MenuType.StartupPrompts;

			void OnIntroductionComplete()
			{
				void OnSysInfoComplete()
				{
					LoadAndDisplayNews(webServices, newsBG);
					SwitchMenu(MenuType.Main);
				}

				if (SystemInfoPromptLogic.ShouldShowPrompt())
				{
					Ui.OpenWindow("MAINMENU_SYSTEM_INFO_PROMPT", new WidgetArgs
					{
						{ "onComplete", OnSysInfoComplete }
					});
				}
				else
					OnSysInfoComplete();
			}

			if (IntroductionPromptLogic.ShouldShowPrompt())
			{
				Game.OpenWindow("MAINMENU_INTRODUCTION_PROMPT", new WidgetArgs
				{
					{ "onComplete", OnIntroductionComplete }
				});
			}
			else
				OnIntroductionComplete();

			Game.OnShellmapLoaded += OpenMenuBasedOnLastGame;

			DiscordService.UpdateStatus(DiscordState.InMenu);
		}

		void LoadAndDisplayNews(WebServices webServices, Widget newsBG)
		{
			if (newsBG == null || !Game.Settings.Game.FetchNews)
				return;

			var fromGitHub = webServices.GameNewsFromGitHubReleases;
			var cacheFile = Path.Combine(Platform.SupportDir,
				fromGitHub ? webServices.GameNewsReleasesFileName : webServices.GameNewsFileName);

			var currentNews = fromGitHub
				? (File.Exists(cacheFile) ? ParseGitHubReleases(File.ReadAllText(cacheFile)) : null)
				: ParseNews(cacheFile);
			if (currentNews != null)
				DisplayNews(currentNews);

			var newsButton = newsBG.GetOrNull<DropDownButtonWidget>("NEWS_BUTTON");
			if (newsButton != null)
			{
				if (!fetchedNews)
				{
					Task.Run(async () =>
					{
						try
						{
							var client = HttpClientFactory.Create();

							string response;
							if (fromGitHub)
							{
								// The GitHub API requires a User-Agent header and is not version-filtered.
								client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenRA");
								client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
								response = await client.GetStringAsync(webServices.GameNews);
							}
							else
							{
								// Send the mod and engine version to support version-filtered news (update prompts)
								var url = new HttpQueryBuilder(webServices.GameNews)
								{
									{ "version", Game.EngineVersion },
									{ "mod", modData.Manifest.Id },
									{ "modversion", modData.Manifest.Metadata.Version }
								}.ToString();

								// Parameter string is blank if the player has opted out
								url += SystemInfoPromptLogic.CreateParameterString();

								response = await client.GetStringAsync(url);
							}

							await File.WriteAllTextAsync(cacheFile, response);

							Game.RunAfterTick(() => // run on the main thread
							{
								fetchedNews = true;
								var newNews = fromGitHub ? ParseGitHubReleases(response) : ParseNews(cacheFile);
								if (newNews == null)
									return;

								DisplayNews(newNews);

								if (currentNews == null || newNews.Any(n => !currentNews.Select(c => c.DateTime).Contains(n.DateTime)))
									OpenNewsPanel(newsButton);
							});
						}
						catch (Exception e)
						{
							Game.RunAfterTick(() => // run on the main thread
								SetNewsStatus(FluentProvider.GetMessage(NewsRetrivalFailed, "message", e.Message)));
						}
					});
				}

				newsButton.OnClick = () => OpenNewsPanel(newsButton);
			}
		}

		void OpenNewsPanel(DropDownButtonWidget button)
		{
			newsOpen = true;
			button.AttachPanel(newsPanel, () => newsOpen = false);
		}

		void OnRemoteDirectConnect(ConnectionTarget endpoint)
		{
			SwitchMenu(MenuType.None);
			Ui.OpenWindow("MULTIPLAYER_PANEL", new WidgetArgs
			{
				{ "onStart", RemoveShellmapUI },
				{ "onExit", () => SwitchMenu(MenuType.Main) },
				{ "directConnectEndPoint", endpoint },
			});
		}

		static void LoadMapIntoEditor(string uid)
		{
			Game.LoadEditor(uid);

			DiscordService.UpdateStatus(DiscordState.InMapEditor);

			lastGameState = MenuPanel.MapEditor;
		}

		void SetNewsStatus(string message)
		{
			message = WidgetUtils.WrapText(message, newsStatus.Bounds.Width, Game.Renderer.Fonts[newsStatus.Font]);
			newsStatus.GetText = () => message;
		}

		sealed class NewsItem
		{
			public string Title;
			public string Author;
			public DateTime DateTime;
			public string Content;
		}

		NewsItem[] ParseNews(string path)
		{
			if (!File.Exists(path))
				return null;

			try
			{
				return MiniYaml.FromFile(path).Select(node =>
				{
					var nodesDict = node.Value.ToDictionary();
					return new NewsItem
					{
						Title = nodesDict["Title"].Value,
						Author = nodesDict["Author"].Value,
						DateTime = FieldLoader.GetValue<DateTime>("DateTime", node.Key),
						Content = nodesDict["Content"].Value
					};
				}).ToArray();
			}
			catch (Exception ex)
			{
				SetNewsStatus(FluentProvider.GetMessage(NewsParsingFailed, "message", ex.Message));
			}

			return null;
		}

		const int MaxGitHubReleases = 10;
		const int MaxReleaseBodyLength = 1500;

		sealed class GitHubAuthor
		{
			[JsonPropertyName("login")]
			public string Login { get; set; }
		}

		sealed class GitHubRelease
		{
			[JsonPropertyName("name")]
			public string Name { get; set; }

			[JsonPropertyName("tag_name")]
			public string TagName { get; set; }

			[JsonPropertyName("published_at")]
			public DateTime PublishedAt { get; set; }

			[JsonPropertyName("body")]
			public string Body { get; set; }

			[JsonPropertyName("draft")]
			public bool Draft { get; set; }

			[JsonPropertyName("author")]
			public GitHubAuthor Author { get; set; }
		}

		NewsItem[] ParseGitHubReleases(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
				return null;

			try
			{
				var releases = JsonSerializer.Deserialize<GitHubRelease[]>(json);
				if (releases == null)
					return null;

				return releases
					.Where(r => !r.Draft)
					.OrderByDescending(r => r.PublishedAt)
					.Take(MaxGitHubReleases)
					.Select(r => new NewsItem
					{
						Title = string.IsNullOrEmpty(r.Name) ? r.TagName : r.Name,
						Author = r.Author?.Login ?? "",
						DateTime = r.PublishedAt,
						Content = CleanReleaseBody(r.Body)
					})
					.ToArray();
			}
			catch (Exception ex)
			{
				SetNewsStatus(FluentProvider.GetMessage(NewsParsingFailed, "message", ex.Message));
			}

			return null;
		}

		// GitHub release bodies are Markdown, but the news panel only renders unstyled, width-wrapped
		// text in a single font. This flattens the common Markdown syntax to clean plain text:
		// links/images become their visible label, emphasis and code markers are removed, and block
		// syntax (headings, quotes, rules, fences) is reduced to plain lines. List bullets are kept
		// as a visible dash.
		static string CleanReleaseBody(string body)
		{
			if (string.IsNullOrWhiteSpace(body))
				return "";

			var text = body.Replace("\r\n", "\n").Replace("\r", "\n");

			// HTML comments and code-fence markers (the fenced content stays as plain text).
			text = Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);
			text = Regex.Replace(text, "(?m)^[ \t]*```.*$", "");

			// Horizontal rules (---, ***, ___) on their own line.
			text = Regex.Replace(text, "(?m)^[ \t]*([-*_])\\1{2,}[ \t]*$", "");

			// Links and images -> their visible text (URLs are not clickable here).
			text = Regex.Replace(text, "<(https?://[^>\\s]+)>", "$1");      // autolink
			text = Regex.Replace(text, "!\\[[^\\]]*\\]\\([^)]*\\)", "");     // image -> dropped
			text = Regex.Replace(text, "\\[([^\\]]+)\\]\\([^)]*\\)", "$1");  // [text](url) -> text

			// Inline code and emphasis markers -> their content (underscores left alone for snake_case).
			text = Regex.Replace(text, "`([^`]+)`", "$1");
			text = Regex.Replace(text, "\\*\\*([^*]+?)\\*\\*", "$1");
			text = Regex.Replace(text, "(?<!\\*)\\*([^*\n]+?)\\*(?!\\*)", "$1");

			// Block syntax: headings, blockquotes, and list markers (kept as a visible dash).
			text = Regex.Replace(text, "(?m)^#{1,6}[ \t]*", "");
			text = Regex.Replace(text, "(?m)^[ \t]*>[ \t]?", "");
			text = Regex.Replace(text, "(?m)^([ \t]*)[*+][ \t]+", "$1- ");

			text = Regex.Replace(text, "[ \t]+\n", "\n");   // trailing spaces
			text = Regex.Replace(text, "\n{3,}", "\n\n");   // collapse large gaps
			text = text.Trim();

			if (text.Length > MaxReleaseBodyLength)
				text = text[..MaxReleaseBodyLength].TrimEnd() + "...";

			return text;
		}

		void DisplayNews(IEnumerable<NewsItem> newsItems)
		{
			newsPanel.RemoveChildren();
			SetNewsStatus("");

			foreach (var item in newsItems)
			{
				var newsItem = newsTemplate.Clone();

				var titleLabel = newsItem.Get<LabelWidget>("TITLE");
				var titleFont = Game.Renderer.Fonts[titleLabel.Font];
				var title = WidgetUtils.WrapText(item.Title ?? "", titleLabel.Bounds.Width, titleFont);
				titleLabel.GetText = () => title;
				titleLabel.Bounds.Height = titleFont.Measure(title).Y;

				var authorDateTimeLabel = newsItem.Get<LabelWidget>("AUTHOR_DATETIME");
				var authorDateTime = FluentProvider.GetMessage(AuthorDateTime,
					"author", item.Author,
					"datetime", item.DateTime.ToLocalTime().ToString(CultureInfo.CurrentCulture));

				authorDateTimeLabel.GetText = () => authorDateTime;

				// Lay the author/date and content out relative to the (possibly multi-line) title,
				// so long release names wrap cleanly instead of overflowing a fixed single-line slot.
				authorDateTimeLabel.Bounds.Y = titleLabel.Bounds.Y + titleLabel.Bounds.Height + 4;

				var contentLabel = newsItem.Get<LabelWidget>("CONTENT");
				var content = item.Content.Replace("\\n", "\n");
				content = WidgetUtils.WrapText(content, contentLabel.Bounds.Width, Game.Renderer.Fonts[contentLabel.Font]);
				contentLabel.GetText = () => content;
				contentLabel.Bounds.Y = authorDateTimeLabel.Bounds.Y + authorDateTimeLabel.Bounds.Height + 6;
				contentLabel.Bounds.Height = Game.Renderer.Fonts[contentLabel.Font].Measure(content).Y;

				var bottom = contentLabel.Bounds.Y + contentLabel.Bounds.Height;

				// Draw a thin divider in the gap so consecutive news items read as separate entries.
				var separator = newsItem.GetOrNull<ColorBlockWidget>("SEPARATOR");
				if (separator != null)
				{
					separator.Bounds.Y = bottom + 14;
					separator.Bounds.Width = contentLabel.Bounds.Width;
					bottom = separator.Bounds.Y + separator.Bounds.Height;
				}

				newsItem.Bounds.Height = bottom + 14;

				newsPanel.AddChild(newsItem);
				newsPanel.Layout.AdjustChildren();
				newsPanel.Bounds.Height = Math.Min(newsPanel.ContentHeight, maxNewsHeight);
			}
		}

		void RemoveShellmapUI()
		{
			rootMenu.Parent.RemoveChild(rootMenu);
		}

		void StartSkirmishGame()
		{
			SwitchMenu(MenuType.None);

			var map = modData.MapCache.ChooseInitialMap(modData.MapCache.PickLastModifiedMap(MapVisibility.Lobby) ?? Game.Settings.Server.Map, Game.CosmeticRandom);
			Game.Settings.Server.Map = map;
			Game.Settings.Save();

			ConnectionLogic.Connect(Game.CreateLocalServer(map, isSkirmish: true),
				"",
				OpenSkirmishLobbyPanel,
				() => { Game.CloseServer(); SwitchMenu(MenuType.Main); });
		}

		void OpenMissionBrowserPanel(string map)
		{
			SwitchMenu(MenuType.None);
			Game.OpenWindow("MISSIONBROWSER_PANEL", new WidgetArgs
			{
				{ "onExit", () => { Game.Disconnect(); SwitchMenu(MenuType.Singleplayer); } },
				{ "onStart", () => { RemoveShellmapUI(); lastGameState = MenuPanel.Missions; } },
				{ "initialMap", map }
			});
		}

		void OpenEncyclopediaPanel()
		{
			SwitchMenu(MenuType.None);
			Game.OpenWindow("ENCYCLOPEDIA_PANEL", new WidgetArgs
			{
				{ "onExit", () => SwitchMenu(MenuType.Singleplayer) }
			});
		}

		void OpenSkirmishLobbyPanel()
		{
			SwitchMenu(MenuType.None);
			Game.OpenWindow("SERVER_LOBBY", new WidgetArgs
			{
				{ "onExit", () => { Game.Disconnect(); SwitchMenu(MenuType.Singleplayer); } },
				{ "onStart", () => { RemoveShellmapUI(); lastGameState = MenuPanel.Skirmish; } },
				{ "skirmishMode", true }
			});
		}

		void OpenMultiplayerPanel()
		{
			SwitchMenu(MenuType.None);
			Ui.OpenWindow("MULTIPLAYER_PANEL", new WidgetArgs
			{
				{ "onStart", () => { RemoveShellmapUI(); lastGameState = MenuPanel.Multiplayer; } },
				{ "onExit", () => SwitchMenu(MenuType.Main) },
				{ "directConnectEndPoint", null },
			});
		}

		void OpenReplayBrowserPanel()
		{
			SwitchMenu(MenuType.None);
			Ui.OpenWindow("REPLAYBROWSER_PANEL", new WidgetArgs
			{
				{ "onExit", () => SwitchMenu(MenuType.Extras) },
				{ "onStart", () => { RemoveShellmapUI(); lastGameState = MenuPanel.Replays; } }
			});
		}

		void OpenGameSaveBrowserPanel()
		{
			SwitchMenu(MenuType.None);
			Ui.OpenWindow("LOAD_GAME_BROWSER_PANEL", new WidgetArgs
			{
				{ "onExit", () => SwitchMenu(MenuType.Singleplayer) },
				{ "onStart", () => { RemoveShellmapUI(); lastGameState = MenuPanel.GameSaves; } },
			});
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Game.OnRemoteDirectConnect -= OnRemoteDirectConnect;
				Game.BeforeGameStart -= RemoveShellmapUI;
			}

			Game.OnShellmapLoaded -= OpenMenuBasedOnLastGame;
			base.Dispose(disposing);
		}

		void OpenMenuBasedOnLastGame()
		{
			switch (lastGameState)
			{
				case MenuPanel.Missions:
					OpenMissionBrowserPanel(null);
					break;

				case MenuPanel.Replays:
					OpenReplayBrowserPanel();
					break;

				case MenuPanel.Skirmish:
					StartSkirmishGame();
					break;

				case MenuPanel.Multiplayer:
					OpenMultiplayerPanel();
					break;

				case MenuPanel.MapEditor:
					SwitchMenu(MenuType.MapEditor);
					break;

				case MenuPanel.GameSaves:
					SwitchMenu(MenuType.Singleplayer);
					break;
			}

			lastGameState = MenuPanel.None;
		}
	}
}
