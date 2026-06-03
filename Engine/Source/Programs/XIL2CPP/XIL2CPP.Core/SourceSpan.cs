// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XIL2CPP.Core;

/// <summary>
/// A 1-based source location span: a file path plus an inclusive
/// (start line, start column) .. (end line, end column) range. The
/// front-end (XIL2CPP.Frontend, Phase 6.a Pass 1) translates Roslyn's
/// 0-based <c>FileLinePositionSpan</c> into this 1-based form so anchored
/// <see cref="DiagnosticRecord"/>s match the MSBuild diagnostic convention
/// (which is 1-based) without re-deriving the offset at each call site.
/// </summary>
/// <remarks>
/// Line and column numbers are 1-based to mirror MSBuild / IDE conventions
/// (Roslyn reports 0-based). A diagnostic anchored to a single point uses
/// the start coordinates; <see cref="DiagnosticRecord.Line"/> /
/// <see cref="DiagnosticRecord.Column"/> are populated from
/// <see cref="StartLine"/> / <see cref="StartColumn"/>.
/// </remarks>
/// <param name="File">Absolute or repo-relative source file path.</param>
/// <param name="StartLine">Start line, 1-based. Must be &gt;= 1.</param>
/// <param name="StartColumn">Start column, 1-based. Must be &gt;= 1.</param>
/// <param name="EndLine">End line, 1-based, inclusive. Must be &gt;= <paramref name="StartLine"/>.</param>
/// <param name="EndColumn">End column, 1-based, inclusive. Must be &gt;= 1.</param>
public readonly record struct SourceSpan(
    string File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn)
{
    /// <summary>
    /// Construct a span from validated 1-based coordinates.
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="file"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If any coordinate is &lt; 1, or the end precedes the start.</exception>
    public SourceSpan(string file, int startLine, int startColumn, int endLine, int endColumn, bool validate)
        : this(file, startLine, startColumn, endLine, endColumn)
    {
        if (!validate)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(startColumn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endColumn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
        if (endLine == startLine)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(endColumn, startColumn);
        }
    }

    /// <summary>
    /// Construct a single-point span (start == end) at the given 1-based
    /// coordinates. Validates the inputs.
    /// </summary>
    /// <param name="file">Source file path. Must not be null.</param>
    /// <param name="line">Line, 1-based. Must be &gt;= 1.</param>
    /// <param name="column">Column, 1-based. Must be &gt;= 1.</param>
    /// <returns>A span covering the single point.</returns>
    public static SourceSpan Point(string file, int line, int column)
        => new(file, line, column, line, column, validate: true);

    /// <summary>
    /// True iff this span covers a single point (start equals end).
    /// </summary>
    public bool IsPoint => StartLine == EndLine && StartColumn == EndColumn;

    /// <summary>
    /// Build a file-anchored <see cref="DiagnosticRecord"/> for this span,
    /// using the start coordinates as the 1-based line / column anchor.
    /// </summary>
    /// <param name="severity">Severity bucket.</param>
    /// <param name="code">An <see cref="DiagnosticCodes"/> constant.</param>
    /// <param name="message">Human-readable diagnostic text.</param>
    /// <param name="module">Owning module name, or null.</param>
    /// <returns>The anchored diagnostic record.</returns>
    public DiagnosticRecord ToDiagnostic(
        DiagnosticSeverity severity,
        string code,
        string message,
        string? module = null)
        => new(severity, code, message, File, StartLine, StartColumn, module);
}
