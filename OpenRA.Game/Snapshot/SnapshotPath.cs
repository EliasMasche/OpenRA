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

using System.Globalization;
using System.Text;

namespace OpenRA.Snapshot
{
	/// <summary>
	/// Where a value is in a snapshot, for a message. A save and a load make one for each field and element, and almost
	/// none ends in a message. So this keeps the parts, and joins the text only for a message.
	/// </summary>
	public sealed class SnapshotPath
	{
		readonly SnapshotPath parent;
		readonly string owner;
		readonly string name;
		readonly int index;
		readonly bool isElement;

		SnapshotPath(SnapshotPath parent, string owner, string name, int index, bool isElement)
		{
			this.parent = parent;
			this.owner = owner;
			this.name = name;
			this.index = index;
			this.isElement = isElement;
		}

		public static implicit operator SnapshotPath(string text) => new(null, null, text, 0, false);

		/// <summary>A field of the object at this path, as "path: Owner.Name", or "path: Name" without an owner.</summary>
		public SnapshotPath Field(string fieldName, string fieldOwner = null) => new(this, fieldOwner, fieldName, 0, false);

		public SnapshotPath Element(string key) => new(this, null, key, 0, true);

		public SnapshotPath Element(int position) => new(this, null, null, position, true);

		public override string ToString()
		{
			var text = new StringBuilder();
			Append(text);
			return text.ToString();
		}

		void Append(StringBuilder text)
		{
			if (parent == null)
			{
				text.Append(name);
				return;
			}

			parent.Append(text);
			if (isElement)
				text.Append('[').Append(name ?? index.ToString(CultureInfo.InvariantCulture)).Append(']');
			else
				text.Append(": ").Append(owner != null ? owner + "." : "").Append(name);
		}
	}
}
