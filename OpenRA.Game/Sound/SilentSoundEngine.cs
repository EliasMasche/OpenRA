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

using System.IO;

namespace OpenRA
{
	/// <summary>
	/// The sound engine of a host profile with silent audio. It accepts every call and plays nothing.
	/// </summary>
	public sealed class SilentSoundEngine : ISoundEngine
	{
		public bool Dummy => true;

		public SoundDevice[] AvailableDevices() { return [new SoundDevice(null, "No Sound Output")]; }

		public ISoundSource AddSoundSourceFromMemory(byte[] data, int channels, int sampleBits, int sampleRate) { return new SilentSoundSource(); }

		public ISound Play2D(ISoundSource sound, bool loop, bool relative, WPos pos, float volume, bool attenuateVolume) { return new SilentSound(); }

		public ISound Play2DStream(Stream stream, int channels, int sampleBits, int sampleRate, bool loop, bool relative, WPos pos, float volume)
		{
			return new SilentSound();
		}

		public float Volume
		{
			get => 0;
			set { }
		}

		public void PauseSound(ISound sound, bool paused) { }
		public void SetAllSoundsPaused(bool paused) { }
		public void SetSoundVolume(float volume, ISound music, ISound video) { }
		public void StopSound(ISound sound) { }
		public void StopAllSounds() { }
		public void SetListenerPosition(WPos position) { }
		public void SetSoundLooping(bool looping, ISound sound) { }
		public void SetSoundPosition(ISound sound, WPos position) { }
		public void Dispose() { }

		sealed class SilentSoundSource : ISoundSource
		{
			public void Dispose() { }
		}

		sealed class SilentSound : ISound
		{
			public float Volume { get; set; }
			public float SeekPosition => 0;
			public bool Complete => false;

			public void SetPosition(WPos pos) { }
		}
	}
}
