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

using OpenRA.Traits;

namespace OpenRA.Hosting
{
	/// <summary>
	/// The presentation steps of starting a game, in the order <see cref="Game"/> calls them.
	/// </summary>
	public interface IPresentation
	{
		void DetachWorld();

		void PrepareMap(Map map);

		/// <summary>Creates the presentation of a constructed world, before its traits load.</summary>
		IWorldPresentation AttachWorld(World world);

		void BeforeStart();

		void AfterStart();
	}

	/// <summary>
	/// The presentation of a host without a display: every step does nothing, and no trait receives a
	/// renderer.
	/// </summary>
	public sealed class NoDisplayPresentation : IPresentation, IWorldPresentation
	{
		public void DetachWorld() { }

		public void PrepareMap(Map map) { }

		public IWorldPresentation AttachWorld(World world) { return this; }

		public void Attach(World world, IWorldRendererLoaded trait) { }

		public void BeforeStart() { }

		public void AfterStart() { }
	}
}
