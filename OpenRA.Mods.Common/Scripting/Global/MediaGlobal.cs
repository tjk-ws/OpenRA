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

using System;
using Eluant;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Scripting;

namespace OpenRA.Mods.Common.Scripting
{
	[ScriptGlobal("Media")]
	public class MediaGlobal : ScriptGlobal
	{
		readonly World world;
		readonly MusicPlaylist playlist;

		public MediaGlobal(ScriptContext context)
			: base(context)
		{
			world = context.World;
			playlist = world.WorldActor.Trait<MusicPlaylist>();
		}

		[Desc("Play an announcer voice listed in notifications.yaml")]
		public void PlaySpeechNotification(Player player, string notification)
		{
			Game.Sound.PlayNotification(world.Map.Rules, player, "Speech", notification, player?.Faction.InternalName);
		}

		[Desc("Play a sound listed in notifications.yaml")]
		public void PlaySoundNotification(Player player, string notification)
		{
			Game.Sound.PlayNotification(world.Map.Rules, player, "Sounds", notification, player?.Faction.InternalName);
		}

		[Desc("Play a sound file")]
		public void PlaySound(string file)
		{
			// TODO: Investigate how scripts use this function, and think about exposing the UI vs World distinction if needed
			Game.Sound.Play(SoundType.World, file);
		}

		[Desc("Play track defined in music.yaml or map.yaml, or keep track empty for playing a random song.")]
		public void PlayMusic(string track = null, [ScriptEmmyTypeOverride("fun()")] LuaFunction onPlayComplete = null)
		{
			if (!playlist.IsMusicAvailable)
				return;

			var musicInfo = !string.IsNullOrEmpty(track)
				? GetMusicTrack(track)
				: playlist.GetNextSong();

			var onComplete = WrapOnPlayComplete(onPlayComplete);
			playlist.Play(musicInfo, onComplete);
		}

		[Desc("Play track defined in music.yaml or map.yaml as background music." +
			" If music is already playing use Media.StopMusic() to stop it" +
			" and the background music will start automatically." +
			" Keep the track empty to disable background music.")]
		public void SetBackgroundMusic(string track = null)
		{
			if (!playlist.IsMusicAvailable)
				return;

			playlist.SetBackgroundMusic(string.IsNullOrEmpty(track) ? null : GetMusicTrack(track));
		}

		MusicInfo GetMusicTrack(string track)
		{
			var music = world.Map.Rules.Music;
			if (music.ContainsKey(track))
				return music[track];

			Log.Write("lua", "Missing music track: " + track);
			return null;
		}

		[Desc("Stop the current song.")]
		public void StopMusic()
		{
			playlist.Stop();
		}

		[Desc("Play a video fullscreen. File name has to include the file extension.")]
		public void PlayMovieFullscreen(string videoFileName, [ScriptEmmyTypeOverride("fun()")] LuaFunction onPlayComplete = null)
		{
			var onComplete = WrapOnPlayComplete(onPlayComplete);

			// Decoupled rendering: Lua runs on the sim thread; PlayFMVFullscreen opens a UI window
			// (Ui.OpenWindow/CloseWindow), so marshal the window operation to the main thread. The Lua-ref wrapping
			// above stays on the sim/Lua thread.
			if (Game.IsOnMainThread)
				Media.PlayFMVFullscreen(world, videoFileName, onComplete);
			else
				Game.RunAfterTick(() => Media.PlayFMVFullscreen(world, videoFileName, onComplete));
		}

		[Desc("Play a video in the radar window. File name has to include the file extension.")]
		public void PlayMovieInRadar(string videoFileName, [ScriptEmmyTypeOverride("fun()")] LuaFunction onPlayComplete = null)
		{
			var onComplete = WrapOnPlayComplete(onPlayComplete);

			// Decoupled rendering: marshal the radar-window UI operation to the main thread (see above).
			if (Game.IsOnMainThread)
				Media.PlayFMVInRadar(videoFileName, onComplete);
			else
				Game.RunAfterTick(() => Media.PlayFMVInRadar(videoFileName, onComplete));
		}

		[Desc("Display a text message to all players.")]
		public void DisplayMessage(string text, string prefix = "Mission", Color? color = null)
		{
			if (string.IsNullOrEmpty(text))
				return;

			var c = color ?? Color.White;
			TextNotificationsManager.AddMissionLine(prefix, text, c);
		}

		[Desc("Display a text message only to this player.")]
		public void DisplayMessageToPlayer(Player player, string text, string prefix = "Mission", Color? color = null)
		{
			if (world.LocalPlayer != player)
				return;

			DisplayMessage(text, prefix, color);
		}

		[Desc("Display a system message to the player. If 'prefix' is nil the default system prefix is used.")]
		public void DisplaySystemMessage(string text, string prefix = null)
		{
			if (string.IsNullOrEmpty(text))
				return;

			if (string.IsNullOrEmpty(prefix))
				TextNotificationsManager.AddSystemLine(text);
			else
				TextNotificationsManager.AddSystemLine(prefix, text);
		}

		[Desc("Displays a debug message to the player, if \"Show Map Debug Messages\" is checked in the settings.")]
		public void Debug(string format)
		{
			if (string.IsNullOrEmpty(format) || !Game.Settings.Debug.LuaDebug)
				return;

			TextNotificationsManager.Debug(format);
		}

		[Desc("Display a text message at the specified location.")]
		public void FloatingText(string text, WPos position, int duration = 30, Color? color = null)
		{
			if (string.IsNullOrEmpty(text) || !world.Map.Contains(world.Map.CellContaining(position)))
				return;

			var c = color ?? Color.White;
			world.AddFrameEndTask(w => w.Add(new FloatingText(position, c, text, duration)));
		}

		Action WrapOnPlayComplete(LuaFunction onPlayComplete)
		{
			if (onPlayComplete != null)
			{
				var f = (LuaFunction)onPlayComplete.CopyReference();
				return () =>
				{
					void CallLua()
					{
						try
						{
							using (f)
								f.Call().Dispose();
						}
						catch (LuaException e)
						{
							Context.FatalError(e);
						}
					}

					// Decoupled rendering: the Lua continuation must run on the sim thread (single Lua VM). When decoupled,
					// movie/music completion fires on the MAIN thread, so marshal it onto the sim via AddFrameEndTask.
					// When the feature is OFF (main IS the sim thread) - or this already runs on the sim thread - run it
					// INLINE so stock on-completion timing is preserved (no frame-end deferral, no local movie timing
					// injected into sim ordering, so toggle-OFF stays byte-identical).
					if (Game.IsDecoupledRunning && Game.IsOnMainThread)
						world.AddFrameEndTask(_ => CallLua());
					else
						CallLua();
				};
			}
			else
				return () => { };
		}
	}
}
