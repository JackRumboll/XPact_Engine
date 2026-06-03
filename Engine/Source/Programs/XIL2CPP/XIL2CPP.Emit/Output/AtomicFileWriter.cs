// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;

namespace Simgenics.XPact.XIL2CPP.Emit.Output;

/// <summary>
/// Atomic-write helper for the XIL2CPP Pass-7 output writer: writes to a
/// uniquely-named sibling temp file then renames over the target, so a reader
/// never observes a torn output. Mirrors the established XIL2CPP.Entry write
/// pattern (<c>RefonlyCompileMode.WriteAtomic</c> /
/// <c>TranspileModuleMode</c>) and the XHT.Core.AtomicFile temp-then-rename
/// discipline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Encoding.</b> Text is written as UTF-8 without a BOM (the engine-wide
/// on-disk encoding) with the content's LF newlines preserved verbatim -- the
/// emitters already produce LF-only content via <see cref="Cpp.CppWriter"/>.
/// </para>
/// <para>
/// <b>Temp-file naming.</b> <c>&lt;dest&gt;.tmp-&lt;guid&gt;</c>, matching the
/// existing XIL2CPP.Entry atomic-write helpers. The <see cref="Guid"/> here is
/// a temp-file de-collision nonce (two concurrent module writers targeting the
/// same directory), NOT emitted content -- the "no Guid in emit code" rule
/// applies to the C++ output bytes, which carry no nonce.
/// </para>
/// <para>
/// <b>Atomicity.</b> <see cref="File.Move(string,string,bool)"/> with
/// <c>overwrite: true</c> is atomic on a single volume on Windows and Linux.
/// The temp file is cleaned up best-effort on any failure path.
/// </para>
/// </remarks>
public static class AtomicFileWriter
{
    /// <summary>
    /// Atomically write <paramref name="content"/> to
    /// <paramref name="destPath"/> as UTF-8 without a BOM.
    /// </summary>
    /// <param name="destPath">Absolute destination path. Must not be null / empty / whitespace.</param>
    /// <param name="content">Text to write (LF newlines preserved). Must not be null; empty produces an empty file.</param>
    /// <exception cref="ArgumentException">If <paramref name="destPath"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="content"/> is null.</exception>
    public static void WriteAllText(string destPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentNullException.ThrowIfNull(content);

        UTF8Encoding utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        WriteAllBytes(destPath, utf8NoBom.GetBytes(content));
    }

    /// <summary>
    /// Atomically write <paramref name="bytes"/> to
    /// <paramref name="destPath"/>.
    /// </summary>
    /// <param name="destPath">Absolute destination path. Must not be null / empty / whitespace.</param>
    /// <param name="bytes">Bytes to write. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="destPath"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="bytes"/> is null.</exception>
    public static void WriteAllBytes(string destPath, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentNullException.ThrowIfNull(bytes);

        string fullOutput = Path.GetFullPath(destPath);
        string? directory = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Temp-file naming mirrors RefonlyCompileMode.WriteAtomic: a unique
        // sibling nonce so two concurrent writers do not collide on the temp.
        string tempPath = fullOutput + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            // File.Move with overwrite is atomic on a single volume on both
            // Windows and Linux for the rename step.
            File.Move(tempPath, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the temp file is uniquely named so a
                    // leftover never corrupts a subsequent run.
                }
            }
        }
    }
}
