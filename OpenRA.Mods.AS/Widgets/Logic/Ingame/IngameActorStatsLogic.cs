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

			List<ActorIconWidget> Icons = new List<ActorIconWidget>();
			Icons.Add(widget.Get<ActorIconWidget>("STAT_ICON"));
			List<HealthBarWidget> HealthBars = new List<HealthBarWidget>();
			HealthBars.Add(widget.Get<HealthBarWidget>("STAT_HEALTH_BAR"));
			var iconCount = 1;
			var iconSpacing = new int2 (2, 2);
			if (logicArgs.ContainsKey("IconCount"))
				iconCount = FieldLoader.GetValue<int>("IconCount", logicArgs["IconCount"].Value);
			if (logicArgs.ContainsKey("IconSpacing"))
				iconSpacing = FieldLoader.GetValue<int2>("IconSpacing", logicArgs["IconSpacing"].Value);
			if (iconCount > 1)
			{
				for (int i = 1; i < iconCount; i++)
				{
					var iconClone = Icons[0].Clone() as ActorIconWidget;
					iconClone.Bounds.X += (iconClone.IconSize.X + iconSpacing.X) * i;

					widget.AddChild(iconClone);
					Icons.Add(iconClone);

					var healthBarClone = HealthBars[0].Clone() as HealthBarWidget;
					healthBarClone.Bounds.X += (healthBarClone.Bounds.Width + iconSpacing.X) * i;

					widget.AddChild(healthBarClone);
					HealthBars.Add(healthBarClone);
				}
			}

			var name = widget.Get<LabelWidget>("STAT_NAME");

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

			iconID = 0;
			foreach (var icon in Icons)
			{
				var index = ++iconID;
				if (index > 1)
				{
					icon.IsVisible = () =>
					{
						var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
						return validActors.Count() >= index;
					};
				}

				icon.GetActor = () =>
				{
					var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (validActors.Count() >= index)
						return validActors[index - 1];
					else
						return null;
				};
			}

			name.GetText = () =>
			{
				var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
				if (iconCount > 1 && validActors.Count() != 1)
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

			for (int i = 0; i < HealthBars.Count(); i++)
			{
				var index = i;
				if (index > 0)
				{
					HealthBars[index].IsVisible = () =>
					{
						var validActors = selection.Actors.Where(a => a.Info.HasTraitInfo<ActorStatValuesInfo>());
						return validActors.Count() >= index + 1;
					};
				}

				HealthBars[index].GetHealth = () =>
				{
					var validActors = selection.Actors.Where(a => !a.IsDead && a.Info.HasTraitInfo<ActorStatValuesInfo>()).ToArray();
					if (validActors.Count() >= index + 1)
					{
						return validActors[index].Trait<ActorStatValues>().Health;
					}
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
					if (iconCount > 1 && validActors.Count() > 1)
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
					if (iconCount > 1 && validActors.Count() > 1)
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
