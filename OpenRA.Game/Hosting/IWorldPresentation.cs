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
	/// Decides whether a loading world's traits receive a renderer. The host's presentation implements it
	/// and returns itself from <see cref="IPresentation.AttachWorld"/>.
	/// </summary>
	public interface IWorldPresentation
	{
		void Attach(World world, IWorldRendererLoaded trait);
	}
}
