// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Auto-derived contract version per Toolchain Contract Rev 13
/// Section 10.2. The string is composed of a hand-bumped semantic tag
/// plus the first 16 hex characters of a BLAKE3 hash over a canonical
/// serialization of <see cref="ContractSurface"/>: any change to any
/// element of the surface produces a new hash and a new
/// <see cref="Current"/>.
/// </summary>
/// <remarks>
/// <para>
/// The composite format is <c>$"{SemanticVersionTag}+{StructureHash[0..16]}"</c>,
/// e.g. <c>"13.0+a1b2c3d4e5f60718"</c>. The string flows through:
/// </para>
/// <list type="bullet">
///   <item>Symbol mangling (XIL2CPP -- Contract Section 2.2 <c>{ContractVersion}</c> component).</item>
///   <item>Reflection metadata version (XHT -- Contract Section 1.5).</item>
///   <item>ActionHistory cache-key input (XBT.ActionGraph -- /Documents/XBT.html Section 15.1).</item>
///   <item>The <c>Manifest.ContractVersion</c> field that XHT and XIL2CPP both verify on read (Contract Section 10.2).</item>
/// </list>
/// <para>
/// The canonicalization algorithm is documented in
/// <see cref="ComputeStructureHash"/>. The choice of BLAKE3 mirrors the
/// engine-wide content-hash decision (<see cref="IoHash"/>); the choice
/// of 16-hex-char truncation mirrors the Contract's "first ten hex
/// characters of the contract-structure BLAKE3" prescription but with
/// 16 chars (6 extra bits of disambiguation) so collision probability
/// over the contract's lifetime is negligible.
/// </para>
/// </remarks>
public static class ContractVersion
{
    /// <summary>
    /// The composite contract-version string. Format:
    /// <c>$"{SemanticVersionTag}+{first-16-hex-of-BLAKE3-of-canonical-surface}"</c>.
    /// </summary>
    /// <remarks>
    /// Computed once at static-init from <see cref="ContractSurface"/>
    /// and the canonical serialization. The value is stable for the
    /// lifetime of the process; <see cref="Lazy{T}"/> guarantees
    /// thread-safe single-evaluation semantics.
    /// </remarks>
    public static string Current => s_current.Value;

    /// <summary>
    /// The full 32-byte BLAKE3 hash of the canonical contract surface.
    /// <see cref="Current"/> exposes only the first 16 hex characters;
    /// callers needing the full hash for diagnostics or for cache-key
    /// composition should use this property directly.
    /// </summary>
    public static IoHash StructureHash => s_hash.Value;

    private static readonly Lazy<IoHash> s_hash = new(ComputeStructureHash);
    private static readonly Lazy<string> s_current = new(ComputeCurrent);

    private static string ComputeCurrent()
    {
        // First 16 hex chars of the 64-hex-char BLAKE3 digest. 64 bits of
        // collision space is sufficient over the contract's lifetime
        // (BLAKE3 is collision-resistant, and the surface is small).
        string hex = StructureHash.ToString();
        return string.Concat(ContractSurface.SemanticVersionTag, "+", hex.AsSpan(0, 16));
    }

    /// <summary>
    /// Canonicalize <see cref="ContractSurface"/> into a deterministic
    /// byte stream and BLAKE3-hash it. The canonicalization is the
    /// load-bearing piece of the auto-derivation: two structurally-
    /// equivalent surfaces must produce byte-identical streams; any
    /// surface change must produce a different stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The serialization is text rather than binary so a developer
    /// inspecting the input (e.g. via the
    /// <see cref="WriteCanonicalSurfaceTo"/> helper) sees something
    /// human-readable. The format is line-based and uses LF (not CRLF)
    /// line endings unconditionally so the same source produces the
    /// same hash on Windows and on Linux.
    /// </para>
    /// <para>
    /// Steps (in order; reordering changes the hash):
    /// </para>
    /// <list type="number">
    ///   <item>SemanticVersionTag + LF.</item>
    ///   <item>For each enum (sorted by name): enum-name + LF, then each member as <c>name=ordinal</c> + LF sorted by ordinal, then a blank line.</item>
    ///   <item>For each marker macro (preserved order): <c>marker:{name}</c> + LF.</item>
    ///   <item>For each body-macro suffix (preserved order): <c>bodysuffix:{name}</c> + LF.</item>
    ///   <item><c>mangling:{ManglingRuleExample}</c> + LF.</item>
    ///   <item><c>file_id:{FileIdScheme}</c> + LF.</item>
    ///   <item>For each exit code (sorted by code): <c>exit:{code}={mnemonic}</c> + LF.</item>
    ///   <item>For each action type (preserved order = slot ordinal): <c>action:{name}</c> + LF.</item>
    /// </list>
    /// </remarks>
    private static IoHash ComputeStructureHash()
    {
        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true))
        {
            NewLine = "\n",
        };

        WriteCanonicalSurfaceTo(writer);
        writer.Flush();

        return IoHash.Compute(ms.ToArray());
    }

    /// <summary>
    /// Write the canonical surface serialization to the provided
    /// writer. The test suite reproduces this algorithm independently
    /// (rather than calling this helper) so a refactor that changes
    /// the canonicalization is forced to update both sides; see
    /// <c>ContractVersionTests.StructureHash_IsDeterministic</c>.
    /// </summary>
    private static void WriteCanonicalSurfaceTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // 1. SemanticVersionTag.
        writer.Write(ContractSurface.SemanticVersionTag);
        writer.Write('\n');

        // 2. Enums -- already sorted by ContractSurface.ReflectEnums.
        foreach ((string enumName, IReadOnlyList<(string Name, int Ordinal)> members) in ContractSurface.Enums)
        {
            writer.Write(enumName);
            writer.Write('\n');
            foreach ((string memberName, int ordinal) in members)
            {
                writer.Write(memberName);
                writer.Write('=');
                writer.Write(ordinal);
                writer.Write('\n');
            }
            // Blank line terminates the enum entry.
            writer.Write('\n');
        }

        // 3. Marker macros.
        foreach (string marker in ContractSurface.MarkerMacros)
        {
            writer.Write("marker:");
            writer.Write(marker);
            writer.Write('\n');
        }

        // 4. Body-macro suffixes.
        foreach (string suffix in ContractSurface.BodyMacroSuffixes)
        {
            writer.Write("bodysuffix:");
            writer.Write(suffix);
            writer.Write('\n');
        }

        // 5. Mangling rule example.
        writer.Write("mangling:");
        writer.Write(ContractSurface.ManglingRuleExample);
        writer.Write('\n');

        // 6. File-ID scheme.
        writer.Write("file_id:");
        writer.Write(ContractSurface.FileIdScheme);
        writer.Write('\n');

        // 7. Exit codes -- sorted by code.
        (int Code, string Mnemonic)[] sortedExits = new (int, string)[ContractSurface.ExitCodes.Count];
        for (int i = 0; i < ContractSurface.ExitCodes.Count; i++)
        {
            sortedExits[i] = ContractSurface.ExitCodes[i];
        }
        Array.Sort(sortedExits, static (a, b) => a.Code.CompareTo(b.Code));
        foreach ((int code, string mnemonic) in sortedExits)
        {
            writer.Write("exit:");
            writer.Write(code);
            writer.Write('=');
            writer.Write(mnemonic);
            writer.Write('\n');
        }

        // 8. Action types -- declared order = slot ordinal.
        foreach (string action in ContractSurface.ActionTypes)
        {
            writer.Write("action:");
            writer.Write(action);
            writer.Write('\n');
        }
    }
}
