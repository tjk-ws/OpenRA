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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using OpenRA.FileSystem;
using OpenRA.GameRules;
using OpenRA.Primitives;

namespace OpenRA
{
	public interface ISoundLoader
	{
		bool TryParseSound(Stream stream, out ISoundFormat sound);
	}

	public interface ISoundFormat : IDisposable
	{
		int Channels { get; }
		int SampleBits { get; }
		int SampleRate { get; }
		float LengthInSeconds { get; }
		Stream GetPCMInputStream();
	}

	public enum SoundType { World, UI }

	public sealed class Sound : IDisposable
	{
		readonly ISoundEngine soundEngine;
		ISoundLoader[] loaders;
		IReadOnlyFileSystem fileSystem;
		Cache<string, ISoundSource> sounds;
		ISoundSource videoSource;
		ISound music;
		ISound video;
		readonly Dictionary<uint, ISound> currentSounds = [];
		readonly Dictionary<string, ISound> currentNotifications = [];

		// EVA speech notifications are played one at a time: newer lines queue behind the
		// current one, and any that have waited longer than the configured expiry are dropped.
		// Notifications flagged Priority bypass the queue and play immediately; sound effects and
		// unit voices are not affected.
		const string SpeechNotificationType = "speech";
		readonly List<QueuedSpeechNotification> speechQueue = [];
		ISound currentSpeechNotification;
		string currentSpeechDefinition;
		long nextSpeechAt;
		int speechQueueDelay;
		int speechQueueExpiry = 5000;
		readonly Dictionary<string, long> speechNotificationReadyAt = [];

		sealed class QueuedSpeechNotification
		{
			public long EnqueuedAt;
			public string Definition;
			public Func<ISound> Play;
		}

		public bool DummyEngine { get; }

		public Sound(IPlatform platform, SoundSettings soundSettings)
		{
			soundEngine = platform.CreateSound(soundSettings.Device);
			DummyEngine = soundEngine.Dummy;
			soundEngine.SetCategoryVolume(SoundCategory.SoundEffect, soundSettings.SoundVolume);
			soundEngine.SetCategoryVolume(SoundCategory.EVA, soundSettings.MuteEVA ? 0f : soundSettings.EVAVolume);
			soundEngine.SetCategoryVolume(SoundCategory.UnitVoice, soundSettings.MuteUnitVoices ? 0f : soundSettings.UnitVoiceVolume);

			if (soundSettings.Mute)
				MuteAudio();
		}

		T LoadSound<T>(string filename, Func<ISoundFormat, T> loadFormat)
		{
			if (!fileSystem.Exists(filename))
			{
				Log.Write("sound", $"LoadSound, file does not exist: {filename}");
				return default;
			}

			using (var stream = fileSystem.Open(filename))
			{
				foreach (var loader in loaders)
				{
					stream.Position = 0;
					if (loader.TryParseSound(stream, out var soundFormat))
					{
						var source = loadFormat(soundFormat);
						soundFormat.Dispose();
						return source;
					}
				}
			}

			throw new InvalidDataException(filename + " is not a valid sound file!");
		}

		public void Initialize(ISoundLoader[] loaders, IReadOnlyFileSystem fileSystem)
		{
			StopMusic();
			soundEngine.StopAllSounds();

			if (sounds != null)
				foreach (var soundSource in sounds.Values)
					soundSource?.Dispose();

			this.loaders = loaders;
			this.fileSystem = fileSystem;
			ISoundSource LoadIntoMemory(ISoundFormat soundFormat) => soundEngine.AddSoundSourceFromMemory(
				soundFormat.GetPCMInputStream().ReadAllBytes(), soundFormat.Channels, soundFormat.SampleBits, soundFormat.SampleRate);
			sounds = new Cache<string, ISoundSource>(filename => LoadSound(filename, LoadIntoMemory));
			currentSounds.Clear();
			currentNotifications.Clear();
			ResetSpeechNotifications();
			video = null;
		}

		public SoundDevice[] AvailableDevices()
		{
			return soundEngine.AvailableDevices();
		}

		/// <summary>
		/// Switches the output device live without restarting the game.
		/// <paramref name="device"/> of <c>null</c> selects the system default output.
		/// Returns <c>false</c> if hot-swapping isn't supported (caller should fall
		/// back to requiring a restart).
		/// </summary>
		public bool SetDevice(string device)
		{
			return soundEngine.TrySetDevice(device);
		}

