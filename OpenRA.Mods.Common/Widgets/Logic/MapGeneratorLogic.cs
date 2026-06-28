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
using System.Text;
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

		[FluentReference]
		const string BlueprintCopy = "button-mapchooser-blueprint-copy";

		[FluentReference]
		const string BlueprintCopied = "button-mapchooser-blueprint-copied";

		[FluentReference]
		const string RenameTitle = "dialog-rename-map.title";

		[FluentReference]
		const string RenamePrompt = "dialog-rename-map.prompt";

		[FluentReference]
		const string RenameAccept = "dialog-rename-map.confirm";

		public static readonly IReadOnlyDictionary<string, int2> MapSizes = new Dictionary<string, int2>()
		{
			{ MapSizeSmall, new int2(48, 60) },
			{ MapSizeMedium, new int2(60, 90) },
			{ MapSizeLarge, new int2(90, 120) },
			{ MapSizeHuge, new int2(120, 160) },
		};

		readonly ModData modData;
		readonly IReadOnlyList<IEditorMapGeneratorInfo> generators;
		readonly List<ITerrainInfo> validTerrainInfos;
		IEditorMapGeneratorInfo generator;
		IMapGeneratorSettings settings;
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

		// The last successfully generated map, kept so the Rename button can re-title it in place.
		Map generatedMap;
		MapGenerationArgs generatedArgs;

		// User-chosen map name (via Rename). Null/empty falls back to the generator's default title.
		string customTitle;

		// On-disk path the generated map was last written to, so a rename can move the old file.
		string currentMapPath;

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

			generators = modData.DefaultRules.Actors[SystemActors.EditorWorld].TraitInfos<IEditorMapGeneratorInfo>().ToList();

			// Build the unified tileset list spanning every generator. Each tileset maps to the first
			// generator (in rules order) that supports it, so dedicated terrain generators (Classic/D2k)
			// own their tilesets and ClearMapGenerator - listed last - only owns tilesets nothing else covers.
			var seenTilesets = new HashSet<string>();
			validTerrainInfos = new List<ITerrainInfo>();
			foreach (var g in generators)
				foreach (var t in g.Tilesets)
					if (seenTilesets.Add(t))
						validTerrainInfos.Add(modData.DefaultTerrainInfo[t]);

			// Start on the first generator; SelectTerrain swaps to the matching one as the tileset changes.
			generator = generators[0];
			settings = generator.GetSettings();
			preview = widget.Get<GeneratedMapPreviewWidget>("PREVIEW");

			widget.Get("ERROR").IsVisible = () => failed;

			var title = new CachedTransform<string, string>(id => FluentProvider.GetMessage(id));
			var previewTitleLabel = widget.Get<LabelWidget>("TITLE");
			previewTitleLabel.GetText = () =>
			{
				// Once a map has settled, show the user-chosen name (if any); otherwise fall back to
				// the live status text (Generating.../error) or the default random-map title.
				if (!IsGenerating && !failed && !string.IsNullOrWhiteSpace(customTitle))
					return customTitle;

				return title.Update(IsGenerating ? Generating : failed ? GenerationFailed : RandomMap);
			};

			var previewDetailsLabel = widget.GetOrNull<LabelWidget>("DETAILS");
			if (previewDetailsLabel != null)
			{
				// The default "Conquest" label is hardcoded in Map.cs
				var desc = new CachedTransform<int, string>(p => "Conquest " + FluentProvider.GetMessage(Players, "players", p));
				previewDetailsLabel.GetText = () => desc.Update(settings.PlayerCount);
				previewDetailsLabel.IsVisible = () => !failed;
			}

			var previewAuthorLabel = widget.GetOrNull<LabelWithTooltipWidget>("AUTHOR");
			if (previewAuthorLabel != null)
			{
				previewAuthorLabel.GetText = () => FluentProvider.GetMessage(CreatedBy, "author", FluentProvider.GetMessage(generator.Name));
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

			// As DROPDOWN_TEMPLATE, but its button carries a tooltip - used for the tileset, whose
			// descriptive names can overflow the dropdown and need the full text available on hover.
			var dropdownTooltipTemplate = settingsPanel.Get<Widget>("DROPDOWN_TOOLTIP_TEMPLATE");
			settingsPanel.Layout = new GridLayout(settingsPanel);

			// Tileset and map size are handled outside the generator logic so must be created manually
			var tilesetLabel = FluentProvider.GetMessage(Tileset);
			tilesetSetting = dropdownTooltipTemplate.Clone();
			tilesetSetting.Get<LabelWidget>("LABEL").GetText = () => tilesetLabel;

			var label = new CachedTransform<ITerrainInfo, string>(ti => FluentProvider.GetMessage(ti.Name));
			var tilesetDropdown = tilesetSetting.Get<DropDownButtonWidget>("DROPDOWN");
			var tilesetFont = Game.Renderer.Fonts[tilesetDropdown.Font];

			// The descriptive environment names can be wider than the dropdown button, so fit them
			// with an ellipsis and surface the full name as a tooltip when it doesn't fit.
			string FitTilesetName() => WidgetUtils.TruncateText(
				label.Update(selectedTerrain),
				tilesetDropdown.UsableWidth - tilesetDropdown.LeftMargin - tilesetDropdown.RightMargin,
				tilesetFont);
			tilesetDropdown.GetText = FitTilesetName;

			// Always expose the full name on hover (must be non-null - the tooltip logic splits it).
			tilesetDropdown.GetTooltipText = () => label.Update(selectedTerrain);
			tilesetDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(ITerrainInfo terrainInfo, ScrollItemWidget template)
				{
					bool IsSelected() => terrainInfo == selectedTerrain;
					void OnClick()
					{
						// Picking a tileset owned by a different generator swaps to it and gives the
						// freshly-shown options a random seed; same-generator switches keep settings.
						if (SelectTerrain(terrainInfo))
							settings.Randomize(Game.CosmeticRandom);
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

			// Generate with the current settings and seed (no implicit randomization), so a
			// seed that was typed in or randomized by the user is honoured - the same seed and
			// settings always produce the same map.
			generateButton.OnClick = GenerateMap;

			// Randomizes only the seed (Randomize leaves the user's other settings untouched).
			// Does not generate - the user clicks Generate themselves.
			var randomizeSeedButton = widget.GetOrNull<ButtonWidget>("BUTTON_RANDOMIZE_SEED");
			if (randomizeSeedButton != null)
			{
				randomizeSeedButton.OnClick = () =>
				{
					settings.Randomize(Game.CosmeticRandom);
					RefreshSettings();
				};
			}

			// Blueprint panel visibility is shared across IsDisabled lambdas below, so declare here.
			var blueprintPanelVisible = false;
			generateButton.IsDisabled = () => IsGenerating || blueprintPanelVisible;
			if (randomizeSeedButton != null)
				randomizeSeedButton.IsDisabled = () => IsGenerating || blueprintPanelVisible;

			// Lets the user give the generated map a custom name (its Title metadata).
			var renameButton = widget.GetOrNull<ButtonWidget>("BUTTON_RENAME");
			if (renameButton != null)
			{
				renameButton.OnClick = RenameMap;
				renameButton.IsDisabled = () => IsGenerating || blueprintPanelVisible;
			}

			var blueprintPanel = widget.GetOrNull<ContainerWidget>("BLUEPRINT_PANEL");
			var exportField = widget.GetOrNull<TextFieldWidget>("BLUEPRINT_EXPORT");
			var importField = widget.GetOrNull<TextFieldWidget>("BLUEPRINT_IMPORT");
			var copyButton = widget.GetOrNull<ButtonWidget>("BLUEPRINT_COPY");
			var loadButton = widget.GetOrNull<ButtonWidget>("BLUEPRINT_LOAD");
			var closeButton = widget.GetOrNull<ButtonWidget>("BLUEPRINT_CLOSE");
			var shareButton = widget.GetOrNull<ButtonWidget>("BUTTON_BLUEPRINT");

			if (blueprintPanel != null)
				blueprintPanel.IsVisible = () => blueprintPanelVisible;

			void OpenBlueprint()
			{
				if (exportField != null)
					exportField.Text = BuildBlueprintCode();
				if (importField != null)
					importField.Text = string.Empty;
				blueprintPanelVisible = true;
			}

			void CloseBlueprint() => blueprintPanelVisible = false;

			if (shareButton != null)
			{
				shareButton.IsDisabled = () => IsGenerating;
				shareButton.OnClick = OpenBlueprint;
			}

			DateTime? copiedAt = null;
			if (copyButton != null && exportField != null)
			{
				copyButton.GetText = () =>
					copiedAt.HasValue && (DateTime.UtcNow - copiedAt.Value).TotalSeconds < 2
						? FluentProvider.GetMessage(BlueprintCopied)
						: FluentProvider.GetMessage(BlueprintCopy);
				copyButton.OnClick = () =>
				{
					var code = BuildBlueprintCode();
					exportField.Text = code;
					Game.SetClipboardText(code);
					copiedAt = DateTime.UtcNow;
				};
			}

			if (loadButton != null && importField != null)
			{
				loadButton.IsDisabled = () => string.IsNullOrWhiteSpace(importField.Text);
				loadButton.OnClick = () =>
				{
					if (TryApplyBlueprintCode(importField.Text))
					{
						RefreshSettings();
						CloseBlueprint();
						GenerateMap();
					}
				};
			}

			if (closeButton != null)
				closeButton.OnClick = CloseBlueprint;

			selectedSize = MapSizes.Keys.Skip(1).First();

			if (initialSettings != null && modData.DefaultTerrainInfo.ContainsKey(initialSettings.Tileset))
			{
				// The lobby preselected a generated map - restore it and show its cached preview.
				// Honour the map's own generator (by Type) so its options restore into matching settings.
				SelectTerrain(modData.DefaultTerrainInfo[initialSettings.Tileset]);
				var sourceGenerator = generators.FirstOrDefault(g => g.Type == initialSettings.Generator);
				if (sourceGenerator != null && sourceGenerator != generator)
				{
					generator = sourceGenerator;
					settings = generator.GetSettings();
				}

				size = initialSettings.Size;
				foreach (var kv in MapSizes)
					if (size.Width >= kv.Value.X && size.Width <= kv.Value.Y)
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
				SelectTerrain(validTerrainInfos[0]);
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

		// Selects a tileset and switches the active generator to whichever one owns it (first match
		// in rules order). Returns true if the generator actually changed, so the caller can decide
		// whether to reset option values (e.g. randomize a fresh seed) or preserve them (on restore).
		bool SelectTerrain(ITerrainInfo terrainInfo)
		{
			selectedTerrain = terrainInfo;
			var match = generators.FirstOrDefault(g => g.Tilesets.Contains(terrainInfo.Id)) ?? generators[0];
			if (match == generator)
				return false;

			generator = match;
			settings = generator.GetSettings();
			return true;
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

				// Switch to the generator owning this tileset before loading its option values.
				SelectTerrain(modData.DefaultTerrainInfo[tileset]);
				size = FieldLoader.GetValue<Size>("Size", sizeNode.Value.Value);
				foreach (var kv in MapSizes)
					if (size.Width >= kv.Value.X && size.Width <= kv.Value.Y)
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

		string BuildBlueprintCode()
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

			var nodes = new List<MiniYamlNode>
			{
				new("Tileset", selectedTerrain.Id),
				new("Size", FieldSaver.FormatValue(size)),
				new("Options", new MiniYaml(null, optionNodes)),
			};

			return Convert.ToBase64String(Encoding.UTF8.GetBytes(nodes.WriteToString()));
		}

		bool TryApplyBlueprintCode(string code)
		{
			try
			{
				var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(code.Trim()));
				var root = new MiniYaml(null, MiniYaml.FromString(yaml, "blueprint"));
				var tilesetNode = root.NodeWithKeyOrDefault("Tileset");
				var sizeNode = root.NodeWithKeyOrDefault("Size");
				var optionsNode = root.NodeWithKeyOrDefault("Options");
				if (tilesetNode == null || sizeNode == null || optionsNode == null)
					return false;

				var tileset = tilesetNode.Value.Value;
				if (!modData.DefaultTerrainInfo.ContainsKey(tileset))
					return false;

				// Switch to the generator owning this tileset before loading its option values.
				SelectTerrain(modData.DefaultTerrainInfo[tileset]);
				size = FieldLoader.GetValue<Size>("Size", sizeNode.Value.Value);
				foreach (var kv in MapSizes)
					if (size.Width >= kv.Value.X && size.Width <= kv.Value.Y)
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
				Log.Write("debug", $"Failed to apply blueprint code: {e}");
				return false;
			}
		}

		void RandomizeSize()
		{
			var mapGrid = modData.GetOrCreate<MapGrid>();
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
			// A fresh generation is a new, separate map: drop any custom name from a previous Rename so it
			// gets the default title and filename, and stop tracking the old file so a renamed-and-kept map
			// is not deleted by this generation's save.
			customTitle = null;
			currentMapPath = null;

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

					// Honour a user-chosen name (set via Rename) so it carries through to the
					// generated map's Title metadata, the lobby and the saved file.
					if (!string.IsNullOrWhiteSpace(customTitle))
						args.Title = customTitle;

					map = generator.Generate(modData, args);
				}
				catch (Exception e)
				{
					// Catch ALL exceptions, not just MapGenerationException: a generator that throws
					// anything else (e.g. a name mismatch surfacing as YamlException/KeyNotFound) would
					// otherwise fault the task silently and leave the UI stuck on "Generating..." forever.
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
						// Keep a reference so the Rename button can re-title this map without regenerating.
						generatedMap = map;
						generatedArgs = args;
						SaveAndPublish(map, args);
						lastGeneration = currentGeneration;
					}
				});
			});
		}

		// Turns a map name into a safe .oramap base filename (whitespace and invalid characters
		// become underscores), defaulting to "generated" for an empty or fully-stripped name.
		static string MakeMapFileName(string title)
		{
			if (string.IsNullOrWhiteSpace(title))
				return "generated";

			var invalid = Path.GetInvalidFileNameChars();
			var cleaned = new string(title.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray()).Trim('.');
			return string.IsNullOrWhiteSpace(cleaned) ? "generated" : cleaned;
		}

		// Saves the map into a fresh package, writes it to the User map folder and hands it to the lobby.
		// Shared by the initial generation and the Rename button (which also renames the file on disk).
		void SaveAndPublish(Map map, MapGenerationArgs args)
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
				// Name the file after the chosen map name so Rename renames the file on disk;
				// fall back to a fixed scratch name when the map is unnamed.
				var newPath = Path.Combine(mapsDir, MakeMapFileName(customTitle) + ".oramap");
				File.WriteAllBytes(newPath, package.GetBytes());

				// Move rather than copy: remove the previously written file when the name changed.
				if (currentMapPath != null && currentMapPath != newPath && File.Exists(currentMapPath))
					File.Delete(currentMapPath);

				currentMapPath = newPath;
			}

			preview.Update(map);

			// Remember these settings (incl. the seed) so reopening the generator - even
			// after restarting the game - restores them.
			PersistSettings();

			// `onGenerate` assumed to take ownership of package here.
			onGenerate(args, package);
		}

		// Prompts for a new map name and applies it as the map's Title. An empty name clears the
		// override, restoring the generator's default title.
		void RenameMap()
		{
			var current = !string.IsNullOrWhiteSpace(customTitle)
				? customTitle
				: FluentProvider.GetMessage(RandomMap);

			ConfirmationDialogs.TextInputPrompt(modData,
				RenameTitle, RenamePrompt, current,
				onAccept: name =>
				{
					name = name?.Trim();
					customTitle = string.IsNullOrEmpty(name) ? null : name;

					// Re-title the already-generated map in place (no regeneration) and republish it
					// so the lobby and the saved file pick up the new name immediately.
					if (generatedMap != null && generatedArgs != null)
					{
						var newTitle = customTitle ?? FluentProvider.GetMessage(RandomMap);
						generatedMap.Title = newTitle;
						generatedArgs.Title = newTitle;
						SaveAndPublish(generatedMap, generatedArgs);
					}
				},
				acceptText: RenameAccept);
		}
	}
}
