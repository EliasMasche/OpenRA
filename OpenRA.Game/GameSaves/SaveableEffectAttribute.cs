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
	/// <summary>Marks an effect that the save code can write and the restore code can rebuild.</summary>
	/// <remarks>
	/// The effect needs a restore constructor that takes a <see cref="World"/>, a
	/// <see cref="SnapshotReader"/>, and a <see cref="MiniYaml"/>. <see cref="EffectRegistry"/> refuses
	/// a marked type that has no such constructor.
	/// </remarks>
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public sealed class SaveableEffectAttribute : Attribute { }
}