		public void SetListenerPosition(WPos position, int viewportHalfWidth)
		{
			soundEngine.SetListenerPosition(position, viewportHalfWidth);
		}

		ISound Play(SoundType type, Player player, string name, bool headRelative, WPos pos, float volumeModifier = 1f, bool loop = false)
		{
			if (string.IsNullOrEmpty(name) || DisableAllSounds || (DisableWorldSounds && type == SoundType.World))
				return null;

			if (player != null && player != player.World.LocalPlayer)
				return null;

			return soundEngine.Play2D(sounds[name], loop, headRelative, pos, volumeModifier, true, SoundCategory.SoundEffect);
		}

		public void StopAudio()
		{
			soundEngine.StopAllSounds();
			ResetSpeechNotifications();
		}

		void ResetSpeechNotifications()
		{
			speechQueue.Clear();
			currentSpeechNotification = null;
			currentSpeechDefinition = null;
			nextSpeechAt = 0;
			speechNotificationReadyAt.Clear();
		}

		public void SetLooped(ISound sound, bool looped)
		{
			soundEngine.SetSoundLooping(looped, sound);
		}

		public void SetPosition(ISound sound, WPos position)
		{
			soundEngine.SetSoundPosition(sound, position);
		}

		public void MuteAudio()
		{
			soundEngine.Volume = 0f;
		}

		public void UnmuteAudio()
		{
			soundEngine.Volume = 1f;
		}

		public void SetMusicLooped(bool loop)
		{
			Game.Settings.Sound.Repeat = loop;
			soundEngine.SetSoundLooping(loop, music);
		}

		public bool DisableAllSounds { get; set; }
		public bool DisableWorldSounds { get; set; }
		public ISound Play(SoundType type, string name) { return Play(type, null, name, true, WPos.Zero, 1f); }
		public ISound Play(SoundType type, string name, WPos pos) { return Play(type, null, name, false, pos, 1f); }
		public ISound Play(SoundType type, string name, float volumeModifier) { return Play(type, null, name, true, WPos.Zero, volumeModifier); }
		public ISound Play(SoundType type, string name, WPos pos, float volumeModifier) { return Play(type, null, name, false, pos, volumeModifier); }
		public ISound PlayToPlayer(SoundType type, Player player, string name) { return Play(type, player, name, true, WPos.Zero, 1f); }
		public ISound PlayToPlayer(SoundType type, Player player, string name, WPos pos) { return Play(type, player, name, false, pos, 1f); }
		public ISound PlayLooped(SoundType type, string name) { return Play(type, null, name, true, WPos.Zero, 1f, true); }
		public ISound PlayLooped(SoundType type, string name, float volumeModifier) { return Play(type, null, name, true, WPos.Zero, volumeModifier, true); }
		public ISound PlayLooped(SoundType type, string name, WPos pos) { return Play(type, null, name, false, pos, 1f, true); }
		public ISound PlayLooped(SoundType type, string name, WPos pos, float volumeModifier) { return Play(type, null, name, false, pos, volumeModifier, true); }

		public ISound Play(SoundType type, ImmutableArray<string> names, World world, Player player = null, float volumeModifier = 1f)
		{
			return Play(type, player, names.Random(world.LocalRandom), true, WPos.Zero, volumeModifier);
		}

		public ISound Play(SoundType type, ImmutableArray<string> names, World world, WPos pos, Player player = null, float volumeModifier = 1f)
		{
			return Play(type, player, names.Random(world.LocalRandom), false, pos, volumeModifier);
		}

		public ISound Play(ISoundFormat soundFormat) => Play(soundFormat, MusicVolume);

		public ISound Play(ISoundFormat soundFormat, float volume)
		{
			return soundEngine.Play2DStream(soundFormat.GetPCMInputStream(), soundFormat.Channels, soundFormat.SampleBits, soundFormat.SampleRate,
				false, true, WPos.Zero, volume);
		}

		public void PlayVideo(byte[] raw, int channels, int sampleBits, int sampleRate)
		{
			StopVideo();
			videoSource = soundEngine.AddSoundSourceFromMemory(raw, channels, sampleBits, sampleRate);
			video = soundEngine.Play2D(videoSource, false, true, WPos.Zero, VideoVolume, false, SoundCategory.None);
		}

