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

using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Renders a lens flare at an armament muzzle while AttackCharges is charged.")]
	public class WithChargeLensFlareInfo : ConditionalTraitInfo, Requires<AttackChargesInfo>
	{
		[Desc("Armament names whose enabled instance supplies the muzzle position.")]
		public readonly ImmutableArray<string> Armaments = ["primary"];

		[Desc("Color of the flare rays at full charge.")]
		public readonly Color RayColor = Color.Red;

		[Desc("Color of the flare core at full charge.")]
		public readonly Color CoreColor = Color.White;

		[Desc("Horizontal and vertical flare ray lengths in pixels.")]
		public readonly int2 Size = new(28, 22);

		[Desc("Flare ray width in pixels.")]
		public readonly float Width = 2f;

		[Desc("Flare core diameter in pixels.")]
		public readonly float CoreSize = 4f;

		[Desc("Offset added to the flare render depth.")]
		public readonly int ZOffset = 1;

		public override object Create(ActorInitializer init) { return new WithChargeLensFlare(init.Self, this); }
	}

	public class WithChargeLensFlare : ConditionalTrait<WithChargeLensFlareInfo>, IRender
	{
		readonly AttackCharges attackCharges;
		readonly LensFlareRenderable flare;
		readonly IRenderable[] renderable;
		Armament[] armaments;

		public WithChargeLensFlare(Actor self, WithChargeLensFlareInfo info)
			: base(info)
		{
			attackCharges = self.Trait<AttackCharges>();
			flare = new LensFlareRenderable(WPos.Zero, info.ZOffset, info.RayColor, info.CoreColor,
				info.Size.X, info.Size.Y, info.Width, info.CoreSize);
			renderable = [flare];
		}

		protected override void Created(Actor self)
		{
			var matches = new List<Armament>();
			foreach (var armament in self.TraitsImplementing<Armament>())
			{
				foreach (var name in Info.Armaments)
				{
					if (armament.Info.Name != name)
						continue;

					matches.Add(armament);
					break;
				}
			}

			armaments = matches.ToArray();
			base.Created(self);
		}

		public IEnumerable<IRenderable> Render(Actor self, WorldRenderer wr)
		{
			var opacity = attackCharges.NormalizedChargeLevel;
			if (opacity <= 0f || IsTraitDisabled || self.IsDead || !self.IsInWorld || self.World.FogObscures(self))
				return SpriteRenderable.None;

			Armament activeArmament = null;
			foreach (var armament in armaments)
			{
				if (!armament.IsTraitDisabled)
				{
					activeArmament = armament;
					break;
				}
			}

			if (activeArmament == null)
				return SpriteRenderable.None;

			var rayColor = Color.FromArgb((int)(Info.RayColor.A * opacity), Info.RayColor);
			var coreColor = Color.FromArgb((int)(Info.CoreColor.A * opacity), Info.CoreColor);
			if (rayColor.A == 0 && coreColor.A == 0)
				return SpriteRenderable.None;

			var muzzle = self.CenterPosition + activeArmament.MuzzleOffset(self, activeArmament.Barrels[0]);
			flare.Update(muzzle, Info.ZOffset, rayColor, coreColor,
				Info.Size.X, Info.Size.Y, Info.Width, Info.CoreSize);
			return renderable;
		}

		public IEnumerable<Rectangle> ScreenBounds(Actor self, WorldRenderer wr) { return []; }
	}
}
