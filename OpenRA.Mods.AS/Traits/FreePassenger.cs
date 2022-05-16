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

using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Player receives a unit for free as passenger once the trait is enabled.",
		"If you want more than one unit to appear copy this section and assign IDs like FreePassener@2, ...")]
	public class FreePassengerInfo : ConditionalTraitInfo, Requires<CargoInfo>
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Name of the actor.")]
		public readonly string Actor = null;

		[Desc("Whether another actor should spawn upon re-enabling the trait.")]
		public readonly bool AllowRespawn = false;

		public override object Create(ActorInitializer init) { return new FreePassenger(init, this); }
	}

	public class FreePassenger : ConditionalTrait<FreePassengerInfo>
	{
		protected bool allowSpawn = true;
		Cargo cargo;

		public FreePassenger(ActorInitializer init, FreePassengerInfo info)
			: base(info)
		{
			cargo = init.Self.Trait<Cargo>();
		}

		protected override void TraitEnabled(Actor self)
		{
			if (!allowSpawn)
				return;

			allowSpawn = Info.AllowRespawn;

			self.World.AddFrameEndTask(w =>
			{
				var passenger = self.World.Map.Rules.Actors[Info.Actor].TraitInfoOrDefault<PassengerInfo>();

				if (passenger == null || !cargo.Info.Types.Contains(passenger.CargoType) || !cargo.HasSpace(passenger.Weight))
					return;

				var a = w.CreateActor(Info.Actor, new TypeDictionary
				{
					new ParentActorInit(self),
					new LocationInit(self.Location),
					new OwnerInit(self.Owner),
				});

				w.Remove(a);
				cargo.Load(self, a);
			});
		}
	}
}
