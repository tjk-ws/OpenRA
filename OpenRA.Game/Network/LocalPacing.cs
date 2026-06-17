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

using System.Globalization;

namespace OpenRA.Network
{
	/// <summary>
	/// Bridge for single-player adaptive game-speed ("Stage C"). Lets a mod-side controller feed a
	/// locally-derived pacing scale into the SAME server-authoritative TickScale path the network uses
	/// (<see cref="OrderManager.ReceiveTickScale"/> → SuggestedTimestep → the game-loop logicInterval).
	/// It changes ONLY wall-clock pacing, never <c>World.Timestep</c> or the simulated dt, so it cannot
	/// affect determinism. This bridge does NO gating: callers are responsible for restricting use to
	/// local, non-replay, single-human games (a locally-derived scale must never leak into MP/replay).
	/// </summary>
	public static class LocalPacing
	{
		public static void SetTickScale(float scale)
		{
			Game.OrderManager?.ReceiveTickScale(scale);
		}
	}

	/// <summary>
	/// Bridge for the MULTIPLAYER host-authoritative adaptive game-speed driver (Stage C Prong C). Sends the
	/// host's absolute pacing scale straight to the SERVER as a frame-0 command via
	/// <see cref="IConnection.SendServerCommand"/>, which bypasses the local immediate-order path — so the
	/// value is NOT echoed back, processed locally, or recorded into the host's replay (matching the server's
	/// TickScale frame, which is likewise un-recorded and un-synced). The server validates that the sender is
	/// the admin/host, sanitizes the value, and folds it into the authoritative TickScale broadcast. Pacing
	/// only; never touches the simulated dt. Callers must restrict use to the host in a networked game.
	/// </summary>
	public static class HostPacing
	{
		public static void SendScale(float scale)
		{
			Game.OrderManager?.Connection?.SendServerCommand(
				Order.FromTargetString("AdaptivePacingScale", scale.ToString("F3", CultureInfo.InvariantCulture), true));
		}
	}
}
