#region Copyright & License Information
/*
 * Copyright 2007-2021 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

namespace OpenRA
{
	public interface ILog
	{
		void Write(string channel, string format, params object[] args);
	}

	public class LogProxy : ILog
	{
		public void Write(string channel, string format, params object[] args)
		{
			Log.Write(channel, format, args);
		}
	}
}
