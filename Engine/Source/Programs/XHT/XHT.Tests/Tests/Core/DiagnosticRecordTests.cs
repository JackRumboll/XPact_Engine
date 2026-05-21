// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="DiagnosticRecord"/> JSON serialisation per
/// <c>/Documents/XHT.html</c> Rev 7 Section 1.4 + Section 12.4 + MSBuild
/// format per Section 12.1.
/// </summary>
public class DiagnosticRecordTests
{
    [Fact]
    public void WriteJson_EmitsRequiredFields_FullErrorRecord()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Error,
            Code: "XHT070",
            Message: "Reflected type XValve missing XGENERATED_BODY().",
            File: "Engine/Source/Runtime/XScoring/Public/XValve.h",
            Line: 14,
            Column: 1,
            Module: "XScoring");

        string json = RenderJson(r);

        // Required fields per XHT.html Section 1.4 example.
        Assert.Contains("\"tool\":\"XHT\"", json);
        Assert.Contains("\"severity\":\"error\"", json);
        Assert.Contains("\"code\":\"XHT070\"", json);
        Assert.Contains("\"file\":\"Engine/Source/Runtime/XScoring/Public/XValve.h\"", json);
        Assert.Contains("\"line\":14", json);
        Assert.Contains("\"column\":1", json);
        Assert.Contains("\"message\":\"Reflected type XValve missing XGENERATED_BODY().\"", json);
        Assert.Contains("\"module\":\"XScoring\"", json);
    }

    [Fact]
    public void WriteJson_OmitsOptionalFields_WhenNull()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Info,
            Code: "XHT000",
            Message: "trace");

        string json = RenderJson(r);

        Assert.Contains("\"tool\":\"XHT\"", json);
        Assert.Contains("\"severity\":\"info\"", json);
        Assert.Contains("\"code\":\"XHT000\"", json);
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
            Code: "XHT070",
            Message: "no reflected types",
            Context: new Dictionary<string, string>
            {
                { "endLine", "1" },
                { "endColumn", "5" },
            });

        string json = RenderJson(r);
        Assert.Contains("\"context\":{", json);
        Assert.Contains("\"endLine\":\"1\"", json);
        Assert.Contains("\"endColumn\":\"5\"", json);
    }

    [Fact]
    public void WriteJson_OmitsEmptyContext()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Info,
            Code: "XHT000",
            Message: "trace",
            Context: new Dictionary<string, string>());

        string json = RenderJson(r);
        Assert.DoesNotContain("\"context\":", json);
    }

    [Fact]
    public void WriteJson_IsSingleLineWithTrailingNewline()
    {
        DiagnosticRecord r = new(DiagnosticSeverity.Error, "XHT062", "boom");
        string raw = RenderJsonRaw(r);
        Assert.EndsWith("\n", raw);
        // Strip the trailing newline; the remaining text must not have
        // any embedded newlines.
        string body = raw.TrimEnd('\n');
        Assert.DoesNotContain("\n", body);
    }

    [Fact]
    public void WriteJson_IsValidParseableJson()
    {
        DiagnosticRecord r = new(
            DiagnosticSeverity.Error,
            "XHT070",
            "test message",
            File: "X.h",
            Line: 10,
            Column: 5);

        string raw = RenderJsonRaw(r);
        string body = raw.TrimEnd('\n');

        // Round-trip parse to JsonDocument to verify well-formedness.
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal("XHT", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("error", doc.RootElement.GetProperty("severity").GetString());
        Assert.Equal("XHT070", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("column").GetInt32());
    }

    [Fact]
    public void FormatMsBuild_FullForm_WithFileLineColumn()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Error,
            Code: "XHT040",
            Message: "Missing XGENERATED_BODY() in reflected class <XValve>.",
            File: "Engine/Source/Runtime/XGameFramework/Public/XValve.h",
            Line: 14,
            Column: 1);

        string line = r.FormatMsBuild();
        Assert.Equal(
            "Engine/Source/Runtime/XGameFramework/Public/XValve.h(14,1): error XHT040: Missing XGENERATED_BODY() in reflected class <XValve>.",
            line);
    }

    [Fact]
    public void FormatMsBuild_LineOnly_WhenColumnNull()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Warning,
            Code: "XHT070",
            Message: "test",
            File: "X.h",
            Line: 42);
        Assert.Equal("X.h(42): warning XHT070: test", r.FormatMsBuild());
    }

    [Fact]
    public void FormatMsBuild_FileOnly_WhenLineNull()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Info,
            Code: "XHT000",
            Message: "informational",
            File: "X.h");
        Assert.Equal("X.h: info XHT000: informational", r.FormatMsBuild());
    }

    [Fact]
    public void FormatMsBuild_NoFile_FallsBackToBareForm()
    {
        DiagnosticRecord r = new(
            Severity: DiagnosticSeverity.Error,
            Code: "XHT062",
            Message: "internal failure");
        Assert.Equal("error XHT062: internal failure", r.FormatMsBuild());
    }

    [Fact]
    public void ToolProperty_AlwaysXht()
    {
        DiagnosticRecord r = new(DiagnosticSeverity.Info, "XHT000", "msg");
        Assert.Equal("XHT", r.Tool);
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
