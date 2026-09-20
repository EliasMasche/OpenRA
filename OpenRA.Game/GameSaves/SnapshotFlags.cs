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

namespace OpenRA.GameSaves
{
	/// <summary>Properties of a snapshot that a reader can know before it reads the sections.</summary>
	[Flags]
	public enum SnapshotFlags
	{
		None = 0,

		Autosave = 1,

		HasScriptState = 2,
	}
}
