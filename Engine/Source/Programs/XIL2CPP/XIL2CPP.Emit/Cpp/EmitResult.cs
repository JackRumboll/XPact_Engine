// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// One emitted source unit: the per-<c>.cs</c>-file pair of generated C++
/// outputs (the <c>.cs.h</c> header content + the <c>.cs.cpp</c> source
/// content) and the source-relative path the pair is named from, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (Pass 6 / Pass 7) +
/// Section 5.1. The next-wave class / method / property emitters PRODUCE
/// these; <see cref="Output.Pass7Writer"/> CONSUMES them (writing
/// <c>&lt;stem&gt;.cs.h</c> + <c>&lt;stem&gt;.cs.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>SourceRelativePath.</b> The path of the originating <c>.cs</c> file
/// relative to the module's source root (forward-slashed), used to derive the
/// output stem (e.g. <c>Game/HealthPickup.cs</c> yields
/// <c>HealthPickup.cs.h</c> + <c>HealthPickup.cs.cpp</c>). It is the input
/// identity for the per-action cache key (Pass 7) and is carried verbatim so
/// the writer never re-derives it.
/// </para>
/// <para>
/// <b>Content is the final bytes.</b> <see cref="HeaderContent"/> and
/// <see cref="SourceContent"/> are the complete, ready-to-write text (LF
/// newlines, no BOM); the writer does not transform them. An emitter that
/// has nothing to emit for one side (e.g. a header-only or source-only unit)
/// supplies an empty string for that side, never null.
/// </para>
/// </remarks>
/// <param name="SourceRelativePath">The originating <c>.cs</c> file path, relative to the module source root (forward-slashed).</param>
/// <param name="HeaderContent">The complete <c>.cs.h</c> content (LF newlines; empty when no header is emitted).</param>
/// <param name="SourceContent">The complete <c>.cs.cpp</c> content (LF newlines; empty when no source is emitted).</param>
public sealed record EmitResult(
    string SourceRelativePath,
    string HeaderContent,
    string SourceContent);
