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
using System.Linq;
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class BlobTransferTest
	{
		static byte[] Payload(int length, int seed = 12345)
		{
			var random = new Random(seed);
			var payload = new byte[length];
			random.NextBytes(payload);
			return payload;
		}

		static byte[] RoundTrip(byte[] payload)
		{
			var parts = BlobTransfer.Split(payload);
			var reassembler = new BlobReassembler();

			for (var i = 0; i < parts.Chunks.Count; i++)
				Assert.That(reassembler.TryAdd(i, parts.Chunks[i], out _), Is.True);

			Assert.That(
				reassembler.TryComplete(parts.Chunks.Count, parts.EncodedLength, parts.Hash, out var actual, out var error),
				Is.True,
				$"Reassembly failed with {error}");

			return actual;
		}

		[TestCase(0, TestName = "Empty payload round trips")]
		[TestCase(1, TestName = "Single byte payload round trips")]
		[TestCase(1024, TestName = "Small payload round trips")]
		[TestCase(512 * 1024, TestName = "Payload spanning several chunks round trips")]
		public void RoundTrips(int length)
		{
			var expected = Payload(length);

			Assert.That(RoundTrip(expected), Is.EqualTo(expected));
		}

		[TestCase(TestName = "A payload larger than one chunk is actually split")]
		public void SplitsLargePayloads()
		{
			var parts = BlobTransfer.Split(Payload(512 * 1024));

			Assert.That(parts.Chunks.Count, Is.GreaterThan(1));
			Assert.That(parts.Chunks.All(c => c.Length <= BlobTransfer.ChunkLength), Is.True);
		}

		[TestCase(TestName = "An empty payload still produces one chunk")]
		public void EmptyPayloadProducesOneChunk()
		{
			Assert.That(BlobTransfer.Split([]).Chunks.Count, Is.EqualTo(1));
		}

		[TestCase(TestName = "Chunks arriving out of order round trip")]
		public void AcceptsChunksOutOfOrder()
		{
			var expected = Payload(256 * 1024);
			var parts = BlobTransfer.Split(expected);
			Assume.That(parts.Chunks.Count, Is.GreaterThan(1));

			var reassembler = new BlobReassembler();
			foreach (var i in Enumerable.Range(0, parts.Chunks.Count).Reverse())
				Assert.That(reassembler.TryAdd(i, parts.Chunks[i], out _), Is.True);

			Assert.That(
				reassembler.TryComplete(parts.Chunks.Count, parts.EncodedLength, parts.Hash, out var actual, out _),
				Is.True);
			Assert.That(actual, Is.EqualTo(expected));
		}

		static BlobReassembler ReceiveChunk(BlobReassembler current, int index, string chunk, out BlobTransferError error)
		{
			if (index == 0 || current == null)
				current = new BlobReassembler();

			return current.TryAdd(index, chunk, out error) ? current : null;
		}

		[TestCase(TestName = "A transfer after an abandoned one succeeds")]
		public void AbandonedTransferDoesNotPoisonTheNext()
		{
			var expected = Payload(256 * 1024);
			var parts = BlobTransfer.Split(expected);
			Assume.That(parts.Chunks.Count, Is.GreaterThan(1));

			var receiver = ReceiveChunk(null, 0, parts.Chunks[0], out _);

			for (var i = 0; i < parts.Chunks.Count; i++)
			{
				receiver = ReceiveChunk(receiver, i, parts.Chunks[i], out var error);
				Assert.That(receiver, Is.Not.Null, $"Chunk {i} of the second transfer was refused with {error}.");
			}

			Assert.That(
				receiver.TryComplete(parts.Chunks.Count, parts.EncodedLength, parts.Hash, out var actual, out var completeError),
				Is.True,
				$"Reassembly after an abandoned transfer failed with {completeError}.");
			Assert.That(actual, Is.EqualTo(expected));
		}

		[TestCase(TestName = "Reusing a partial reassembler rejects the next transfer")]
		public void ReusingAPartialReassemblerFails()
		{
			var parts = BlobTransfer.Split(Payload(256 * 1024));
			Assume.That(parts.Chunks.Count, Is.GreaterThan(1));

			var reused = new BlobReassembler();
			reused.TryAdd(0, parts.Chunks[0], out _);

			Assert.That(reused.TryAdd(0, parts.Chunks[0], out var error), Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.DuplicateChunk));
		}

		[TestCase(TestName = "A duplicate chunk index is refused")]
		public void RefusesDuplicateChunk()
		{
			var parts = BlobTransfer.Split(Payload(1024));
			var reassembler = new BlobReassembler();

			Assert.That(reassembler.TryAdd(0, parts.Chunks[0], out _), Is.True);
			Assert.That(reassembler.TryAdd(0, parts.Chunks[0], out var error), Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.DuplicateChunk));
		}

		[TestCase(TestName = "A missing chunk is refused")]
		public void RefusesMissingChunk()
		{
			var parts = BlobTransfer.Split(Payload(256 * 1024));
			Assume.That(parts.Chunks.Count, Is.GreaterThan(1));

			var reassembler = new BlobReassembler();
			for (var i = 0; i < parts.Chunks.Count - 1; i++)
				reassembler.TryAdd(i, parts.Chunks[i], out _);

			Assert.That(
				reassembler.TryComplete(parts.Chunks.Count, parts.EncodedLength, parts.Hash, out _, out var error),
				Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.MissingChunk));
		}

		[TestCase(TestName = "A hole in the indices is refused even when the count matches")]
		public void RefusesHoleWithMatchingCount()
		{
			var parts = BlobTransfer.Split(Payload(256 * 1024));
			Assume.That(parts.Chunks.Count, Is.GreaterThan(1));

			var reassembler = new BlobReassembler();
			for (var i = 1; i < parts.Chunks.Count; i++)
				reassembler.TryAdd(i, parts.Chunks[i], out _);

			reassembler.TryAdd(parts.Chunks.Count, "", out _);

			Assert.That(
				reassembler.TryComplete(parts.Chunks.Count, parts.EncodedLength, parts.Hash, out _, out var error),
				Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.MissingChunk));
		}

		[TestCase(TestName = "A declared length that disagrees is refused")]
		public void RefusesLengthMismatch()
		{
			var parts = BlobTransfer.Split(Payload(1024));
			var reassembler = new BlobReassembler();
			for (var i = 0; i < parts.Chunks.Count; i++)
				reassembler.TryAdd(i, parts.Chunks[i], out _);

			Assert.That(
				reassembler.TryComplete(parts.Chunks.Count, parts.EncodedLength + 1, parts.Hash, out _, out var error),
				Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.LengthMismatch));
		}

		[TestCase(TestName = "A corrupted chunk is refused by the hash")]
		public void RefusesCorruptedPayload()
		{
			var parts = BlobTransfer.Split(Payload(1024));
			var corrupted = parts.Chunks.ToArray();

			var chunk = corrupted[0].ToCharArray();
			chunk[0] = chunk[0] == 'A' ? 'B' : 'A';
			corrupted[0] = new string(chunk);

			var reassembler = new BlobReassembler();
			for (var i = 0; i < corrupted.Length; i++)
				reassembler.TryAdd(i, corrupted[i], out _);

			Assert.That(
				reassembler.TryComplete(corrupted.Length, parts.EncodedLength, parts.Hash, out _, out var error),
				Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.HashMismatch));
		}

		[TestCase(TestName = "A hash that disagrees is refused")]
		public void RefusesHashMismatch()
		{
			var parts = BlobTransfer.Split(Payload(1024));
			var reassembler = new BlobReassembler();
			for (var i = 0; i < parts.Chunks.Count; i++)
				reassembler.TryAdd(i, parts.Chunks[i], out _);

			Assert.That(
				reassembler.TryComplete(parts.Chunks.Count, parts.EncodedLength, "not-the-hash", out _, out var error),
				Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.HashMismatch));
		}

		[TestCase(TestName = "More chunks than the ceiling are refused")]
		public void RefusesTooManyChunks()
		{
			var reassembler = new BlobReassembler();
			for (var i = 0; i < BlobTransfer.MaxChunks; i++)
				Assert.That(reassembler.TryAdd(i, "", out _), Is.True);

			Assert.That(reassembler.TryAdd(BlobTransfer.MaxChunks, "", out var error), Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.TooManyChunks));
		}

		[TestCase(TestName = "A negative chunk index is refused")]
		public void RefusesNegativeIndex()
		{
			var reassembler = new BlobReassembler();

			Assert.That(reassembler.TryAdd(-1, "", out var error), Is.False);
			Assert.That(error, Is.EqualTo(BlobTransferError.TooManyChunks));
		}

		[TestCase(TestName = "Chunks summing past the size ceiling are refused")]
		public void RefusesOversizedTotal()
		{
			var chunk = new string('A', BlobTransfer.MaxEncodedLength / 8);
			var reassembler = new BlobReassembler();

			var error = BlobTransferError.None;
			var refused = false;
			for (var i = 0; i < BlobTransfer.MaxChunks && !refused; i++)
				refused = !reassembler.TryAdd(i, chunk, out error);

			Assert.That(refused, Is.True);
			Assert.That(error, Is.EqualTo(BlobTransferError.TooLarge));
		}
	}
}