		public void PlayVideo()
		{
			if (video != null)
				soundEngine.PauseSound(video, false);
		}

		public void PauseVideo()
		{
			if (video != null)
				soundEngine.PauseSound(video, true);
		}

		public void StopVideo()
		{
			if (video != null)
			{
				soundEngine.StopSound(video);
				videoSource.Dispose();
				videoSource = null;
				video = null;
			}
		}

		public void Tick()
		{
			// Song finished
			if (MusicPlaying && music.Complete)
			{
				StopMusic();
				onMusicComplete();
			}

			TickSpeechNotifications();
		}

		// Plays queued EVA speech notifications one at a time, dropping any that have waited too long.
		void TickSpeechNotifications()
		{
			if (speechQueue.Count > 0)
			{
				var now = Game.RunTime;
				speechQueue.RemoveAll(q => now - q.EnqueuedAt > speechQueueExpiry);
			}

			if (currentSpeechNotification != null && !currentSpeechNotification.Complete)
				return;

			// When a line finishes, hold the next one back by the inter-line delay so queued
			// speech is spaced out rather than played back-to-back.
			if (currentSpeechNotification != null)
			{
				currentSpeechNotification = null;
				currentSpeechDefinition = null;
				nextSpeechAt = Game.RunTime + speechQueueDelay;
			}

			if (Game.RunTime < nextSpeechAt)
				return;

			while (speechQueue.Count > 0)
			{
				var next = speechQueue[0];
				speechQueue.RemoveAt(0);
				var sound = next.Play();
				if (sound != null)
				{
					currentSpeechNotification = sound;
					currentSpeechDefinition = next.Definition;
					break;
				}
			}
		}

		Action onMusicComplete;
		public bool MusicPlaying { get; private set; }
		public MusicInfo CurrentMusic { get; private set; }

		public void PlayMusicThen(MusicInfo m, Action then)
		{
			if (m == null || !m.Exists)
				return;

			onMusicComplete = then;

			if (m == CurrentMusic && music != null)
			{
				soundEngine.PauseSound(music, false);
				MusicPlaying = true;
				return;
			}

			PlayMusic(m, Game.Settings.Sound.Repeat);
		}

		public void PlayMusic(MusicInfo m, bool looped = false)
		{
			if (m == null || !m.Exists)
				return;

			StopMusic();

			ISound Stream(ISoundFormat soundFormat) => soundEngine.Play2DStream(
				soundFormat.GetPCMInputStream(), soundFormat.Channels, soundFormat.SampleBits, soundFormat.SampleRate,
				looped, true, WPos.Zero, MusicVolume * m.VolumeModifier);

			music = LoadSound(m.Filename, Stream);
			if (music == null)
			{
				onMusicComplete = null;
				return;
			}

			CurrentMusic = m;
			MusicPlaying = true;
		}

		public void PlayMusic()
		{
			if (music == null)
				return;

			MusicPlaying = true;
			soundEngine.PauseSound(music, false);
		}

		public void StopSound(ISound sound)
		{
			if (sound != null)
				soundEngine.StopSound(sound);
		}

		public void StopMusic()
		{
			if (music != null)
			{
				soundEngine.StopSound(music);
				music = null;
			}

			CurrentMusic = null;
			MusicPlaying = false;
		}

		public void PauseMusic()
		{
			if (music == null)
				return;

			MusicPlaying = false;
			soundEngine.PauseSound(music, true);
		}

		float soundVolumeModifier = 1.0f;
		public float SoundVolumeModifier
		{
			get => soundVolumeModifier;

			set
			{
				soundVolumeModifier = value;
				UpdateCategoryVolumes();
			}
		}

		void UpdateCategoryVolumes()
		{
			soundEngine.SetCategoryVolume(SoundCategory.SoundEffect, SoundVolume * soundVolumeModifier);
			soundEngine.SetCategoryVolume(SoundCategory.EVA,
				Game.Settings.Sound.MuteEVA ? 0f : EVAVolume * soundVolumeModifier);
			soundEngine.SetCategoryVolume(SoundCategory.UnitVoice,
				Game.Settings.Sound.MuteUnitVoices ? 0f : UnitVoiceVolume * soundVolumeModifier);
		}

