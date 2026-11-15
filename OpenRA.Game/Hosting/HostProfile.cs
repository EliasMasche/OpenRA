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

#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;

namespace OpenRA.Hosting
{
	public enum PresentationPortion { None, Window }

	public enum AudioPortion { Silent, Device }

	public enum ClockPortion { Realtime, Stepped }

	public enum RunnerPortion { Client, Tooling, Relay }

	/// <summary>
	/// States which implementation of each engine portion a process runs.
	/// Every portion resolves to a concrete service: a process without a display
	/// has <see cref="PresentationPortion.None"/>, never a null renderer.
	/// </summary>
	public readonly record struct HostProfile(
		string Name,
		PresentationPortion Presentation,
		AudioPortion Audio,
		ClockPortion Clock,
		RunnerPortion Runner)
	{
		public static readonly HostProfile Interactive = new(
			"Interactive", PresentationPortion.Window, AudioPortion.Device, ClockPortion.Realtime, RunnerPortion.Client);

		/// <summary>
		/// OpenRA.Utility: no display, and time moves only when a command advances it, as the replay sync
		/// check does.
		/// </summary>
		public static readonly HostProfile Tooling = new(
			"Tooling", PresentationPortion.None, AudioPortion.Silent, ClockPortion.Stepped, RunnerPortion.Tooling);

		/// <summary>OpenRA.Server: relays orders between clients and runs no world. Pings need real time.</summary>
		public static readonly HostProfile Relay = Tooling with { Name = "Relay", Clock = ClockPortion.Realtime, Runner = RunnerPortion.Relay };

		public static readonly ImmutableArray<HostProfile> Available = [Interactive, Tooling, Relay];

		public bool HasDisplay => Presentation != PresentationPortion.None;

		public void Validate()
		{
			if (string.IsNullOrEmpty(Name))
				throw new InvalidOperationException("A host profile must have a name.");

			if (Runner == RunnerPortion.Client && Presentation != PresentationPortion.Window)
				throw new HostPortionException(this, "the client runner needs a window.");

			if (Runner == RunnerPortion.Client && Clock != ClockPortion.Realtime)
				throw new HostPortionException(this, "the client runner needs a realtime clock.");
		}

		public static HostProfile FromName(string name)
		{
			foreach (var profile in Available)
				if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
					return profile;

			var valid = string.Join(", ", Available.Select(p => p.Name));
			throw new ArgumentException($"Unknown host profile '{name}'. Available profiles: {valid}.", nameof(name));
		}
	}
}
