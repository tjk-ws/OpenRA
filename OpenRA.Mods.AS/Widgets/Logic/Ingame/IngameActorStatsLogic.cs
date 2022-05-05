#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
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
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.AS.Widgets;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class IngameActorStatsLogic : ChromeLogic
	{
		[ObjectCreator.UseCtor]
		public IngameActorStatsLogic(Widget widget, World world, Dictionary<string, MiniYaml> logicArgs)
		{
			var selection = world.WorldActor.Trait<ISelection>();

			List<ActorIconWidget> LargeIcons = new List<ActorIconWidget>();
			LargeIcons.Add(widget.Get<ActorIconWidget>("STAT_ICON"));
			List<HealthBarWidget> LargeHealthBars = new List<HealthBarWidget>();
			LargeHealthBars.Add(widget.Get<HealthBarWidget>("STAT_HEALTH_BAR"));
			var largeIconCount = 1;
			var largeIconSpacing = new int2 (2, 2);
			if (logicArgs.ContainsKey("LargeIconCount"))
				largeIconCount = FieldLoader.GetValue<int>("LargeIconCount", logicArgs["LargeIconCount"].Value);
			if (logicArgs.ContainsKey("LargeIconSpacing"))
				largeIconSpacing = FieldLoader.GetValue<int2>("LargeIconSpacing", logicArgs["LargeIconSpacing"].Value);
			if (largeIconCount > 1)
			{
				for (int i = 1; i < largeIconCount; i++)
				{
					var iconClone = LargeIcons[0].Clone() as ActorIconWidget;
					iconClone.Bounds.X += (iconClone.IconSize.X + largeIconSpacing.X) * i;

					widget.AddChild(iconClone);
					LargeIcons.Add(iconClone);

					var healthBarClone = LargeHealthBars[0].Clone() as HealthBarWidget;
					healthBarClone.Bounds.X += (healthBarClone.Bounds.Width + largeIconSpacing.X) * i;

					widget.AddChild(healthBarClone);
					LargeHealthBars.Add(healthBarClone);
				}
			}
			List<ActorIconWidget> SmallIcons = new List<ActorIconWidget>();
			List<HealthBarWidget> SmallHealthBars = new List<HealthBarWidget>();
			var smallIconCount = 0;
			var smallIconSpacing = new int2 (0, 5);
			var smallIconRows = 6;
			if (logicArgs.ContainsKey("SmallIconCount"))
				smallIconCount = FieldLoader.GetValue<int>("SmallIconCount", logicArgs["SmallIconCount"].Value);
			if (logicArgs.ContainsKey("SmallIconSpacing"))
				smallIconSpacing = FieldLoader.GetValue<int2>("SmallIconSpacing", logicArgs["SmallIconSpacing"].Value);
			if (logicArgs.ContainsKey("SmallIconRows"))
				smallIconRows = FieldLoader.GetValue<int>("SmallIconRows", logicArgs["SmallIconRows"].Value);
			if (smallIconCount > 0)
			{
				SmallIcons.Add(widget.Get<ActorIconWidget>("STAT_ICON_SMALL"));
				SmallHealthBars.Add(widget.Get<HealthBarWidget>("STAT_HEALTH_BAR_SMALL"));
				for (int i = 1; i < largeIconCount + smallIconCount; i++)
				{
					var iconClone = SmallIcons[0].Clone() as ActorIconWidget;
					iconClone.Bounds.X += (iconClone.IconSize.X + smallIconSpacing.X) * (i % smallIconRows);
					iconClone.Bounds.Y += (iconClone.IconSize.Y + smallIconSpacing.Y) * (i / smallIconRows);

					widget.AddChild(iconClone);
					SmallIcons.Add(iconClone);

					var healthBarClone = SmallHealthBars[0].Clone() as HealthBarWidget;
					healthBarClone.Bounds.X += (iconClone.IconSize.X + smallIconSpacing.X) * (i % smallIconRows);
					healthBarClone.Bounds.Y += (iconClone.IconSize.Y + smallIconSpacing.Y) * (i / smallIconRows);

					widget.AddChild(healthBarClone);
					SmallHealthBars.Add(healthBarClone);
				}
			}

			var name = widget.Get<LabelWidget>("STAT_NAME");
			var more = widget.GetOrNull<LabelWidget>("STAT_MORE");

			List<LabelWidget> ExtraStatLabels = new List<LabelWidget>();
			var labelID = 1;
			while (widget.GetOrNull<LabelWidget>("STAT_LABEL_" + labelID.ToString()) != null)
			{
				ExtraStatLabels.Add(widget.Get<LabelWidget>("STAT_LABEL_" + labelID.ToString()));
				labelID++;
			}
			List<ImageWidget> ExtraStatIcons = new List<ImageWidget>();
			var iconID = 1;
			while (widget.GetOrNull<ImageWidget>("STAT_ICON_" + iconID.ToString()) != null)
			{
				ExtraStatIcons.Add(widget.Get<ImageWidget>("STAT_ICON_" + iconID.ToString()));
				iconID++;
			}

			name.GetText = () =>
			{
				var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
				if (largeIconCount > 1 && validActors.Count() != 1)
					return "";

				var unit = validActors.First();
				if (unit != null && !unit.IsDead)
				{
					var usv = unit.Trait<ActorStatValues>();
					if (usv.Tooltips.Any())
					{
						var stance = world.RenderPlayer == null ? PlayerRelationship.None : unit.Owner.RelationshipWith(world.RenderPlayer);
						var actorName = usv.Tooltips.FirstOrDefault(a => !a.IsTraitDisabled).Info.TooltipForPlayerStance(stance);
						return actorName;
					}
				}

				return "";
			};

			iconID = 0;
			foreach (var icon in LargeIcons)
			{
				var index = ++iconID;
				icon.IsVisible = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
					if (smallIconCount > 0 && validActors.Count() > largeIconCount)
						return false;

					return index == 1 || validActors.Count() >= index;
				};

				icon.GetActor = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (validActors.Count() >= index)
						return validActors[index - 1];
					else
						return null;
				};
			}
			iconID = 0;
			foreach (var icon in SmallIcons)
			{
				var index = ++iconID;
				icon.IsVisible = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
					return validActors.Count() > largeIconCount && validActors.Count() >= index;
				};

				icon.GetActor = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (validActors.Count() >= index)
						return validActors[index - 1];
					else
						return null;
				};
			}

			if (more != null)
			{
				more.GetText = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
					if (validActors.Count() <= largeIconCount + smallIconCount)
						return "";
					else
						return "+" + (validActors.Count() - (largeIconCount + smallIconCount)).ToString();
				};
			}

			for (int i = 0; i < LargeHealthBars.Count(); i++)
			{
				var index = i;
				LargeHealthBars[index].IsVisible = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
					if (smallIconCount > 0 && validActors.Count() > largeIconCount)
						return false;

					return index == 0 || validActors.Count() >= index + 1;
				};

				LargeHealthBars[index].GetHealth = () =>
				{
					var validActors = selection.Actors.Where(a => !a.IsDead && a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (validActors.Count() >= index + 1)
						return validActors[index].Trait<ActorStatValues>().Health;
					else
						return null;
				};
			}

			for (int i = 0; i < SmallHealthBars.Count(); i++)
			{
				var index = i;
				SmallHealthBars[index].IsVisible = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
					return validActors.Count() > largeIconCount && validActors.Count() >= index + 1;
				};

				SmallHealthBars[index].GetHealth = () =>
				{
					var validActors = selection.Actors.Where(a => !a.IsDead && a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (validActors.Count() >= index + 1)
						return validActors[index].Trait<ActorStatValues>().Health;
					else
						return null;
				};
			}

			labelID = 0;
			foreach (var statLabel in ExtraStatLabels)
			{
				var index = ++labelID;
				statLabel.GetText = () =>
				{
					var validActors = selection.Actors.Where(a => !a.IsDead && a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (largeIconCount > 1 && validActors.Count() > 1)
						return "";

					var unit = validActors.FirstOrDefault();
					if (unit != null)
					{
						var usv = unit.Trait<ActorStatValues>();
						var labelText = usv.GetValueFor(index);

						return string.IsNullOrEmpty(labelText) ? "" : statLabel.Text + labelText;
					}

					return statLabel.Text;
				};
			}

			iconID = 0;
			foreach (var statIcon in ExtraStatIcons)
			{
				var index = ++iconID;
				statIcon.IsVisible = () =>
				{
					var validActors = selection.Actors.Where(a => !a.IsDead && a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (largeIconCount > 1 && validActors.Count() > 1)
						return false;

					var unit = validActors.FirstOrDefault();
					if (unit != null)
					{
						var usv = unit.Trait<ActorStatValues>();

						return usv.GetIconFor(index) != null;
					}

					return true;
				};
				statIcon.GetImageName = () =>
				{
					var unit = selection.Actors.FirstOrDefault(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
					if (unit != null && !unit.IsDead)
					{
						var usv = unit.Trait<ActorStatValues>();
						var iconName = usv.GetIconFor(index);

						return string.IsNullOrEmpty(iconName) ? statIcon.ImageName : iconName;
					}

					return statIcon.ImageName;
				};
			}
		}
	}
}