		public float SoundVolume
		{
			get => Game.Settings.Sound.SoundVolume;

			set
			{
				Game.Settings.Sound.SoundVolume = value;
				soundEngine.SetCategoryVolume(SoundCategory.SoundEffect, value * soundVolumeModifier);
			}
		}

		public float EVAVolume
		{
			get => Game.Settings.Sound.EVAVolume;
			set
			{
				Game.Settings.Sound.EVAVolume = value;
				soundEngine.SetCategoryVolume(SoundCategory.EVA,
					Game.Settings.Sound.MuteEVA ? 0f : value * soundVolumeModifier);
			}
		}

		public float UnitVoiceVolume
		{
			get => Game.Settings.Sound.UnitVoiceVolume;
			set
			{
				Game.Settings.Sound.UnitVoiceVolume = value;
				soundEngine.SetCategoryVolume(SoundCategory.UnitVoice,
					Game.Settings.Sound.MuteUnitVoices ? 0f : value * soundVolumeModifier);
			}
		}

		public void SetEVAMuted(bool muted)
		{
			Game.Settings.Sound.MuteEVA = muted;
			soundEngine.SetCategoryVolume(SoundCategory.EVA, muted ? 0f : EVAVolume * soundVolumeModifier);
			if (muted)
			{
				soundEngine.StopSounds(SoundCategory.EVA);
				ResetSpeechNotifications();
			}
		}

		public void SetUnitVoicesMuted(bool muted)
		{
			Game.Settings.Sound.MuteUnitVoices = muted;
			soundEngine.SetCategoryVolume(SoundCategory.UnitVoice, muted ? 0f : UnitVoiceVolume * soundVolumeModifier);
			if (muted)
			{
				soundEngine.StopSounds(SoundCategory.UnitVoice);
				currentSounds.Clear();
			}
		}

		public float MusicVolume
		{
			get => Game.Settings.Sound.MusicVolume;

			set
			{
				Game.Settings.Sound.MusicVolume = value;
				if (music != null)
					music.Volume = value;
			}
		}

		public float VideoVolume
		{
			get => Game.Settings.Sound.VideoVolume;

			set
			{
				Game.Settings.Sound.VideoVolume = value;
				if (video != null)
					video.Volume = value;
			}
		}

		public float MusicSeekPosition => music?.SeekPosition ?? 0;

		public float VideoSeekPosition => video?.SeekPosition ?? 0;

