// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;
using MetadataReferenceResolver = Simgenics.XPact.XIL2CPP.Frontend.MetadataReferenceResolver;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// Cross-module type-resolution coverage for the Pass-1 reference pipeline
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8. A dependency
/// module's reference-only DLL is synthesised in-test via
/// <c>compilation.Emit(..., new EmitOptions(metadataOnly: true))</c> (the
/// same shape XBT's ReferenceCompileCSharpAction produces) and written to a
/// scratch <c>&lt;Module&gt;/Reference/&lt;Module&gt;.refonly.dll</c>; the
/// consumer module then resolves a type from it.
/// </summary>
public sealed class CrossModuleResolutionTests : IDisposable
{
    private readonly string _scratch;

    public CrossModuleResolutionTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "XIL2CPP-XModule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Emit a reference-only DLL for a synthetic dependency module and place
    /// it at the Section-9.8 path under the scratch root.
    /// </summary>
    private string EmitDependencyRefOnly(string moduleName, string source)
    {
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes(
            $"/dep/{moduleName}.cs",
            System.Text.Encoding.UTF8.GetBytes(source),
            ParseOptionsFactory.Create());
        Assert.Empty(parsed.SyntaxDiagnostics);

        ReferenceSet refs = MetadataReferenceResolver.BuildFromReferences(
            FrontendTestHelpers.BclReferences(),
            Array.Empty<string>(),
            _ => null);
        CSharpCompilation dep = CompilationBuilder.Build(moduleName, new[] { parsed.Tree }, refs);

        string referenceDir = MetadataReferenceResolver.ComposeReferenceRoot(_scratch, moduleName);
        Directory.CreateDirectory(referenceDir);
        string refOnlyPath = Path.Combine(referenceDir, moduleName + MetadataReferenceResolver.RefOnlyDllSuffix);

        using FileStream peStream = File.Create(refOnlyPath);
        EmitResult emit = dep.Emit(peStream, options: new EmitOptions(metadataOnly: true));
        Assert.True(emit.Success, "metadataOnly emit failed: " + string.Join(" | ", emit.Diagnostics));

        return refOnlyPath;
    }

    [Fact]
    public void ConsumerResolvesTypeFromDependencyRefOnlyDll()
    {
        EmitDependencyRefOnly(
            "XDep",
            "namespace XDep; public class Widget { public int Spin() { return 7; } }");

        const string consumerSource = """
            namespace XConsumer;
            public class User
            {
                public int Use(XDep.Widget w) => w.Spin();
            }
            """;
        ModuleParser.ParsedFile consumer = ModuleParser.ParseBytes(
            "/consumer/User.cs",
            System.Text.Encoding.UTF8.GetBytes(consumerSource),
            ParseOptionsFactory.Create());

        List<MetadataReference> bcl = FrontendTestHelpers.BclReferences().ToList();
        ReferenceSet refs = MetadataReferenceResolver.BuildFromReferences(
            bcl,
            new[] { "XDep" },
            module => MetadataReferenceResolver.ComposeReferenceRoot(_scratch, module));

        // The reference DLL was found: no missing-DLL warning.
        Assert.Empty(refs.Diagnostics);

        CSharpCompilation compilation = CompilationBuilder.Build("XConsumer", new[] { consumer.Tree }, refs);
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == RoslynSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "Expected clean cross-module bind, got: " + string.Join(" | ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public void RefOnlyReference_HasModuleProvenance()
    {
        EmitDependencyRefOnly("XDep", "namespace XDep; public class Widget { }");

        ReferenceSet refs = MetadataReferenceResolver.BuildFromReferences(
            FrontendTestHelpers.BclReferences(),
            new[] { "XDep" },
            module => MetadataReferenceResolver.ComposeReferenceRoot(_scratch, module));

        // The last reference is the dependency DLL; it maps to its module.
        MetadataReference depReference = refs.References.Last();
        Assert.Equal("XDep", refs.OwningModuleOf(depReference));

        // A BCL reference (first) has no owning XPact module.
        MetadataReference bclReference = refs.References.First();
        Assert.Null(refs.OwningModuleOf(bclReference));
    }

    [Fact]
    public void MissingRefOnlyDll_DegradesWithWarning_DoesNotThrow()
    {
        // No DLL is emitted for XAbsent; the resolver records a warning and
        // omits the reference rather than crashing.
        ReferenceSet refs = MetadataReferenceResolver.BuildFromReferences(
            FrontendTestHelpers.BclReferences(),
            new[] { "XAbsent" },
            module => MetadataReferenceResolver.ComposeReferenceRoot(_scratch, module));

        DiagnosticRecord warning = Assert.Single(refs.Diagnostics);
        Assert.Equal(XilSeverity.Warning, warning.Severity);
        Assert.Equal("XAbsent", warning.Module);
        Assert.Contains("XAbsent", warning.Message, StringComparison.Ordinal);

        // The set still contains the BCL references (degradation, not failure).
        Assert.NotEmpty(refs.References);
        // XAbsent remains in the attempted-dependency set for the translator hint.
        Assert.Contains("XAbsent", refs.DependencyModuleNames);
    }

    [Fact]
    public void UnresolvedType_FromMissingDependency_GetsAugmentedHint()
    {
        // The consumer references a type from XDep, but XDep's reference DLL
        // is absent. The CS0234/CS0246 diagnostic must be augmented with the
        // candidate module name and carry it in the record's Context.
        const string consumerSource = """
            namespace XConsumer;
            public class User
            {
                public int Use(XDep.Widget w) => 0;
            }
            """;
        ModuleParser.ParsedFile consumer = ModuleParser.ParseBytes(
            "/consumer/User.cs",
            System.Text.Encoding.UTF8.GetBytes(consumerSource),
            ParseOptionsFactory.Create());

        ReferenceSet refs = MetadataReferenceResolver.BuildFromReferences(
            FrontendTestHelpers.BclReferences(),
            new[] { "XDep" },
            // Reference root that exists but contains no DLL -> missing.
            module => MetadataReferenceResolver.ComposeReferenceRoot(_scratch, module));

        CSharpCompilation compilation = CompilationBuilder.Build("XConsumer", new[] { consumer.Tree }, refs);

        IReadOnlyList<DiagnosticRecord> records = RoslynDiagnosticTranslator.TranslateAll(
            compilation.GetDiagnostics(),
            "XConsumer",
            refs);

        DiagnosticRecord unresolved = records.First(r =>
            RoslynDiagnosticTranslator.IsUnresolvedNameDiagnostic(r.Code));

        Assert.Contains("XDep", unresolved.Message, StringComparison.Ordinal);
        Assert.NotNull(unresolved.Context);
        Assert.True(unresolved.Context!.TryGetValue(
            RoslynDiagnosticTranslator.ContextKeyCandidateModules, out string? candidates));
        Assert.Contains("XDep", candidates!, StringComparison.Ordinal);
    }
}
