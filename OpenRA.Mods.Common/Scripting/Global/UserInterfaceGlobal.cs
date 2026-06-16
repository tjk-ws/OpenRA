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

using Eluant;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Scripting;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Scripting.Global
{
	[ScriptGlobal("UserInterface")]
	public class UserInterfaceGlobal : ScriptGlobal
	{
		public UserInterfaceGlobal(ScriptContext context)
			: base(context) { }

		[Desc("Displays a text message at the top center of the screen.")]
		public void SetMissionText(string text, Color? color = null)
		{
			// Decoupled rendering: Lua mission scripts run on the background sim thread (World.Tick), so
			// this widget-tree traversal (Ui.Root.Get) + label mutation would race the main thread. Marshal to the
			// main thread (inline when already on it; OFF path unaffected).
			void Apply()
			{
				var luaLabel = Ui.Root.Get("INGAME_ROOT").Get<LabelWidget>("MISSION_TEXT");
				luaLabel.GetText = () => text;

				var c = color ?? Color.White;
				luaLabel.GetColor = () => c;
			}

			if (Game.IsOnMainThread)
				Apply();
			else
				Game.RunAfterTick(Apply);
		}

		[Desc("Formats a language string for a given string key defined in the language files (*.ftl). " +
			"Args can be passed to be substituted into the resulting message.")]
		public string GetFluentMessage(string key, [ScriptEmmyTypeOverride("{ string: any }")] LuaTable args = null)
		{
			if (args != null)
			{
				var argumentDictionary = new object[args.Count * 2];
				var i = 0;
				foreach (var kv in args)
				{
					using (kv.Key)
					using (kv.Value)
					{
						if (!kv.Key.TryGetClrValue<string>(out var variable) || !kv.Value.TryGetClrValue<object>(out var value))
							throw new LuaException(
								"String arguments requires a table of [\"string\"]=value pairs. " +
								$"Received {kv.Key.WrappedClrType().Name},{kv.Value.WrappedClrType().Name}");

						argumentDictionary[i++] = variable;
						argumentDictionary[i++] = value;
					}
				}

				return FluentProvider.GetMessage(key, argumentDictionary);
			}

			return FluentProvider.GetMessage(key);
		}
	}
}
