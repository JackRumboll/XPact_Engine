// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XIL2CPP.Core;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="SourceSpan"/>: 1-based coordinate validation and
/// translation into anchored <see cref="DiagnosticRecord"/>s.
/// </summary>
public class SourceSpanTests
{
    [Fact]
    public void Point_BuildsSinglePointSpan()
    {
        SourceSpan s = SourceSpan.Point("Foo.cs", 3, 7);
        Assert.Equal("Foo.cs", s.File);
        Assert.Equal(3, s.StartLine);
        Assert.Equal(7, s.StartColumn);
        Assert.Equal(3, s.EndLine);
        Assert.Equal(7, s.EndColumn);
        Assert.True(s.IsPoint);
    }

    [Fact]
    public void MultiLineSpan_IsNotAPoint()
    {
        SourceSpan s = new("Foo.cs", 3, 7, 5, 2, validate: true);
        Assert.False(s.IsPoint);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)] // start line < 1
    [InlineData(1, 0, 1, 1)] // start column < 1
    [InlineData(1, 1, 1, 0)] // end column < 1
    [InlineData(5, 1, 4, 1)] // end line precedes start line
    [InlineData(3, 7, 3, 2)] // same line, end column precedes start column
    public void Validate_RejectsBadCoordinates(int startLine, int startColumn, int endLine, int endColumn)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SourceSpan("Foo.cs", startLine, startColumn, endLine, endColumn, validate: true));
    }

    [Fact]
    public void Validate_NullFile_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SourceSpan(null!, 1, 1, 1, 1, validate: true));
    }

    [Fact]
    public void ToDiagnostic_AnchorsAtStartCoordinates()
    {
        SourceSpan s = new("Foo.cs", 14, 3, 14, 20, validate: true);
        DiagnosticRecord r = s.ToDiagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.LangVersionTooHigh,
            "bad lang version",
            module: "XCore");

        Assert.Equal(DiagnosticSeverity.Error, r.Severity);
        Assert.Equal("XIL2CPP020", r.Code);
        Assert.Equal("Foo.cs", r.File);
        Assert.Equal(14, r.Line);
        Assert.Equal(3, r.Column);
        Assert.Equal("XCore", r.Module);
        Assert.Equal(
            "Foo.cs(14,3): error XIL2CPP020: bad lang version",
            r.FormatMsBuild());
    }
}
