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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.FileSystem;
using OpenRA.Mods.Common.MapGenerator;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class MapGeneratorLogic : ChromeLogic
	{
		[FluentReference]
		const string Tileset = "label-mapchooser-random-map-tileset";

		[FluentReference]
		const string MapSize = "label-mapchooser-random-map-size";

		[FluentReference]
		const string RandomMap = "label-mapchooser-random-map-title";

		[FluentReference]
		const string Generating = "label-mapchooser-random-map-generating";

		[FluentReference]
		const string GenerationFailed = "label-mapchooser-random-map-error";

		[FluentReference("players")]
		const string Players = "label-player-count";

		[FluentReference("author")]
		const string CreatedBy = "label-created-by";

		[FluentReference]
		const string MapSizeSmall = "label-map-size-small";

		[FluentReference]
		const string MapSizeMedium = "label-map-size-medium";

		[FluentReference]
		const string MapSizeLarge = "label-map-size-large";

		[FluentReference]
		const string MapSizeHuge = "label-map-size-huge";

		public static readonly IReadOnlyDictionary<string, int2> MapSizes = new Dictionary<string, int2>()
		{
			{ MapSizeSmall, new int2(48, 60) },
			{ MapSizeMedium, new int2(60, 90) },
			{ MapSizeLarge, new int2(90, 120) },
			{ MapSizeHuge, new int2(120, 160) },
		};

		readonly ModData modData;
		readonly IEditorMapGeneratorInfo generator;
		readonly IMapGeneratorSettings settings;
		readonly Action<MapGenerationArgs, IReadWritePackage> onGenerate;

		readonly GeneratedMapPreviewWidget preview;
		readonly ScrollPanelWidget settingsPanel;
		readonly Widget checkboxSettingTemplate;
		readonly Widget textSettingTemplate;
		readonly Widget dropdownSettingTemplate;
		readonly Widget tilesetSetting;
		readonly Widget sizeSetting;
		readonly Widget parentWidget;

		ITerrainInfo selectedTerrain;
		string selectedSize;
		Size size;
		bool initialGenerationDone;

		volatile bool failed;
		volatile uint generationCounter = 0;
		volatile uint lastGeneration = 0;

		bool IsGenerating => lastGeneration != generationCounter;

		[ObjectCreator.UseCtor]
		internal MapGeneratorLogic(Widget widget, ModData modData, MapGenerationArgs initialSettings, Action<MapGenerationArgs, IReadWritePackage> onGenerate)
		{
			this.modData = modData;
			this.onGenerate = onGenerate;
			parentWidget = widget.Parent;

			generator = modData.DefaultRules.Actors[SystemActors.EditorWorld].TraitInfos<IEditorMapGeneratorInfo>().First();
			settings = generator.GetSettings();
			preview = widget.Get<GeneratedMapPreviewWidget>("PREVIEW");

			widget.Get("ERROR").IsVisible = () => failed;

			var title = new CachedTransform<string, string>(id => FluentProvider.GetMessage(id));
			var previewTitleLabel = widget.Get<LabelWidget>("TITLE");
			previewTitleLabel.GetText = () => title.Update(IsGenerating ? Generating : failed ? GenerationFailed : RandomMap);

			var previewDetailsLabel = widget.GetOrNull<LabelWidget>("DETAILS");
			if (previewDetailsLabel != null)
			{
				// The default "Conquest" label is hardcoded in Map.cs
				var desc = new CachedTransform<int, string>(p => "Conquest " + FluentProvider.GetMessage(Players, "players", p));
				var playersOption = settings.Options.FirstOrDefault(o => o.Id == "Players") as MapGeneratorMultiIntegerChoiceOption;
				previewDetailsLabel.GetText = () => desc.Update(playersOption?.Value ?? 0);
				previewDetailsLabel.IsVisible = () => !failed;
			}

			var previewAuthorLabel = widget.GetOrNull<LabelWithTooltipWidget>("AUTHOR");
			if (previewAuthorLabel != null)
			{
				var desc = FluentProvider.GetMessage(CreatedBy, "author", FluentProvider.GetMessage(generator.Name));
				previewAuthorLabel.GetText = () => desc;
				previewAuthorLabel.IsVisible = () => !failed;
			}

			var previewSizeLabel = widget.GetOrNull<LabelWidget>("SIZE");
			if (previewSizeLabel != null)
			{
				var desc = new CachedTransform<Size, string>(MapChooserLogic.MapSizeLabel);
				previewSizeLabel.IsVisible = () => !failed;
				previewSizeLabel.GetText = () => desc.Update(size);
			}

			settingsPanel = widget.Get<ScrollPanelWidget>("SETTINGS_PANEL");
			checkboxSettingTemplate = settingsPanel.Get<Widget>("CHECKBOX_TEMPLATE");
			textSettingTemplate = settingsPanel.Get<Widget>("TEXT_TEMPLATE");
			dropdownSettingTemplate = settingsPanel.Get<Widget>("DROPDOWN_TEMPLATE");
			settingsPanel.Layout = new GridLayout(settingsPanel);

			// Tileset and map size are handled outside the generator logic so must be created manually
			var validTerrainInfos = generator.Tilesets.Select(t => modData.DefaultTerrainInfo[t]).ToList();
			var tilesetLabel = FluentProvider.GetMessage(Tileset);
			tilesetSetting = dropdownSettingTemplate.Clone();
			tilesetSetting.Get<LabelWidget>("LABEL").GetText = () => tilesetLabel;

			var label = new CachedTransform<ITerrainInfo, string>(ti => FluentProvider.GetMessage(ti.Name));
			var tilesetDropdown = tilesetSetting.Get<DropDownButtonWidget>("DROPDOWN");
			tilesetDropdown.GetText = () => label.Update(selectedTerrain);
			tilesetDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(ITerrainInfo terrainInfo, ScrollItemWidget template)
				{
					bool IsSelected() => terrainInfo == selectedTerrain;
					void OnClick()
					{
						selectedTerrain = terrainInfo;
						RefreshSettings();
					}

					var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
					var itemLabel = FluentProvider.GetMessage(terrainInfo.Name);
					item.Get<LabelWidget>("LABEL").GetText = () => itemLabel;
					return item;
				}

				tilesetDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", validTerrainInfos.Count * 30, validTerrainInfos, SetupItem);
			};

			var sizeLabel = FluentProvider.GetMessage(MapSize);
			sizeSetting = dropdownSettingTemplate.Clone();
			sizeSetting.Get<LabelWidget>("LABEL").GetText = () => sizeLabel;

			var sizeDropdown = sizeSetting.Get<DropDownButtonWidget>("DROPDOWN");
			var sizeDropdownLabel = new CachedTransform<string, string>(s => FluentProvider.GetMessage(s));
			sizeDropdown.GetText = () => sizeDropdownLabel.Update(selectedSize);
			sizeDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(string size, ScrollItemWidget template)
				{
					bool IsSelected() => size == selectedSize;
					void OnClick()
					{
						selectedSize = size;
						RandomizeSize();
					}

					var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
					var label = FluentProvider.GetMessage(size);
					item.Get<LabelWidget>("LABEL").GetText = () => label;
					return item;
				}

				sizeDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", MapSizes.Count * 30, MapSizes.Keys, SetupItem);
			};

			var generateButton = widget.Get<ButtonWidget>("BUTTON_GENERATE");
			generateButton.IsDisabled = () => IsGenerating;

			// Generate with the current settings and seed (no implicit randomization), so a
			// seed that was typed in or randomized by the user is honoured - the same seed and
			// settings always produce the same map.
			generateButton.OnClick = GenerateMap;

			// Randomizes only the seed (Randomize leaves the user's other settings untouched).
			// Does not generate - the user clicks Generate themselves.
			var randomizeSeedButton = widget.GetOrNull<ButtonWidget>("BUTTON_RANDOMIZE_SEED");
			if (randomizeSeedButton != null)
			{
				randomizeSeedButton.IsDisabled = () => IsGenerating;
				randomizeSeedButton.OnClick = () =>
				{
					settings.Randomize(Game.CosmeticRandom);
					RefreshSettings();
				};
			}

			selectedSize = MapSizes.Keys.Skip(1).First();

			if (initialSettings != null && modData.DefaultTerrainInfo.ContainsKey(initialSettings.Tileset))
			{
				// The lobby preselected a generated map - restore it and show its cached preview.
				selectedTerrain = modData.DefaultTerrainInfo[initialSettings.Tileset];
				size = initialSettings.Size;
				foreach (var kv in MapSizes)
					if (kv.Value.X > size.Width && kv.Value.Y <= size.Width)
						selectedSize = kv.Key;

				settings.Initialize(initialSettings);
				RefreshSettings();

				var map = modData.MapCache[initialSettings.Uid];
				if (map.Status == MapStatus.Available)
				{
					preview.Update(map);
					onGenerate(initialSettings, null);
				}
			}
			else if (TryLoadPersistedSettings())
			{
				// Restored the last settings the user generated with (persisted to disk).
				// Leave the preview empty until the user clicks Generate (no auto-generation).
				RefreshSettings();
			}
			else
			{
				selectedTerrain = validTerrainInfos[0];
				settings.Randomize(Game.CosmeticRandom);
				RandomizeSize();
				RefreshSettings();
			}

			initialGenerationDone = true;
		}

		public override void Tick()
		{
			if (!initialGenerationDone && !IsGenerating && parentWidget.IsVisible())
			{
				initialGenerationDone = true;
				GenerateMap();
			}
		}

		string PersistedSettingsPath => Path.Combine(Platform.SupportDir, "map-generator-" + modData.Manifest.Id + ".yaml");

		// Writes the current tileset, size and option values (incl. the seed) to disk so the
		// generator can restore them next time it is opened, surviving game restarts.
		void PersistSettings()
		{
			try
			{
				var optionNodes = new List<MiniYamlNode>();
				foreach (var o in settings.Options)
				{
					switch (o)
					{
						case MapGeneratorBooleanOption bo: optionNodes.Add(new MiniYamlNode(o.Id, FieldSaver.FormatValue(bo.Value))); break;
						case MapGeneratorIntegerOption io: optionNodes.Add(new MiniYamlNode(o.Id, FieldSaver.FormatValue(io.Value))); break;
						case MapGeneratorMultiIntegerChoiceOption mio: optionNodes.Add(new MiniYamlNode(o.Id, FieldSaver.FormatValue(mio.Value))); break;
						case MapGeneratorMultiChoiceOption mo: optionNodes.Add(new MiniYamlNode(o.Id, mo.Value ?? "")); break;
					}
				}

				var nodes = new List<MiniYamlNode>()
				{
					new("Tileset", selectedTerrain.Id),
					new("Size", FieldSaver.FormatValue(size)),
					new("Options", new MiniYaml(null, optionNodes)),
				};

				File.WriteAllText(PersistedSettingsPath, nodes.WriteToString());
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to save map generator settings: {e}");
			}
		}

		// Restores tileset, size and option values previously written by PersistSettings.
		// Returns false (leaving state untouched) if there is nothing valid to restore.
		bool TryLoadPersistedSettings()
		{
			try
			{
				var path = PersistedSettingsPath;
				if (!File.Exists(path))
					return false;

				var root = new MiniYaml(null, MiniYaml.FromString(File.ReadAllText(path), path));
				var tilesetNode = root.NodeWithKeyOrDefault("Tileset");
				var sizeNode = root.NodeWithKeyOrDefault("Size");
				var optionsNode = root.NodeWithKeyOrDefault("Options");
				if (tilesetNode == null || sizeNode == null || optionsNode == null)
					return false;

				var tileset = tilesetNode.Value.Value;
				if (!modData.DefaultTerrainInfo.ContainsKey(tileset))
					return false;

				selectedTerrain = modData.DefaultTerrainInfo[tileset];
				size = FieldLoader.GetValue<Size>("Size", sizeNode.Value.Value);
				foreach (var kv in MapSizes)
					if (kv.Value.X > size.Width && kv.Value.Y <= size.Width)
						selectedSize = kv.Key;

				foreach (var o in settings.Options)
				{
					var node = optionsNode.Value.NodeWithKeyOrDefault(o.Id);
					var value = node?.Value.Value;
					if (string.IsNullOrEmpty(value))
						continue;

					switch (o)
					{
						case MapGeneratorBooleanOption bo: bo.Value = FieldLoader.GetValue<bool>(o.Id, value); break;
						case MapGeneratorIntegerOption io: io.Value = FieldLoader.GetValue<int>(o.Id, value); break;
						case MapGeneratorMultiIntegerChoiceOption mio: mio.Value = FieldLoader.GetValue<int>(o.Id, value); break;
						case MapGeneratorMultiChoiceOption mo: mo.Value = value; break;
					}
				}

				return true;
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to load map generator settings: {e}");
				return false;
			}
		}

		void RandomizeSize()
		{
			var mapGrid = modData.Manifest.Get<MapGrid>();
			var sizeRange = MapSizes[selectedSize];
			var width = Game.CosmeticRandom.Next(sizeRange.X, sizeRange.Y);
			var height =
				mapGrid.Type == MapGridType.RectangularIsometric
					? width * 2
					: width;

			size = new Size(width + 2, height + mapGrid.MaximumTerrainHeight * 2 + 2);
		}

		void RefreshSettings()
		{
			settingsPanel.RemoveChildren();
			tilesetSetting.Bounds = sizeSetting.Bounds = dropdownSettingTemplate.Bounds;
			settingsPanel.AddChild(tilesetSetting);
			settingsPanel.AddChild(sizeSetting);

			var playerCount = settings.PlayerCount;
			foreach (var o in settings.Options)
			{
				Widget settingWidget = null;
				switch (o)
				{
					case MapGeneratorBooleanOption bo:
					{
						settingWidget = checkboxSettingTemplate.Clone();
						var checkboxWidget = settingWidget.Get<CheckboxWidget>("CHECKBOX");
						var label = FluentProvider.GetMessage(bo.Label);
						checkboxWidget.GetText = () => label;
						checkboxWidget.IsChecked = () => bo.Value;
						checkboxWidget.OnClick = () =>
						{
							bo.Value ^= true;
						};
						break;
					}

					case MapGeneratorIntegerOption io:
					{
						settingWidget = textSettingTemplate.Clone();
						var labelWidget = settingWidget.Get<LabelWidget>("LABEL");
						var label = FluentProvider.GetMessage(io.Label);
						labelWidget.GetText = () => label;
						var textFieldWidget = settingWidget.Get<TextFieldWidget>("INPUT");
						textFieldWidget.Type = TextFieldType.Integer;
						textFieldWidget.Text = FieldSaver.FormatValue(io.Value);
						textFieldWidget.OnTextEdited = () =>
						{
							var valid = int.TryParse(textFieldWidget.Text, out io.Value);
							textFieldWidget.IsValid = () => valid;
						};

						textFieldWidget.OnEscKey = _ => { textFieldWidget.YieldKeyboardFocus(); return true; };
						textFieldWidget.OnEnterKey = _ => { textFieldWidget.YieldKeyboardFocus(); return true; };
						break;
					}

					case MapGeneratorMultiIntegerChoiceOption mio:
					{
						settingWidget = dropdownSettingTemplate.Clone();
						var labelWidget = settingWidget.Get<LabelWidget>("LABEL");
						var label = FluentProvider.GetMessage(mio.Label);
						labelWidget.GetText = () => label;

						var labelCache = new CachedTransform<int, string>(v => FieldSaver.FormatValue(v));
						var dropDownWidget = settingWidget.Get<DropDownButtonWidget>("DROPDOWN");
						dropDownWidget.GetText = () => labelCache.Update(mio.Value);
						dropDownWidget.OnMouseDown = _ =>
						{
							ScrollItemWidget SetupItem(int choice, ScrollItemWidget template)
							{
								bool IsSelected() => choice == mio.Value;
								void OnClick()
								{
									mio.Value = choice;
									if (o.Id == "Players")
										RefreshSettings();
								}

								var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
								var itemLabel = FieldSaver.FormatValue(choice);
								item.Get<LabelWidget>("LABEL").GetText = () => itemLabel;
								item.GetTooltipText = null;
								return item;
							}

							dropDownWidget.ShowDropDown("LABEL_DROPDOWN_WITH_TOOLTIP_TEMPLATE", 250, mio.Choices, SetupItem);
						};
						break;
					}

					case MapGeneratorMultiChoiceOption mo:
					{
						var validChoices = mo.ValidChoices(selectedTerrain, playerCount);
						if (!validChoices.Contains(mo.Value))
						{
							if (mo.Default != null)
								mo.Value = mo.Default.FirstOrDefault(validChoices.Contains);
							mo.Value ??= validChoices.FirstOrDefault();
						}

						if (mo.Label != null && validChoices.Count > 0)
						{
							settingWidget = dropdownSettingTemplate.Clone();
							var labelWidget = settingWidget.Get<LabelWidget>("LABEL");
							var label = FluentProvider.GetMessage(mo.Label);
							labelWidget.GetText = () => label;

							var labelCache = new CachedTransform<string, string>(v => FluentProvider.GetMessage(mo.Choices[v].Label + ".label"));
							var dropDownWidget = settingWidget.Get<DropDownButtonWidget>("DROPDOWN");
							dropDownWidget.GetText = () => labelCache.Update(mo.Value);
							dropDownWidget.OnMouseDown = _ =>
							{
								ScrollItemWidget SetupItem(string choice, ScrollItemWidget template)
								{
									bool IsSelected() => choice == mo.Value;
									void OnClick()
									{
										mo.Value = choice;
									}

									var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);

									var itemLabel = FluentProvider.GetMessage(mo.Choices[choice].Label + ".label");
									item.Get<LabelWidget>("LABEL").GetText = () => itemLabel;
									if (FluentProvider.TryGetMessage(mo.Choices[choice].Label + ".description", out var desc))
										item.GetTooltipText = () => desc;
									else
										item.GetTooltipText = null;

									return item;
								}

								dropDownWidget.ShowDropDown("LABEL_DROPDOWN_WITH_TOOLTIP_TEMPLATE", 250, validChoices, SetupItem);
							};
						}

						break;
					}

					default:
						throw new NotImplementedException($"Unhandled MapGeneratorOption type {o.GetType().Name}");
				}

				if (settingWidget == null)
					continue;

				settingWidget.IsVisible = () => true;
				settingsPanel.AddChild(settingWidget);
			}
		}

		void GenerateMap()
		{
			var currentGeneration = Interlocked.Increment(ref generationCounter);

			failed = false;
			onGenerate(null, null);
			preview.Clear();

			Task.Run(() =>
			{
				// Tasks don't run in parallel, so we may be able to cancel some outdated requests here.
				if (currentGeneration != generationCounter)
					return;

				MapGenerationArgs args;
				Map map;
				try
				{
					args = settings.Compile(selectedTerrain, size);
					map = generator.Generate(modData, args);
				}
				catch (MapGenerationException e)
				{
					Log.Write("debug", $"Map generation failed: {e}");
					// We are the lastest generation request, mark as failed.
					if (currentGeneration == generationCounter)
					{
						lastGeneration = currentGeneration;
						failed = true;
					}

					return;
				}

				// Need to invoke widgets from the main thread.
				Game.RunAfterTick(() =>
				{
					// A newer generation will be set after us, discard.
					if (currentGeneration == generationCounter)
					{
						var package = new ZipFileLoader.ReadWriteZipFile();
						map.Save(package);

						args.Uid = map.Uid;

						// Save into the actual User map folder the MapCache scans (version-specific
						// subdir), not the parent maps/<mod> directory which is never enumerated.
						var userMapFolder = modData.Manifest.MapFolders.FirstOrDefault(kv => kv.Value == "User").Key;
						if (userMapFolder != null)
						{
							var folderName = userMapFolder.StartsWith('~') ? userMapFolder[1..] : userMapFolder;
							var mapsDir = Platform.ResolvePath(folderName);
							Directory.CreateDirectory(mapsDir);
							File.WriteAllBytes(Path.Combine(mapsDir, "generated.oramap"), package.GetBytes());
						}

						preview.Update(map);
						lastGeneration = currentGeneration;

						// Remember these settings (incl. seed) so reopening the generator - even
						// after restarting the game - restores them.
						PersistSettings();

						// `onGenerate` assumed to take ownership of package here.
						onGenerate(args, package);
					}
				});
			});
		}
	}
}
