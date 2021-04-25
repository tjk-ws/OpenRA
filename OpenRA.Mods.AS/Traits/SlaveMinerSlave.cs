#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public enum SlaveState
	{
		Free,
		Idle,
	}

	public class SlaveMinerSlaveInfo : BaseSpawnerSlaveInfo, Requires<HarvesterInfo>
	{
		[Desc("What will happen when master was killed?")]
		public readonly SlaveState OnMasterKilled = SlaveState.Idle;

		[Desc("What will happen when master is changed owner?")]
		public readonly SlaveState OnMasterOwnerChanged = SlaveState.Idle;

		public override object Create(ActorInitializer init) { return new SlaveMinerSlave(init, this); }
	}

	class SlaveMinerSlave : BaseSpawnerSlave, ITick
	{
		SlaveMinerHarvester spawnerHarvesterMaster;
		SlaveMinerSlaveInfo info;

		public SlaveMinerSlave(ActorInitializer init, SlaveMinerSlaveInfo info)
			: base(init, info)
		{
			this.info = info;
		}

		public override void LinkMaster(Actor self, Actor master, BaseSpawnerMaster spawnerMaster)
		{
			base.LinkMaster(self, master, spawnerMaster);

			spawnerHarvesterMaster = Master.TraitOrDefault<SlaveMinerHarvester>();
		}

		public override void OnMasterKilled(Actor self, Actor attacker, SpawnerSlaveDisposal disposal)
		{
			switch(info.OnMasterKilled)
			{
				case SlaveState.Free:
					self.ChangeOwner(attacker.Owner);
					break;
			}
		}

		public override void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			switch(info.OnMasterOwnerChanged)
			{
				case SlaveState.Free:
					self.ChangeOwner(newOwner);
					break;
			}
		}

		public void Tick(Actor self)
		{/*
			// Compensate for bug #13879 (upstream).
			// https://github.com/OpenRA/OpenRA/issues/13879
			// Follow activity sometimes fails to cancel and the slaves get busy locked by WaitFor activity.
			if (spawnerHarvesterMaster?.MiningState == MiningState.Mining && self.CurrentActivity is WaitFor)
			{
				self.CancelActivity();

				/// No need to run this here, since it already happened.
				/// This slave is just bugged out by Follow activity not canceling properly.
				/// AssignTargetForSpawned(s, self.Location);

				self.QueueActivity(new FindAndDeliverResources(self));
			}
		*/}
	}
}
