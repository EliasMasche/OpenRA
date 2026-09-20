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
using System.IO;
using System.Text;
using System.Threading;

namespace OpenRA.GameSaves
{
	/// <summary>Locates save files, names them, and limits what a name can contain.</summary>
	/// <remarks>
	/// A save name can arrive from a client order, so the name is not trusted.
	/// <see cref="SanitizeFileName"/> removes the characters that the file system rejects, then limits
	/// the name to <see cref="MaxNameBytes"/> bytes of UTF-8.
	/// <para>
	/// A save goes to a scratch file first and then moves into place, because the target file can be open
	/// in another process. The scratch name carries the process id, so two processes that write the same
	/// save do not share a scratch file.
	/// </para>
	/// </remarks>
	public static class SavePaths
	{
		public const string Extension = ".orasav";

		public static string BaseSaveDirectory(Manifest mod)
		{
			return Path.Combine(Platform.SupportDir, "Saves", mod.Id, mod.Metadata.Version);
		}

		public const int MaxNameBytes = 200;

		public static string SanitizeFileName(string filename)
		{
			ArgumentNullException.ThrowIfNull(filename);

			var invalidChars = Path.GetInvalidFileNameChars();
			int invalidIndex;
			while ((invalidIndex = filename.IndexOfAny(invalidChars)) != -1)
				filename = filename.Remove(invalidIndex, 1);

			if (filename.Length == 0 || filename.TrimEnd('.').Length == 0)
				throw new ArgumentException($"'{filename}' does not name a save file.", nameof(filename));

			return TruncateToBytes(filename, MaxNameBytes);
		}

		static string TruncateToBytes(string filename, int maxBytes)
		{
			if (Encoding.UTF8.GetByteCount(filename) <= maxBytes)
				return filename;

			var used = 0;
			var kept = 0;
			while (kept < filename.Length)
			{
				var size = Encoding.UTF8.GetByteCount(filename.AsSpan(kept, 1));
				if (used + size > maxBytes)
					break;

				used += size;
				kept++;
			}

			if (kept > 0 && char.IsHighSurrogate(filename[kept - 1]))
				kept--;

			return filename[..kept].TrimEnd('.');
		}

		public static bool TrySanitizeFileName(string filename, out string sanitized)
		{
			try
			{
				sanitized = SanitizeFileName(filename);
				return true;
			}
			catch (ArgumentException)
			{
				sanitized = null;
				return false;
			}
		}

		public static string ResolveSaveFile(Manifest mod, string filename)
		{
			return Path.Combine(BaseSaveDirectory(mod), SanitizeFileName(filename));
		}

		public const int UniqueNameAttempts = 128;

		public static string UniqueFilePath(string directory, string filename)
		{
			ArgumentNullException.ThrowIfNull(directory);
			ArgumentNullException.ThrowIfNull(filename);

			var path = Path.Combine(directory, filename);
			if (!File.Exists(path))
				return path;

			var name = Path.GetFileNameWithoutExtension(filename);
			var extension = Path.GetExtension(filename);

			for (var attempt = 1; attempt <= UniqueNameAttempts; attempt++)
			{
				path = Path.Combine(directory, $"{name} ({attempt}){extension}");
				if (!File.Exists(path))
					return path;
			}

			throw new IOException($"Could not find a free name for '{filename}' after {UniqueNameAttempts} attempts.");
		}

		public const string WriteSuffix = ".writing";

		public static string ScratchPath(string path)
		{
			return $"{path}.{Environment.ProcessId}{WriteSuffix}";
		}

		public const int ReplaceFileAttempts = 100;

		public static void ReplaceFile(string path, byte[] payload)
		{
			ArgumentNullException.ThrowIfNull(path);
			ArgumentNullException.ThrowIfNull(payload);

			var temporaryPath = ScratchPath(path);
			File.WriteAllBytes(temporaryPath, payload);

			// Another process can hold the target file, so the move retries before it
			// gives up.
			for (var attempt = 0; ; attempt++)
			{
				try
				{
					File.Move(temporaryPath, path, true);
					return;
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException
					&& attempt < ReplaceFileAttempts)
				{
					Thread.Sleep(10);
				}
			}
		}

		public static void DiscardFailedWrite(string path)
		{
			ArgumentNullException.ThrowIfNull(path);

			try
			{
				File.Delete(ScratchPath(path));
			}
			catch (IOException) { }
		}
	}
}
