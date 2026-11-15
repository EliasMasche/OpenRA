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

using OpenRA.Traits;

namespace OpenRA.Scripting
{
	/// <summary>
	/// The info of the world trait that runs the scripts of its map. A snapshot does not hold the state of a script. So
	/// a save of such a world holds every order of its game, and only the server of such a map keeps the orders.
	/// </summary>
	public interface IWorldScriptInfo : ITraitInfoInterface { }
}
