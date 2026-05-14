// Copyright (c) Simgenics. Licensed under the same terms as XPact Engine.
//
// Exit-code-based test driver for Task 0.1. Each test prints PASS / FAIL with
// a one-line description, and the process returns 0 if every test passes,
// non-zero (= number of failures) otherwise. Kept dependency-free on purpose
// so we don't pull in xUnit / Microsoft.NET.Test.Sdk for the first port.

using System;
using System.IO;
using XPact.Core.IO;
using XPact.Build.Platform;

namespace XPact.Core.Tests;

internal static class Program
{
	private static int s_failures;

	private static void Check(string name, bool condition, string? detail = null)
	{
		if (condition)
		{
			Console.Out.WriteLine($"PASS  {name}");
		}
		else
		{
			s_failures++;
			Console.Error.WriteLine($"FAIL  {name}{(detail is null ? "" : ": " + detail)}");
		}
	}

	public static int Main()
	{
		TestFileReferenceRoundTrip();
		TestDirectoryReferenceRoundTrip();
		TestBinaryArchiveRoundTrip();
		TestUnrealTargetPlatformWin64();
		TestUnrealTargetConfigurationDevelopment();

		if (s_failures == 0)
		{
			Console.Out.WriteLine();
			Console.Out.WriteLine("All XPact.Core / XPact.Build smoke tests passed.");
			return 0;
		}

		Console.Error.WriteLine();
		Console.Error.WriteLine($"{s_failures} test(s) failed.");
		return s_failures;
	}

	// ----- 1. FileReference round-trip from string path and back ---------------------------------

	private static void TestFileReferenceRoundTrip()
	{
		// Pick a path that is guaranteed not to depend on cwd lookup behaviour.
		string raw = OperatingSystem.IsWindows()
			? @"C:\Temp\subdir\hello.txt"
			: "/tmp/subdir/hello.txt";

		FileReference fileRef = new(raw);

		// FullName should match the absolute, separator-normalized path. We compare
		// against Path.GetFullPath(raw) because FileReference uses that under the hood.
		string expected = Path.GetFullPath(raw);
		Check("FileReference.FullName matches Path.GetFullPath", fileRef.FullName == expected, $"got '{fileRef.FullName}', expected '{expected}'");

		// ToString round-trips back to the same path
		Check("FileReference.ToString round-trips", fileRef.ToString() == expected);

		// Sanitize.None constructor preserves the input verbatim (no re-normalization)
		FileReference sanitized = new(expected, FileReference.Sanitize.None);
		Check("FileReference Sanitize.None round-trips", sanitized.FullName == expected && sanitized.Equals(fileRef));

		// GetFileName behaves as expected
		Check("FileReference.GetFileName", fileRef.GetFileName() == "hello.txt");
		Check("FileReference.GetExtension", fileRef.GetExtension() == ".txt");

		// FromString of null/whitespace returns null
		Check("FileReference.FromString(null) returns null", FileReference.FromString(null) is null);
		Check("FileReference.FromString(\" \") returns null", FileReference.FromString("   ") is null);
	}

	// ----- 2. DirectoryReference round-trip ------------------------------------------------------

	private static void TestDirectoryReferenceRoundTrip()
	{
		string raw = OperatingSystem.IsWindows()
			? @"C:\Temp\subdir"
			: "/tmp/subdir";

		DirectoryReference dirRef = new(raw);

		// DirectoryReference normalises trailing slash. Use Path.GetFullPath, then trim trailing
		// separator if any (matching DirectoryReference's FixTrailingPathSeparator behaviour).
		string expected = Path.GetFullPath(raw);
		if (expected.Length > 1 && expected.EndsWith(Path.DirectorySeparatorChar))
		{
			expected = expected.TrimEnd(Path.DirectorySeparatorChar);
		}
		Check("DirectoryReference.FullName matches normalized path", dirRef.FullName == expected, $"got '{dirRef.FullName}', expected '{expected}'");

		// ToString round-trip
		Check("DirectoryReference.ToString round-trips", dirRef.ToString() == expected);

		// Sanitize.None ctor
		DirectoryReference sanitized = new(expected, DirectoryReference.Sanitize.None);
		Check("DirectoryReference Sanitize.None round-trips", sanitized.FullName == expected && sanitized.Equals(dirRef));

		// Combine with a sub-fragment then back
		DirectoryReference combined = DirectoryReference.Combine(dirRef, "nested");
		string expectedCombined = expected + Path.DirectorySeparatorChar + "nested";
		Check("DirectoryReference.Combine appends fragment", combined.FullName == expectedCombined, $"got '{combined.FullName}', expected '{expectedCombined}'");

		// FromString of null/whitespace returns null
		Check("DirectoryReference.FromString(null) returns null", DirectoryReference.FromString(null) is null);
	}

