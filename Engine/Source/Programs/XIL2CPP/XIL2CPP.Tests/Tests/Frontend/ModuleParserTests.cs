// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// Tests for <see cref="ModuleParser"/>: UTF-8 SHA-256 SourceText parse to a
/// path-keyed SyntaxTree and surfaced syntax diagnostics with 1-based
/// file:line:col after translation. Per /Documents/XIL2CPP.html Rev 4
/// Section 3.2.
/// </summary>
public sealed class ModuleParserTests
{
    private static CSharpParseOptions Options() => ParseOptionsFactory.Create();

    [Fact]
    public void ParseBytes_CleanSource_NoSyntaxDiagnostics()
    {
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes(
            "/repo/Clean.cs",
            Encoding.UTF8.GetBytes("namespace N; public class C { public int F() { return 1; } }"),
            Options());

        Assert.Empty(parsed.SyntaxDiagnostics);
        Assert.Equal("/repo/Clean.cs", parsed.Tree.FilePath);
    }

    [Fact]
    public void ParseBytes_UsesSha256ChecksumAlgorithm()
    {
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes(
            "/repo/Hashed.cs",
            Encoding.UTF8.GetBytes("public class C { }"),
            Options());

        SourceText text = parsed.Tree.GetText();
        Assert.Equal(SourceHashAlgorithm.Sha256, text.ChecksumAlgorithm);
    }

    [Fact]
    public void ParseBytes_ToleratesUtf8Bom()
    {
        byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("public class C { }"))
            .ToArray();

        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes("/repo/Bom.cs", withBom, Options());

        Assert.Empty(parsed.SyntaxDiagnostics);
        Assert.Single(parsed.Tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>());
    }

    [Fact]
    public void ParseBytes_MissingSemicolon_SurfacesSyntaxDiagnostic()
    {
        // Line 2 has the missing-semicolon error; column points at the
        // location Roslyn reports. We assert the diagnostic translates to a
        // 1-based file:line:col record.
        const string source = "public class C\n{\n    int x = 1\n}\n";
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes("/repo/Bad.cs", Encoding.UTF8.GetBytes(source), Options());

        Assert.NotEmpty(parsed.SyntaxDiagnostics);

        // Translate and assert the location is 1-based and anchored to the file.
        var records = RoslynDiagnosticTranslator.TranslateAll(parsed.SyntaxDiagnostics, "TestModule", references: null);
        DiagnosticRecord error = Assert.Single(records, r => r.Severity == XilSeverity.Error);

        Assert.Equal("/repo/Bad.cs", error.File);
        Assert.NotNull(error.Line);
        Assert.NotNull(error.Column);
        Assert.True(error.Line >= 1, "line is 1-based");
        Assert.True(error.Column >= 1, "column is 1-based");
        Assert.Equal("TestModule", error.Module);
    }

    [Fact]
    public void ParseBytes_DiagnosticLineColumn_AreOneBased()
    {
        // The error is on the first line; Roslyn reports 0-based line 0,
        // which must translate to 1-based line 1.
        const string source = "class C { void M() { return }"; // missing ; -> error on line 1
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes("/repo/L.cs", Encoding.UTF8.GetBytes(source), Options());

        var records = RoslynDiagnosticTranslator.TranslateAll(parsed.SyntaxDiagnostics, "M", references: null);
        DiagnosticRecord error = records.First(r => r.Severity == XilSeverity.Error);

        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void ParseFile_ReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), "XIL2CPP-ModuleParser-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, "public class FromDisk { }", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            ModuleParser.ParsedFile parsed = ModuleParser.ParseFile(path, Options());
            Assert.Empty(parsed.SyntaxDiagnostics);
            Assert.Equal(path, parsed.AbsolutePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseBytes_NullPath_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ModuleParser.ParseBytes("  ", Encoding.UTF8.GetBytes("class C {}"), Options()));
    }

    [Fact]
    public void ParseBytes_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ModuleParser.ParseBytes("/x.cs", Encoding.UTF8.GetBytes("class C {}"), null!));
    }
}
