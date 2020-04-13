#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public class ChangeOwnerOnGarrisonerInfo : ITraitInfo, Requires<GarrisonableInfo>
	{
		[Desc("Speech notification played when the first actor enters this garrison.")]
		public readonly string EnterNotification = null;

		[Desc("Speech notification played when the last actor leaves this garrison.")]
		public readonly string ExitNotification = null;

		[Desc("Sound played when the first actor enters this garrison.")]
		public readonly string EnterSound = null;

		[Desc("Sound played when the last actor exits this garrison.")]
		public readonly string ExitSound = null;

		public object Create(ActorInitializer init) { return new ChangeOwnerOnGarrisoner(init.Self, this); }
	}

	public class ChangeOwnerOnGarrisoner : INotifyGarrisonerEntered, INotifyGarrisonerExited, INotifyOwnerChanged
	{
		readonly ChangeOwnerOnGarrisonerInfo info;
		readonly Garrisonable garrison;

		Player originalOwner;
		bool garrisoning;

		public ChangeOwnerOnGarrisoner(Actor self, ChangeOwnerOnGarrisonerInfo info)
		{
			this.info = info;
			garrison = self.Trait<Garrisonable>();
			originalOwner = self.Owner;
		}

		void INotifyGarrisonerEntered.OnGarrisonerEntered(Actor self, Actor garrisoner)
		{
			var newOwner = garrisoner.Owner;
			if (self.Owner != originalOwner || self.Owner == newOwner || self.Owner.IsAlliedWith(garrisoner.Owner))
				return;

			garrisoning = true;
			self.ChangeOwner(newOwner);
			Game.Sound.Play(SoundType.World, info.EnterSound, self.CenterPosition);
			Game.Sound.PlayNotification(self.World.Map.Rules, garrisoner.Owner, "Speech", info.EnterNotification, newOwner.Faction.InternalName);
			self.World.AddFrameEndTask(_ => garrisoning = false);
		}

		void INotifyGarrisonerExited.OnGarrisonerExited(Actor self, Actor garrisoner)
		{
			if (garrison.GarrisonerCount > 0)
				return;

			garrisoning = true;
			Game.Sound.Play(SoundType.World, info.ExitSound, self.CenterPosition);
			Game.Sound.PlayNotification(self.World.Map.Rules, garrisoner.Owner, "Speech", info.ExitNotification, garrisoner.Owner.Faction.InternalName);
			self.ChangeOwner(originalOwner);
			self.World.AddFrameEndTask(_ => garrisoning = false);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			if (!garrisoning)
				originalOwner = newOwner;
		}
	}
}
