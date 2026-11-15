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

namespace OpenRA
{
	/// <summary>
	/// Marks simulation state that a save leaves out on purpose. The reason says where it comes back from after a load.
	/// On a type, it covers every member of the type.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class)]
	public sealed class NotSavedAttribute(string reason) : Attribute
	{
		public readonly string Reason = reason;
	}

	/// <summary>
	/// Marks state that the cosmetic random or the clock of the system decides, such as the variant of a crater.
	/// A save holds it, so a loaded world draws what the saved world drew. The simulation never reads it, so a
	/// comparison of two worlds leaves it out. On a type, it covers every member of the type.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class)]
	public sealed class CosmeticStateAttribute(string reason) : Attribute
	{
		public readonly string Reason = reason;
	}

	/// <summary>
	/// Marks an array of traits that a save has to hold. A save leaves such an array out by default, because
	/// creation fills it again from the actor's own traits. Other actors fill a marked array, and nothing fills it again.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class SavedTraitsAttribute(string reason) : Attribute
	{
		public readonly string Reason = reason;
	}
}
