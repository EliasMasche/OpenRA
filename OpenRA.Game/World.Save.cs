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

namespace OpenRA
{
	public sealed partial class World
	{
		/// <summary>
		/// Whether a snapshot is being written into this world. The load adds each actor to the world again, which is no
		/// event of the game, so what a script waits for does not run then.
		/// </summary>
		public bool IsLoadingSnapshot { get; internal set; }
	}
}
