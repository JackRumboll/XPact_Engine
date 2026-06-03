// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Simgenics.XPact.XIL2CPP.Core;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="DiagnosticRecord"/> JSON serialisation + MSBuild
/// format per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12.
/// </summary>
public class DiagnosticRecordTests
{
    [Fact]
    public void WriteJson_EmitsRequiredFields_FullErrorRecord()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Error,
            Code: DiagnosticCodes.ReferenceCompileActionMissing,
            Message: "ReferenceCompileCSharpAction not registered.",
            File: "Engine/Source/Runtime/XScoring/Private/Foo.cs",
            Line: 14,
            Column: 1,
            Module: "XScoring");

        string json = RenderJson(r);

        Assert.Contains("\"tool\":\"XIL2CPP\"", json);
        Assert.Contains("\"severity\":\"error\"", json);
        Assert.Contains("\"code\":\"XIL2CPP170\"", json);
        Assert.Contains("\"file\":\"Engine/Source/Runtime/XScoring/Private/Foo.cs\"", json);
        Assert.Contains("\"line\":14", json);
        Assert.Contains("\"column\":1", json);
        Assert.Contains("\"message\":\"ReferenceCompileCSharpAction not registered.\"", json);
        Assert.Contains("\"module\":\"XScoring\"", json);
    }

    [Fact]
    public void WriteJson_OmitsOptionalFields_WhenNull()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Info,
            Code: DiagnosticCodes.LoggerSentinel,
            Message: "trace");

        string json = RenderJson(r);

        Assert.Contains("\"tool\":\"XIL2CPP\"", json);
        Assert.Contains("\"severity\":\"info\"", json);
        Assert.Contains("\"code\":\"XIL2CPP000\"", json);
        Assert.Contains("\"message\":\"trace\"", json);
        Assert.DoesNotContain("\"file\":", json);
        Assert.DoesNotContain("\"line\":", json);
        Assert.DoesNotContain("\"column\":", json);
        Assert.DoesNotContain("\"module\":", json);
        Assert.DoesNotContain("\"context\":", json);
    }

    [Fact]
    public void WriteJson_IncludesContextWhenPresent()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Warning,
            Code: DiagnosticCodes.LoggerSentinel,
            Message: "unresolved type",
            Context: new Dictionary<string, string>
            {
                { "candidateModule", "XCore" },
                { "endColumn", "5" },
            });

        string json = RenderJson(r);
        Assert.Contains("\"context\":{", json);
        Assert.Contains("\"candidateModule\":\"XCore\"", json);
        Assert.Contains("\"endColumn\":\"5\"", json);
    }

    [Fact]
    public void WriteJson_OmitsEmptyContext()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Info,
            Code: DiagnosticCodes.LoggerSentinel,
            Message: "trace",
            Context: new Dictionary<string, string>());

        string json = RenderJson(r);
        Assert.DoesNotContain("\"context\":", json);
    }

    [Fact]
    public void WriteJson_IsSingleLineWithTrailingNewline()
    {
        DiagnosticRecord r = new(DiagnosticSeverity.Error, DiagnosticCodes.InternalCompilerError, "boom");
        string raw = RenderJsonRaw(r);
        Assert.EndsWith("\n", raw);
        string body = raw.TrimEnd('\n');
        Assert.DoesNotContain("\n", body);
    }

    [Fact]
    public void WriteJson_IsValidParseableJson()
    {
        DiagnosticRecord r = new(
            DiagnosticSeverity.Error,
            DiagnosticCodes.ManifestUnknownAbiEnvelopeTag,
            "test message",
            File: "Foo.cs",
            Line: 10,
            Column: 5);

        string raw = RenderJsonRaw(r);
        string body = raw.TrimEnd('\n');

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal("XIL2CPP", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("error", doc.RootElement.GetProperty("severity").GetString());
        Assert.Equal("XIL2CPP140", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("column").GetInt32());
    }

    [Fact]
    public void FormatMsBuild_FullForm_WithFileLineColumn()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Error,
            Code: DiagnosticCodes.LangVersionTooHigh,
            Message: "Module declares LangVersion higher than C# 12.",
            File: "Engine/Source/Runtime/XGameFramework/Private/Foo.cs",
            Line: 14,
            Column: 1);

        string line = r.FormatMsBuild();
        Assert.Equal(
            "Engine/Source/Runtime/XGameFramework/Private/Foo.cs(14,1): error XIL2CPP020: Module declares LangVersion higher than C# 12.",
            line);
    }

    [Fact]
    public void FormatMsBuild_LineOnly_WhenColumnNull()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Warning,
            Code: DiagnosticCodes.LoggerSentinel,
            Message: "test",
            File: "Foo.cs",
            Line: 42);
        Assert.Equal("Foo.cs(42): warning XIL2CPP000: test", r.FormatMsBuild());
    }

    [Fact]
    public void FormatMsBuild_FileOnly_WhenLineNull()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Info,
            Code: DiagnosticCodes.LoggerSentinel,
            Message: "informational",
            File: "Foo.cs");
        Assert.Equal("Foo.cs: info XIL2CPP000: informational", r.FormatMsBuild());
    }

    [Fact]
    public void FormatMsBuild_NoFile_FallsBackToBareForm()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Error,
            Code: DiagnosticCodes.InternalCompilerError,
            Message: "internal failure");
        Assert.Equal("error XIL2CPP900: internal failure", r.FormatMsBuild());
    }

    [Fact]
    public void ToolProperty_AlwaysXil2Cpp()
    {
        DiagnosticRecord r = new(DiagnosticSeverity.Info, DiagnosticCodes.LoggerSentinel, "msg");
        Assert.Equal("XIL2CPP", r.Tool);
    }

    private static string RenderJson(DiagnosticRecord r)
    {
        return RenderJsonRaw(r).TrimEnd('\n');
    }

    private static string RenderJsonRaw(DiagnosticRecord r)
    {
        using MemoryStream ms = new();
        using StreamWriter w = new(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        r.WriteJson(w);
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
