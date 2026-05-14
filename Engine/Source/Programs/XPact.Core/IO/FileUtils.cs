// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slimmed-down subset of EpicGames.Core.FileUtils — only the helpers actually
// consumed by FileReference / DirectoryReference are ported in Task 0.1.
// (GetEncoding overloads, FindCorrectCase). The full FileUtils contains
// Win32 P/Invoke for ForceDelete/lock-info/etc. which we'll port later as
// needed. Public API surface for the included methods matches Unreal 5.9.

using System;
using System.IO;
using System.Text;

namespace XPact.Core.IO
{
	/// <summary>
	/// Exception used to represent caught file/directory exceptions.
	/// </summary>
	/// <param name="inner">Inner exception</param>
	/// <param name="message">Message to display</param>
	public class WrappedFileOrDirectoryException(Exception inner, string message) : Exception(message, inner)
	{
		/// <inheritdoc/>
		public override string ToString() => Message;
	}

	/// <summary>
	/// Utility functions for manipulating files
	/// </summary>
	public static class FileUtils
	{
		/// <summary>
		/// Finds the on-disk case of a a file
		/// </summary>
		/// <param name="info">FileInfo instance describing the file</param>
		/// <returns>New FileInfo instance that represents the file with the correct case</returns>
		public static FileInfo FindCorrectCase(FileInfo info)
		{
			DirectoryInfo parentInfo = DirectoryUtils.FindCorrectCase(info.Directory!);
			if (info.Exists)
			{
				foreach (FileInfo childInfo in parentInfo.EnumerateFiles())
				{
					if (String.Equals(childInfo.Name, info.Name, FileSystemReference.Comparison))
					{
						return childInfo;
					}
				}
			}
			return new FileInfo(Path.Combine(parentInfo.FullName, info.Name));
		}

		/// <summary>
		/// Get the encoding of a span and number of bytes to skip at the start of the buffer
		/// </summary>
		/// <param name="bytes">Bytes to scan for a BOM</param>
		/// <param name="skipBytes">Number of bytes to skip</param>
		/// <returns>The encoding</returns>
		public static Encoding GetEncoding(ReadOnlySpan<byte> bytes, out int skipBytes)
		{
			// https://simple.wikipedia.org/wiki/Byte_order_mark
			switch (bytes.Length)
			{
				// UTF-32, little-endian
				case >= 4 when bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0x00 && bytes[3] == 0x00:
					skipBytes = 4;
					return Encoding.UTF32;
				// UTF-32, big-endian
				case >= 4 when bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xfe && bytes[3] == 0xff:
					skipBytes = 4;
					// Encoding.BigEndianUTF32 is private, use GetEncoding() to return the static Encoding to prevent additional allocation
					return Encoding.GetEncoding(12001);
				// UTF-8 with BOM
				case >= 3 when bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf:
					skipBytes = 3;
					return Encoding.UTF8;
				// UTF-16, little-endian
				case >= 2 when bytes[0] == 0xff && bytes[1] == 0xfe:
					skipBytes = 2;
					return Encoding.Unicode;
				// UTF-16, big-endian
				case >= 2 when bytes[0] == 0xfe && bytes[1] == 0xff:
					skipBytes = 2;
					return Encoding.BigEndianUnicode;
				// Default to UTF-8
				default:
					skipBytes = 0;
					return Encoding.UTF8;
			}
		}

		/// <summary>
		/// Get the encoding of a span
		/// </summary>
		/// <param name="bytes">Bytes to scan for a BOM</param>
		/// <returns>The encoding</returns>
		public static Encoding GetEncoding(ReadOnlySpan<byte> bytes) => GetEncoding(bytes, out int _);

		/// <summary>
		/// Get the encoding of a file
		/// </summary>
		/// <param name="filePath">File path to scan for a BOM</param>
		/// <param name="skipBytes">Number of bytes to skip</param>
		/// <returns>The encoding</returns>
		public static Encoding GetEncoding(string filePath, out int skipBytes)
		{
			try
			{
				byte[] bytes = new byte[4];
				using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					stream.ReadExactly(bytes, 0, 4);
				}
				return GetEncoding(bytes, out skipBytes);
			}
			catch (IOException)
			{
			}
			skipBytes = 0;
			return Encoding.UTF8;
		}

		/// <summary>
		/// Get the encoding of a span
		/// </summary>
		/// <param name="filePath">File path to scan for a BOM</param>
		/// <returns>The encoding</returns>
		public static Encoding GetEncoding(string filePath) => GetEncoding(filePath, out int _);

		/// <summary>
		/// Creates a directory tree, with all intermediate branches
		/// </summary>
		/// <param name="directory">The directory to create</param>
		public static void CreateDirectoryTree(DirectoryReference directory)
		{
			if (!DirectoryReference.Exists(directory))
			{
				DirectoryReference? parentDirectory = directory.ParentDirectory;
				if (parentDirectory != null)
				{
					CreateDirectoryTree(parentDirectory);
				}
				DirectoryReference.CreateDirectory(directory);
			}
		}
	}
}