		// Returns true if played successfully
		public bool PlayPredefined(SoundType soundType, Ruleset ruleset, Player player, Actor voicedActor, string type, string definition, string variant,
			bool relative, WPos pos, float volumeModifier, bool attenuateVolume)
		{
			ArgumentNullException.ThrowIfNull(ruleset);

			if (definition == null || DisableAllSounds || (DisableWorldSounds && soundType == SoundType.World))
				return false;

			if (ruleset.Voices == null || ruleset.Notifications == null)
				return false;

			var category = voicedActor != null ? SoundCategory.UnitVoice :
				type == SpeechNotificationType ? SoundCategory.EVA : SoundCategory.SoundEffect;

			if ((category == SoundCategory.EVA && Game.Settings.Sound.MuteEVA) ||
				(category == SoundCategory.UnitVoice && Game.Settings.Sound.MuteUnitVoices))
				return true;

			var rules = voicedActor != null ? ruleset.Voices[type] : ruleset.Notifications[type];
			if (rules == null)
				return false;

			var id = voicedActor?.ActorID ?? 0;

			SoundPool pool;
			var suffix = rules.DefaultVariant;
			var prefix = rules.DefaultPrefix;

			if (voicedActor != null)
			{
				if (!rules.VoicePools.Value.TryGetValue(definition, out var p))
					throw new InvalidOperationException($"Can't find {definition} in voice pool.");

				pool = p;
			}
			else
			{
				if (!rules.NotificationsPools.Value.TryGetValue(definition, out var p))
					throw new InvalidOperationException($"Can't find {definition} in notification pool.");

				pool = p;
			}

			if (variant != null)
			{
				if (rules.Variants.TryGetValue(variant, out var v) && !rules.DisableVariants.Contains(definition))
					suffix = v[(int)(id % v.Length)];
				if (rules.Prefixes.TryGetValue(variant, out var p) && !rules.DisablePrefixes.Contains(definition))
					prefix = p[(int)(id % p.Length)];
			}

			var clip = pool.GetNext();
			if (string.IsNullOrEmpty(clip))
				return false;

			// Prefer the randomly-rolled clip, but fall back if this voice set doesn't provide it.
			// This lets a notification or voice list extra clips that only some voice sets supply:
			// a set lacking the rolled clip plays the first listed clip it does have (usually the
			// canonical first entry) instead of going silent. Only fully-absent pools stay silent.
			string name = null;
			var candidate = prefix + clip + suffix;
			if (sounds[candidate] != null)
				name = candidate;
			else
			{
				foreach (var fallback in pool.Clips)
				{
					candidate = prefix + fallback + suffix;
					if (sounds[candidate] != null)
					{
						name = candidate;
						break;
					}
				}
			}

			if (name == null)
				return false;
			var actorId = voicedActor != null && voicedActor.World.Selection.Contains(voicedActor) ? 0 : id;
			if (!string.IsNullOrEmpty(name) && (player == null || player == player.World.LocalPlayer))
			{
				ISound PlaySound()
				{
					var volume = volumeModifier * pool.VolumeModifier;
					return soundEngine.Play2D(sounds[name], false, relative, pos, volume, attenuateVolume, category);
				}

				// EVA speech notifications are serialised through a queue so they never talk over
				// each other, with an optional per-notification cooldown. Priority notifications
				// bypass the queue and play immediately so important lines are never delayed or
				// dropped. A dummy backend is excluded because its sounds never report completion,
				// which would otherwise wedge the queue. Effects, unit voices, and the cases above
				// all fall through to the immediate paths below.
				if (voicedActor == null && type == SpeechNotificationType && !pool.Priority && !DummyEngine)
				{
					// Drop a repeat of a line already queued or currently playing (restores the
					// same-name dedup the immediate path below used to provide for speech).
					if (currentSpeechDefinition == definition && currentSpeechNotification != null && !currentSpeechNotification.Complete)
						return true;

					foreach (var queued in speechQueue)
						if (queued.Definition == definition)
							return true;

					var nowMs = Game.RunTime;
					if (pool.Cooldown > 0)
					{
						if (speechNotificationReadyAt.TryGetValue(definition, out var readyAt) && nowMs < readyAt)
							return false;

						speechNotificationReadyAt[definition] = nowMs + pool.Cooldown;
					}

					speechQueueDelay = rules.QueueDelay;
					speechQueueExpiry = rules.QueueExpiry;
					speechQueue.Add(new QueuedSpeechNotification { EnqueuedAt = nowMs, Definition = definition, Play = PlaySound });
					return true;
				}

				if (pool.Type == SoundPool.InterruptType.Overlap)
				{
					if (PlaySound() == null)
						return false;
				}
				else if (voicedActor == null)
				{
					if (currentNotifications.TryGetValue(name, out var currentNotification) && !currentNotification.Complete)
					{
						if (pool.Type == SoundPool.InterruptType.Interrupt)
							soundEngine.StopSound(currentNotification);
						else if (pool.Type == SoundPool.InterruptType.DoNotPlay)
							return false;
					}

					var sound = PlaySound();
					if (sound == null)
						return false;
					else
						currentNotifications[name] = sound;
				}
				else
				{
					if (currentSounds.TryGetValue(actorId, out var currentSound) && !currentSound.Complete)
					{
						if (pool.Type == SoundPool.InterruptType.Interrupt)
							soundEngine.StopSound(currentSound);
						else if (pool.Type == SoundPool.InterruptType.DoNotPlay)
							return false;
					}

					var sound = PlaySound();
					if (sound == null)
						return false;
					else
						currentSounds[actorId] = sound;
				}
			}

			return true;
		}

		public bool PlayNotification(Ruleset rules, Player player, string type, string notification, string variant)
		{
			ArgumentNullException.ThrowIfNull(rules);

			if (type == null || notification == null)
				return false;

			return PlayPredefined(SoundType.UI, rules, player, null, type.ToLowerInvariant(), notification, variant, true, WPos.Zero, 1f, false);
		}

		public void Dispose()
		{
			StopAudio();
			if (sounds != null)
				foreach (var soundSource in sounds.Values)
					soundSource?.Dispose();

			soundEngine.Dispose();
		}
	}
}
