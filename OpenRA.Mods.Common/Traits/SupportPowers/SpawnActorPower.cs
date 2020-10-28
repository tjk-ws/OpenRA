#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Spawns an actor that stays for a limited amount of time.")]
	public class SpawnActorPowerInfo : SupportPowerInfo
	{
		[FieldLoader.Require]
		[Desc("Actors to spawn for each level.")]
		public readonly Dictionary<int, string> Actors = new Dictionary<int, string>();

		[Desc("Amount of time to keep the actor alive in ticks. Value < 0 means this actor will not remove itself.")]
		public readonly int LifeTime = 250;

		public readonly string DeploySound = null;

		public readonly string EffectImage = null;

		[SequenceReference(nameof(EffectImage))]
		public readonly string EffectSequence = null;

		[PaletteReference]
		public readonly string EffectPalette = null;

		public readonly Dictionary<int, WDist> TargetCircleRanges;
		public readonly Color TargetCircleColor = Color.White;
		public readonly bool TargetCircleUsePlayerColor = false;
		public readonly float TargetCircleWidth = 1;
		public readonly Color TargetCircleBorderColor = Color.FromArgb(96, Color.Black);
		public readonly float TargetCircleBorderWidth = 3;

		public override object Create(ActorInitializer init) { return new SpawnActorPower(init.Self, this); }
	}

	public class SpawnActorPower : SupportPower
	{
		public new readonly SpawnActorPowerInfo Info;

		public SpawnActorPower(Actor self, SpawnActorPowerInfo info)
			: base(self, info)
		{
			Info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);

			var info = Info as SpawnActorPowerInfo;

			if (info.Actors != null)
			{
				self.World.AddFrameEndTask(w =>
				{
					PlayLaunchSounds();
					Game.Sound.Play(SoundType.World, info.DeploySound, order.Target.CenterPosition);

					if (!string.IsNullOrEmpty(info.EffectSequence) && !string.IsNullOrEmpty(info.EffectPalette))
						w.Add(new SpriteEffect(order.Target.CenterPosition, w, info.EffectImage, info.EffectSequence, info.EffectPalette));

					var actor = w.CreateActor(info.Actors.First(a => a.Key == GetLevel()).Value, new TypeDictionary
					{
						new LocationInit(self.World.Map.CellContaining(order.Target.CenterPosition)),
						new OwnerInit(self.Owner),
					});

					if (info.LifeTime > -1)
					{
						actor.QueueActivity(new Wait(info.LifeTime));
						actor.QueueActivity(new RemoveSelf());
					}
				});
			}
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			Game.Sound.PlayToPlayer(SoundType.UI, manager.Self.Owner, Info.SelectTargetSound);
			Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech",
				Info.SelectTargetSpeechNotification, self.Owner.Faction.InternalName);
			self.World.OrderGenerator = new SelectSpawnActorPowerTarget(order, manager, this);
		}
	}

	public class SelectSpawnActorPowerTarget : OrderGenerator
	{
		readonly SupportPowerManager manager;
		readonly string order;
		readonly SpawnActorPower power;

		public SelectSpawnActorPowerTarget(string order, SupportPowerManager manager, SpawnActorPower power)
		{
			// Clear selection if using Left-Click Orders
			if (Game.Settings.Game.UseClassicMouseStyle)
				manager.Self.World.Selection.Clear();

			this.manager = manager;
			this.order = order;
			this.power = power;
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			world.CancelInputMode();
			if (mi.Button == MouseButton.Left && world.Map.Contains(cell))
				yield return new Order(order, manager.Self, Target.FromCell(world, cell), false) { SuppressVisualFeedback = true };
		}

		protected override void Tick(World world)
		{
			// Cancel the OG if we can't use the power
			if (!manager.Powers.ContainsKey(order))
				world.CancelInputMode();
		}

		protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }

		protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }

		protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world)
		{
			var xy = wr.Viewport.ViewToWorld(Viewport.LastMousePos);

			if (power.Info.TargetCircleRanges == null || !power.Info.TargetCircleRanges.Any() || power.GetLevel() == 0)
			{
				yield break;
			}
			else
			{
				yield return new RangeCircleAnnotationRenderable(
					world.Map.CenterOfCell(xy),
					power.Info.TargetCircleRanges[power.GetLevel()],
					0,
					power.Info.TargetCircleUsePlayerColor ? power.Self.Owner.Color : power.Info.TargetCircleColor,
					power.Info.TargetCircleWidth,
					power.Info.TargetCircleBorderColor,
					power.Info.TargetCircleBorderWidth);
			}
		}

		protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			return world.Map.Contains(cell) ? power.Info.Cursor : "generic-blocked";
		}
	}
}