	// ----- 3. BinaryArchive round-trip -----------------------------------------------------------

	private static void TestBinaryArchiveRoundTrip()
	{
		using MemoryStream stream = new();

		using (BinaryArchiveWriter writer = new(stream))
		{
			writer.WriteBool(true);
			writer.WriteBool(false);
			writer.WriteByte(0x7F);
			writer.WriteSignedByte(-1);
			writer.WriteShort(-12345);
			writer.WriteUnsignedShort(54321);
			writer.WriteInt(-1_000_003);
			writer.WriteUnsignedInt(4_000_000_007u);
			writer.WriteLong(-9_000_000_000_000L);
			writer.WriteUnsignedLong(18_000_000_000_000_000_000UL);
			writer.WriteDouble(Math.PI);
			writer.WriteString("xpact");
			writer.WriteString(null);
			writer.WriteByteArray(new byte[] { 1, 2, 3, 4, 5 });
			writer.WriteIntArray(new[] { 10, 20, 30 });
			// Don't dispose-flush the stream — we still need to read from it. Flush() pushes the buffer.
			writer.Flush();
		}

		// Reset for reading
		byte[] payload = stream.ToArray();
		using BinaryArchiveReader reader = new(payload);

		Check("BinaryArchive: bool true round-trip", reader.ReadBool());
		Check("BinaryArchive: bool false round-trip", !reader.ReadBool());
		Check("BinaryArchive: byte round-trip", reader.ReadByte() == 0x7F);
		Check("BinaryArchive: sbyte round-trip", reader.ReadSignedByte() == -1);
		Check("BinaryArchive: short round-trip", reader.ReadShort() == -12345);
		Check("BinaryArchive: ushort round-trip", reader.ReadUnsignedShort() == 54321);
		Check("BinaryArchive: int round-trip", reader.ReadInt() == -1_000_003);
		Check("BinaryArchive: uint round-trip", reader.ReadUnsignedInt() == 4_000_000_007u);
		Check("BinaryArchive: long round-trip", reader.ReadLong() == -9_000_000_000_000L);
		Check("BinaryArchive: ulong round-trip", reader.ReadUnsignedLong() == 18_000_000_000_000_000_000UL);
		Check("BinaryArchive: double round-trip", reader.ReadDouble() == Math.PI);
		Check("BinaryArchive: string round-trip", reader.ReadString() == "xpact");
		Check("BinaryArchive: null string round-trip", reader.ReadString() is null);

		byte[]? bytes = reader.ReadByteArray();
		bool bytesMatch = bytes is not null
			&& bytes.Length == 5
			&& bytes[0] == 1 && bytes[1] == 2 && bytes[2] == 3 && bytes[3] == 4 && bytes[4] == 5;
		Check("BinaryArchive: byte array round-trip", bytesMatch);

		int[]? ints = reader.ReadIntArray();
		bool intsMatch = ints is not null
			&& ints.Length == 3
			&& ints[0] == 10 && ints[1] == 20 && ints[2] == 30;
		Check("BinaryArchive: int array round-trip", intsMatch);
	}

	// ----- 4. UnrealTargetPlatform.Win64 exists ---------------------------------------------------

	private static void TestUnrealTargetPlatformWin64()
	{
		UnrealTargetPlatform win64 = UnrealTargetPlatform.Win64;
		Check("UnrealTargetPlatform.Win64.ToString() == \"Win64\"", win64.ToString() == "Win64");

		// Parse should round-trip
		UnrealTargetPlatform parsed = UnrealTargetPlatform.Parse("Win64");
		Check("UnrealTargetPlatform.Parse(\"Win64\") equals Win64", parsed == win64);

		// IsValidName works
		Check("UnrealTargetPlatform.IsValidName(\"Win64\") is true", UnrealTargetPlatform.IsValidName("Win64"));
		Check("UnrealTargetPlatform.IsValidName(\"NotAPlatform\") is false", !UnrealTargetPlatform.IsValidName("NotAPlatform"));
	}

	// ----- 5. UnrealTargetConfiguration.Development exists ----------------------------------------

	private static void TestUnrealTargetConfigurationDevelopment()
	{
		UnrealTargetConfiguration dev = UnrealTargetConfiguration.Development;
		Check("UnrealTargetConfiguration.Development is defined", Enum.IsDefined(typeof(UnrealTargetConfiguration), dev));
		Check("UnrealTargetConfiguration.Development.ToString()", dev.ToString() == "Development");
	}
}
