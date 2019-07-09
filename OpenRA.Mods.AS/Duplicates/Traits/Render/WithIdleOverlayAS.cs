#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits.Render
{
	public class WithIdleOverlayASInfo : WithIdleOverlayInfo
	{
		[Desc("Image name to use, if null, it falls back to default.")]
		public readonly string Image = "";

		public override object Create(ActorInitializer init) { return new WithIdleOverlayAS(init.Self, this); }

		public new IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, RenderSpritesInfo rs, string image, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			if (Palette != null)
				p = init.WorldRenderer.Palette(Palette);

			var idleImage = !string.IsNullOrEmpty(Image) ? Image : image;
			Func<int> facing;
			if (init.Contains<DynamicFacingInit>())
				facing = init.Get<DynamicFacingInit, Func<int>>();
			else
			{
				var f = init.Contains<FacingInit>() ? init.Get<FacingInit, int>() : 0;
				facing = () => f;
			}

			var anim = new Animation(init.World, idleImage, facing);
			anim.PlayRepeating(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), Sequence));

			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			Func<WRot> orientation = () => body.QuantizeOrientation(WRot.FromFacing(facing()), facings);
			Func<WVec> offset = () => body.LocalToWorld(Offset.Rotate(orientation()));
			Func<int> zOffset = () =>
			{
				var tmpOffset = offset();
				return tmpOffset.Y + tmpOffset.Z + 1;
			};

			yield return new SpriteActorPreview(anim, offset, zOffset, p, rs.Scale);
		}
	}

	public class WithIdleOverlayAS : PausableConditionalTrait<WithIdleOverlayASInfo>, INotifyDamageStateChanged
	{
		readonly Animation overlay;

		public WithIdleOverlayAS(Actor self, WithIdleOverlayASInfo info)
			: base(info)
		{
			var rs = self.Trait<RenderSprites>();
			var body = self.Trait<BodyOrientation>();

			var image = !string.IsNullOrEmpty(info.Image) ? info.Image : rs.GetImage(self);

			overlay = new Animation(self.World, image, () => IsTraitPaused);
			if (info.StartSequence != null)
				overlay.PlayThen(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), info.StartSequence),
					() => overlay.PlayRepeating(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), info.Sequence)));
			else
				overlay.PlayRepeating(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), info.Sequence));

			var anim = new AnimationWithOffset(overlay,
				() => body.LocalToWorld(info.Offset.Rotate(body.QuantizeOrientation(self, self.Orientation))),
				() => IsTraitDisabled,
				p => RenderUtils.ZOffsetFromCenter(self, p, 1));

			rs.Add(anim, info.Palette, info.IsPlayerPalette);
		}

		void INotifyDamageStateChanged.DamageStateChanged(Actor self, AttackInfo e)
		{
			overlay.ReplaceAnim(RenderSprites.NormalizeSequence(overlay, e.DamageState, overlay.CurrentSequence.Name));
		}
	}
}
