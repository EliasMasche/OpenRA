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

namespace OpenRA.Network
{
	public static class BlobTransfer
	{
		public const int ChunkLength = 48 * 1024;

		public const int MaxChunks = 512;

		public const int MaxEncodedLength = 32 * 1024 * 1024;

		public static BlobParts Split(byte[] payload)
		{
			ArgumentNullException.ThrowIfNull(payload);

			var compressed = Compress(payload);
			var encoded = Convert.ToBase64String(compressed);

			var chunks = new List<string>();
			for (var i = 0; i < encoded.Length; i += ChunkLength)
				chunks.Add(encoded.Substring(i, Math.Min(ChunkLength, encoded.Length - i)));

			if (chunks.Count == 0)
				chunks.Add("");

			return new BlobParts(chunks, encoded.Length, CryptoUtil.SHA1Hash(compressed));
		}

		internal static byte[] Compress(byte[] payload)
		{
			using (var output = new MemoryStream())
			{
				using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, true))
					deflate.Write(payload, 0, payload.Length);

				return output.ToArray();
			}
		}

		internal static byte[] Decompress(byte[] compressed)
		{
			using (var input = new MemoryStream(compressed))
			using (var deflate = new ZLibStream(input, CompressionMode.Decompress))
			using (var output = new MemoryStream())
			{
				deflate.CopyTo(output);
				return output.ToArray();
			}
		}
	}

	public sealed class BlobParts
	{
		public readonly IReadOnlyList<string> Chunks;

		public readonly int EncodedLength;

		public readonly string Hash;

		public BlobParts(IReadOnlyList<string> chunks, int encodedLength, string hash)
		{
			Chunks = chunks;
			EncodedLength = encodedLength;
			Hash = hash;
		}
	}

	public enum BlobTransferError
	{
		None,
		TooManyChunks,
		TooLarge,
		DuplicateChunk,
		MissingChunk,
		LengthMismatch,
		HashMismatch,
		CorruptPayload
	}

	public sealed class BlobReassembler
	{
		readonly Dictionary<int, string> chunks = [];
		int totalLength;

		public int ChunkCount => chunks.Count;

		public bool TryAdd(int index, string chunk, out BlobTransferError error)
		{
			ArgumentNullException.ThrowIfNull(chunk);

			if (index < 0 || chunks.Count >= BlobTransfer.MaxChunks)
			{
				error = BlobTransferError.TooManyChunks;
				return false;
			}

			if (!chunks.TryAdd(index, chunk))
			{
				error = BlobTransferError.DuplicateChunk;
				return false;
			}

			totalLength += chunk.Length;
			if (totalLength > BlobTransfer.MaxEncodedLength)
			{
				error = BlobTransferError.TooLarge;
				return false;
			}

			error = BlobTransferError.None;
			return true;
		}

		public bool TryComplete(int count, int encodedLength, string hash, out byte[] payload, out BlobTransferError error)
		{
			payload = null;

			if (count < 0 || count > BlobTransfer.MaxChunks)
			{
				error = BlobTransferError.TooManyChunks;
				return false;
			}

			if (chunks.Count != count)
			{
				error = BlobTransferError.MissingChunk;
				return false;
			}

			for (var i = 0; i < count; i++)
			{
				if (!chunks.ContainsKey(i))
				{
					error = BlobTransferError.MissingChunk;
					return false;
				}
			}

			var encoded = string.Concat(Enumerable.Range(0, count).Select(i => chunks[i]));
			if (encoded.Length != encodedLength)
			{
				error = BlobTransferError.LengthMismatch;
				return false;
			}

			byte[] compressed;
			try
			{
				compressed = Convert.FromBase64String(encoded);
			}
			catch (FormatException)
			{
				error = BlobTransferError.CorruptPayload;
				return false;
			}

			if (!string.Equals(CryptoUtil.SHA1Hash(compressed), hash, StringComparison.Ordinal))
			{
				error = BlobTransferError.HashMismatch;
				return false;
			}

			try
			{
				payload = BlobTransfer.Decompress(compressed);
			}
			catch (InvalidDataException)
			{
				error = BlobTransferError.CorruptPayload;
				return false;
			}

			error = BlobTransferError.None;
			return true;
		}
	}
}
