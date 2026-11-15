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
using System.Linq;
using System.Security.Cryptography;
using OpenRA.Snapshot;

namespace OpenRA.Network
{
	/// <summary>
	/// The file of the admin, given to the other people in chunks that each fit one order. It checks each chunk, as they
	/// come from another machine.
	/// </summary>
	public sealed class SharedSave(long limit)
	{
		public const string UploadRequestOrder = "GameSaveUploadRequest";

		public const string UploadOrder = "GameSaveUpload";

		public const string RequestOrder = "GameSaveRequest";

		public const string ChunkOrder = "GameSaveChunk";

		public const int MaxSize = 16 << 20;

		public const int ChunkSize = 48 << 10;

		/// <summary>How many files from other machines a client keeps.</summary>
		public const int KeptFiles = 3;

		// A file opens to about 23 times its size (measured). The factor 64 keeps a margin above that.
		const int OpenedSizeFactor = 64;
		const long MaxOpenedSize = 512 << 20;

		const string Offset = "Offset";
		const string Size = "Size";
		const string Data = "Data";

		const int MaxDataLength = (ChunkSize + 2) / 3 * 4;

		byte[] file;
		int received;

		public int Length => file?.Length ?? 0;

		public bool IsComplete => file != null && received == file.Length;

		public byte[] Bytes => IsComplete ? file : throw new InvalidOperationException("The file is not whole.");

		public static string Hash(byte[] file)
		{
			return Convert.ToHexStringLower(SHA256.HashData(file));
		}

		public static string FileName(string hash)
		{
			return $"shared-{hash[..16]}.orasav";
		}

		/// <summary>Deletes the shared files in <paramref name="folder"/> except <paramref name="kept"/> and the newest others.</summary>
		public static void DeleteOlderFiles(string folder, string kept, int keep = KeptFiles)
		{
			var older = new DirectoryInfo(folder).EnumerateFiles("shared-*.orasav")
				.Where(f => IsFileName(f.Name) && f.Name != kept)
				.OrderByDescending(f => f.LastWriteTimeUtc)
				.ThenByDescending(f => f.Name, StringComparer.Ordinal)
				.Skip(keep - 1);

			foreach (var file in older)
			{
				try
				{
					file.Delete();
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					Log.Write("client", $"The older shared save {file.FullName} was not deleted: {e.Message}");
				}
			}
		}

		static bool IsFileName(string name)
		{
			const string Prefix = "shared-";
			const string Extension = ".orasav";
			return name.Length == Prefix.Length + 16 + Extension.Length && name.StartsWith(Prefix, StringComparison.Ordinal)
				&& name.EndsWith(Extension, StringComparison.Ordinal)
				&& name.AsSpan(Prefix.Length, 16).ToString().All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
		}

		/// <summary>Whether <paramref name="hash"/> is the SHA-256 of a file as lowercase hex.</summary>
		public static bool IsHash(string hash)
		{
			return hash != null && hash.Length == 64 && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
		}

		public static IEnumerable<string> Split(byte[] file)
		{
			for (var offset = 0; offset < file.Length; offset += ChunkSize)
			{
				yield return new List<MiniYamlNode>
				{
					new(Offset, FieldSaver.FormatValue(offset)),
					new(Size, FieldSaver.FormatValue(file.Length)),
					new(Data, Convert.ToBase64String(file, offset, Math.Min(ChunkSize, file.Length - offset))),
				}.WriteToString();
			}
		}

		/// <summary>Checks the hash, the lobby and the opened size of <paramref name="file"/>, and gives its snapshot.</summary>
		public static SnapshotFile Check(byte[] file, string hash, Session.Global settings)
		{
			if (Hash(file) != hash)
				throw new InvalidDataException("the file does not have the hash that the lobby names.");

			var snapshot = GameSaveCopy.CheckedSnapshot(settings, GameSave.FromBytes(file, "the file of the server"));
			snapshot.CheckOpenedSize(Math.Min((long)OpenedSizeFactor * file.Length, MaxOpenedSize));

			return snapshot;
		}

		/// <summary>Adds a chunk, or refuses it with <paramref name="refusal"/>. After a refusal, this instance is of no use.</summary>
		public bool TryAdd(string chunk, out string refusal)
		{
			refusal = null;
			if (chunk == null)
			{
				refusal = "the chunk has no text.";
				return false;
			}

			int offset;
			int size;
			byte[] data;
			try
			{
				var nodes = MiniYaml.FromString(chunk, ChunkOrder);
				var keys = nodes.Select(n => n.Key).ToArray();
				if (keys.Length != 3 || keys.Distinct().Count() != 3)
				{
					refusal = "the chunk does not have the keys Offset, Size and Data.";
					return false;
				}

				string Value(string key) => nodes.FirstOrDefault(n => n.Key == key)?.Value.Value;
				if (!Exts.TryParseInt32Invariant(Value(Offset), out offset) || !Exts.TryParseInt32Invariant(Value(Size), out size))
				{
					refusal = "the chunk has an offset or a size that is not a number.";
					return false;
				}

				var text = Value(Data);
				data = new byte[ChunkSize];
				if (text == null || text.Length > MaxDataLength || !Convert.TryFromBase64String(text, data, out var written) || written == 0)
				{
					refusal = "the chunk has data that is empty, too long or not Base64.";
					return false;
				}

				Array.Resize(ref data, written);
			}
			catch (YamlException e)
			{
				refusal = $"the chunk is not well formed: {e.Message}";
				return false;
			}

			if (size <= 0 || size > limit)
			{
				refusal = $"the file has {size} bytes, and the limit is {limit}.";
				return false;
			}

			if (file != null && size != file.Length)
			{
				refusal = $"the chunk is of a file of {size} bytes, and the chunks before it are of a file of {file.Length}.";
				return false;
			}

			if (offset != received)
			{
				refusal = $"the chunk starts at byte {offset}, and the chunks before it end at byte {received}.";
				return false;
			}

			if (data.Length > size - offset)
			{
				refusal = $"the chunk holds bytes past the end of the file of {size} bytes.";
				return false;
			}

			file ??= new byte[size];
			data.CopyTo(file, offset);
			received += data.Length;
			return true;
		}
	}
}
