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
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.GameSaves
{
	/// <summary>Reads a snapshot and gives the restore code access to the sections in it.</summary>
	/// <remarks>
	/// A saveable type receives a <see cref="SnapshotReader"/> in its restore constructor. The reader
	/// checks the container before it exposes a section: the magic string, the header, the trailer, and
	/// the section table.
	/// <para>
	/// An actor reference can name an actor that the restore does not create until later.
	/// <see cref="DeferActor"/> and <see cref="DeferTarget"/> hold such a reference.
	/// <see cref="RunDeferred"/> resolves it after <see cref="WorldRestorer"/> creates every actor.
	/// </para>
	/// </remarks>
	public sealed class SnapshotReader : IDisposable
	{
		readonly Stream input;
		readonly bool ownsStream;
		readonly Dictionary<string, SnapshotSection> sections = [];
		readonly List<(uint ActorID, Action<Actor> Resolve)> deferredActorRefs = [];
		readonly List<Action> deferredCompletions = [];

		public SnapshotHeader Header { get; }

		public IEnumerable<string> SectionNames => sections.Keys;

		public World World { get; }

		public SnapshotReader(Stream input, bool ownsStream = false, World world = null)
		{
			ArgumentNullException.ThrowIfNull(input);
			if (!input.CanSeek)
				throw new InvalidDataException("Snapshots can only be read from a seekable stream.");

			this.input = input;
			this.ownsStream = ownsStream;
			World = world;

			input.Seek(0, SeekOrigin.Begin);
			var magic = input.ReadBytes(SnapshotContainer.Magic.Length);
			if (!magic.SequenceEqual(SnapshotContainer.Magic))
				throw new InvalidDataException("Not a snapshot file.");

			Header = new SnapshotHeader(input);
			var headerEnd = input.Position;

			ReadSectionTable(headerEnd);
		}

		void ReadSectionTable(long headerEnd)
		{
			if (input.Length < headerEnd + SnapshotContainer.TrailerLength)
				throw new InvalidDataException("Snapshot is truncated.");

			input.Seek(-SnapshotContainer.TrailerLength, SeekOrigin.End);
			var tableOffset = SnapshotContainer.ReadInt64(input);
			if (input.ReadInt32() != SnapshotContainer.EndMarker)
				throw new InvalidDataException("Corrupt snapshot trailer.");

			var tableLimit = input.Length - SnapshotContainer.TrailerLength;
			if (tableOffset < headerEnd || tableOffset > tableLimit)
				throw new InvalidDataException($"Snapshot section table offset {tableOffset} is out of range.");

			input.Seek(tableOffset, SeekOrigin.Begin);
			if (input.ReadInt32() != SnapshotContainer.SectionTableMarker)
				throw new InvalidDataException("Corrupt snapshot section table.");

			var count = input.ReadInt32();
			if (count < 0 || count > SnapshotContainer.MaxSections)
				throw new InvalidDataException($"Snapshot declares an invalid section count {count}.");

			for (var i = 0; i < count; i++)
			{
				var name = SnapshotContainer.ReadSectionName(input);
				var offset = SnapshotContainer.ReadInt64(input);
				var length = SnapshotContainer.ReadInt64(input);
				var encoding = (SnapshotSectionEncoding)input.ReadUInt8();
				var uncompressedLength = SnapshotContainer.ReadInt64(input);

				if (offset < headerEnd || length < 0 || offset + length > tableOffset)
					throw new InvalidDataException($"Snapshot section '{name}' lies outside the file.");

				if (uncompressedLength < 0)
					throw new InvalidDataException($"Snapshot section '{name}' declares a negative uncompressed length.");

				if (encoding != SnapshotSectionEncoding.Raw && encoding != SnapshotSectionEncoding.Deflate)
					throw new InvalidDataException($"Snapshot section '{name}' uses an unknown encoding {(byte)encoding}.");

				if (!sections.TryAdd(name, new SnapshotSection(name, offset, length, encoding, uncompressedLength)))
					throw new InvalidDataException($"Snapshot contains a duplicate section '{name}'.");
			}
		}

		public bool HasSection(string name)
		{
			return sections.ContainsKey(name);
		}

		public Stream OpenSection(string name)
		{
			if (!sections.TryGetValue(name, out var section))
				return null;

			var raw = SegmentStream.CreateWithoutOwningStream(input, section.Offset, (int)section.Length);
			if (section.Encoding == SnapshotSectionEncoding.Raw)
				return raw;

			return new ZLibStream(raw, CompressionMode.Decompress);
		}

		public List<MiniYamlNode> ReadYamlSection(string name)
		{
			using (var s = OpenSection(name))
			{
				if (s == null)
					return null;

				return MiniYaml.FromString(s.ReadAllText(), name).ToList();
			}
		}

		#region Reference resolution

		readonly Dictionary<uint, Actor> restoredActors = [];

		public void RecordRestoredActor(uint actorID, Actor actor)
		{
			restoredActors[actorID] = actor;
		}

		public Actor GetActorById(uint actorID)
		{
			if (restoredActors.TryGetValue(actorID, out var restored))
				return restored;

			return World?.GetActorById(actorID);
		}

		public Actor ResolveActor(string value)
		{
			if (!SnapshotRefs.TryParseActorID(value, out var actorID))
				return null;

			return GetActorById(actorID);
		}

		public Player ResolvePlayer(string value)
		{
			return SnapshotRefs.ParsePlayer(World, value);
		}

		public Target ResolveTarget(string value)
		{
			return SnapshotRefs.ParseTarget(World, value, GetActorById);
		}

		public void DeferActor(string value, Action<Actor> resolve)
		{
			ArgumentNullException.ThrowIfNull(resolve);

			if (!SnapshotRefs.TryParseActorID(value, out var actorID))
				return;

			deferredActorRefs.Add((actorID, resolve));
		}

		public void DeferTarget(string value, Action<Target> resolve)
		{
			ArgumentNullException.ThrowIfNull(resolve);

			if (SnapshotRefs.IsActorTarget(value))
				// Only an actor target must wait. The other forms resolve from state
				// that the reader already has.
				deferredCompletions.Add(() => resolve(ResolveTarget(value)));
			else
				resolve(ResolveTarget(value));
		}

		public void DeferCompleted(Action complete)
		{
			ArgumentNullException.ThrowIfNull(complete);

			deferredCompletions.Add(complete);
		}

		public void RunDeferred()
		{
			foreach (var (actorID, resolve) in deferredActorRefs)
				resolve(GetActorById(actorID));

			deferredActorRefs.Clear();

			foreach (var complete in deferredCompletions)
				complete();

			deferredCompletions.Clear();
		}

		#endregion

		public void Dispose()
		{
			if (ownsStream)
				input.Dispose();
		}
	}
}
