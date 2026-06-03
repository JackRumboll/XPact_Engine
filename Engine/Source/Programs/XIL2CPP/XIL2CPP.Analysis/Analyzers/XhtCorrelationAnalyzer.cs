// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis.Analyzers;

/// <summary>
/// One C# type the module declares for reflection emit (an
/// <c>[XClass]</c>-marked type) correlated to the XHT-side FClass
/// scaffolding the per-module <c>.gen.manifest</c> declares for it, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Sections 10.3 + 10.5. When
/// <see cref="XhtProducesFClass"/> is true XIL2CPP MUST NOT re-emit the
/// FClass <c>constinit</c> + schema vector for the type (XHT owns that
/// emit per the Q14 resolution in Section 10.4); XIL2CPP emits only the
/// singleton-getter body + lifecycle slots + ClassConstructorFn.
/// </summary>
/// <param name="CSharpTypeName">
/// The fully-qualified metadata name of the C# type (e.g.
/// <c>XScoring.XValve</c>), the join key against the manifest.
/// </param>
/// <param name="CppTypeName">
/// The C++ type name the manifest pairs this C# type with (the
/// cross-language pair from Section 10.3), or null when the manifest does
/// not declare a cross-language pairing for the type.
/// </param>
/// <param name="FClassSymbol">
/// The XHT-declared FClass scaffolding symbol (e.g.
/// <c>Z_Construct_FClass_XValve</c> / the <c>&lt;Type&gt;.gen.h</c>
/// scaffolding base), or null when XHT declares none.
/// </param>
/// <param name="XhtProducesFClass">
/// True iff the manifest declares XHT-side FClass scaffolding for the type
/// (so XIL2CPP must skip its own FClass emit). When false XIL2CPP owns the
/// full emit for the type.
/// </param>
public sealed record XhtCorrelatedType(
    string CSharpTypeName,
    string? CppTypeName,
    string? FClassSymbol,
    bool XhtProducesFClass);

/// <summary>
/// The module-scoped table correlating the module's reflection-emit C#
/// types to the XHT-side FClass declarations in the per-module
/// <c>.gen.manifest</c>, per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Sections 10.3 + 10.5 (the cross-tool symbol-space contract,
/// XToolchainContract.html Section 10.2). XIL2CPP's emit pass reads this
/// so it does NOT duplicate the FClass scaffolding XHT already produces.
/// </summary>
/// <param name="ManifestPath">
/// The resolved absolute path the analyzer probed for the
/// <c>.gen.manifest</c>, or null when no candidate path could be derived
/// from the unit's source layout.
/// </param>
/// <param name="ManifestFound">
/// True iff a readable <c>.gen.manifest</c> was found at
/// <see cref="ManifestPath"/>. When false the table is empty (graceful
/// degradation: a transpile that runs before XHT has emitted produces no
/// correlations and no diagnostics).
/// </param>
/// <param name="CorrelatedTypes">
/// The correlated types, sorted by <see cref="XhtCorrelatedType.CSharpTypeName"/>
/// ordinal (deterministic). Empty when the manifest is absent.
/// </param>
public sealed record XhtCorrelationTable(
    string? ManifestPath,
    bool ManifestFound,
    IReadOnlyList<XhtCorrelatedType> CorrelatedTypes);

/// <summary>
/// Pass-3 analyzer (WU-23) that correlates the module's reflection-emit C#
/// types to the XHT-emitted per-module <c>.gen.manifest</c> FClass
/// scaffolding declarations, per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Sections 10.3 (cross-language type-pair manifest) + 10.5 (cross-language
/// binding table / canonical backing-field naming). The product is an
/// <see cref="XhtCorrelationTable"/> singleton telling the emit pass which
/// types XHT already produces FClass scaffolding for, so XIL2CPP does not
/// duplicate that emit (the Q14 resolution, Section 10.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Manifest discovery (path not yet finalized).</b> The exact
/// <c>.gen.manifest</c> path is not finalized at the contract level. XHT's
/// <c>emit-module</c> writer (<c>XHT.Manifest.GenManifestWriter</c>) emits a
/// per-module <c>&lt;Module&gt;.gen.manifest</c> into XHT's output dir; the
/// canonical intermediate layout (mirroring
/// <c>TranspileModuleMode.ResolveIntermediateRoot</c>) is
/// <c>&lt;repo&gt;/Intermediate/Build/XHT/&lt;Module&gt;/&lt;Module&gt;.gen.manifest</c>.
/// The analyzer derives the candidate path PURELY from the unit's source
/// file locations (walk up from a parsed file's directory to the ancestor
/// that has an <c>Engine/</c> sibling = the repo root; then probe the XHT
/// intermediate sub-tree) so it carries no ambient state and stays
/// deterministic (gate X-IL2CPP-CSPATH-DET). When no candidate path can be
/// derived, or the file is absent / unreadable / malformed, the analyzer
/// DEGRADES GRACEFULLY: it emits an empty table and ZERO diagnostics. This
/// is the common case at the current milestone (XIL2CPP runs concurrent
/// with XHT, Section 10.2, so the manifest may not exist yet).
/// </para>
/// <para>
/// <b>Self-contained reader.</b> The analyzer ships its OWN minimal,
/// dependency-free reader for the manifest's <c>[Generated]</c> +
/// forward-compatible <c>[Types]</c> sections rather than referencing
/// <c>XHT.Manifest</c> (a different program whose reader applies an XHT
/// ContractVersion gate that would couple XIL2CPP's analysis pass to XHT's
/// build). The reader is intentionally lenient: any structural problem
/// yields no correlations (no throw) so a hand-edited / partial manifest
/// never crashes the transpile.
/// </para>
/// <para>
/// <b>Codes owned (Section 12).</b>
/// <list type="bullet">
///   <item><description>
///     <c>XIL2CPP142</c> -- cross-tool symbol-space mismatch: a C# type the
///     module emits FClass scaffolding for that the manifest pairs to a
///     DIFFERENT FClass symbol than XIL2CPP's canonical
///     <c>Z_Construct_FClass_&lt;Type&gt;</c> (the two tools disagree on the
///     type pairing).
///   </description></item>
///   <item><description>
///     <c>XIL2CPP149</c> -- backing-field naming mismatch: the manifest
///     declares a backing-field name for one of the type's auto-properties
///     that differs from XIL2CPP's canonical <c>__BackingField_&lt;X&gt;</c>
///     (Section 10.5; both tools must agree).
///   </description></item>
///   <item><description>
///     <c>XIL2CPP150</c> -- cross-language type-pair manifest mismatch: a
///     manifest <c>[Types]</c> pair (C# type / C++ type) whose C# side names
///     a type this module does not declare for reflection emit (the pair is
///     not aligned with the C# source; Section 10.3).
///   </description></item>
///   <item><description>
///     <c>XIL2CPP151</c> -- FProperty descriptor missing: the manifest pairs
///     the type but omits an FProperty descriptor for one of the type's
///     reflected (<c>[XProperty]</c>) fields (Section 10.2).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class XhtCorrelationAnalyzer : ISemanticAnalyzer
{
    // -----------------------------------------------------------------
    // Canonical naming conventions (Section 10.5 + Section 10.4).
    // -----------------------------------------------------------------

    /// <summary>
    /// XIL2CPP's canonical FClass scaffolding-symbol prefix per Section 10.4
    /// (the <c>Z_Construct_FClass_*</c> aggregation point). XHT and XIL2CPP
    /// must agree on the symbol for a given type.
    /// </summary>
    private const string FClassSymbolPrefix = "Z_Construct_FClass_";

    /// <summary>
    /// XIL2CPP's canonical auto-property backing-field prefix per
    /// Section 10.5 (deterministic, C-identifier-safe; NO angle brackets).
    /// </summary>
    private const string BackingFieldPrefix = "__BackingField_";

    // Recognized reflection-emit attribute metadata names (Section 10.5).
    private const string XClassAttributeMetadataName = "XClassAttribute";
    private const string XPropertyAttributeMetadataName = "XPropertyAttribute";
    private const string XPactAttributeNamespace = "XPact.CoreXObject";

    /// <summary>
    /// Explicit manifest-path override (test seam). Null on the production
    /// auto-discovery path, where the path is derived from the unit's source
    /// layout. Settable only via the internal constructor.
    /// </summary>
    private readonly string? _manifestPathOverride;

    /// <summary>
    /// Production constructor. <see cref="Pass3Driver"/> reflection-discovers
    /// the analyzer through this public parameterless ctor; the manifest path
    /// is derived from the unit's source layout at analysis time.
    /// </summary>
    public XhtCorrelationAnalyzer()
        : this(manifestPathOverride: null)
    {
    }

    /// <summary>
    /// Test constructor: pin the manifest path explicitly so a synthetic
    /// fixture written to a temp directory can be correlated without the
    /// repo-layout walk. Internal so only <c>XIL2CPP.Tests</c> uses it.
    /// </summary>
    /// <param name="manifestPathOverride">
    /// Absolute path to the <c>.gen.manifest</c> to read, or null to use the
    /// production source-layout derivation.
    /// </param>
    internal XhtCorrelationAnalyzer(string? manifestPathOverride)
    {
        _manifestPathOverride = manifestPathOverride;
    }

    /// <inheritdoc />
    public string Name => "XhtCorrelationAnalyzer";

    /// <inheritdoc />
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        string? manifestPath = _manifestPathOverride ?? DeriveManifestPath(unit);
        ParsedManifest? manifest = TryReadManifest(manifestPath);

        if (manifest is null)
        {
            // Graceful degradation: no readable manifest -> empty table, no
            // diagnostics. The emit pass treats every type as XIL2CPP-owned.
            builder.SetSingleton(new XhtCorrelationTable(
                ManifestPath: manifestPath,
                ManifestFound: false,
                CorrelatedTypes: Array.Empty<XhtCorrelatedType>()));
            return;
        }

        // Collect the module's reflection-emit C# types (the [XClass]-marked
        // declared types) in deterministic order, keyed by metadata name.
        IReadOnlyList<ReflectedType> reflectedTypes = CollectReflectedTypes(unit);
        HashSet<string> reflectedTypeNames = new(StringComparer.Ordinal);
        foreach (ReflectedType rt in reflectedTypes)
        {
            reflectedTypeNames.Add(rt.MetadataName);
        }

        List<XhtCorrelatedType> correlated = new();

        // --- C#-driven correlation: for each [XClass] type, find the XHT
        // FClass scaffolding declaration and validate the pairing. -------
        foreach (ReflectedType rt in reflectedTypes)
        {
            if (!manifest.TryGetType(rt.MetadataName, out ManifestTypeEntry entry))
            {
                // The manifest does not declare FClass scaffolding for this
                // type: XIL2CPP owns the full emit. Record the correlation
                // (XhtProducesFClass = false); no diagnostic (concurrent
                // tools, Section 10.2 -- XHT may simply not have reflected
                // this type).
                correlated.Add(new XhtCorrelatedType(
                    CSharpTypeName: rt.MetadataName,
                    CppTypeName: null,
                    FClassSymbol: null,
                    XhtProducesFClass: false));
                continue;
            }

            string canonicalSymbol = FClassSymbolPrefix + rt.SimpleName;

            // XIL2CPP142 -- cross-tool symbol-space mismatch: XHT declares an
            // FClass symbol for the type that disagrees with XIL2CPP's
            // canonical Z_Construct_FClass_<Type>.
            if (entry.FClassSymbol is { Length: > 0 } xhtSymbol
                && !string.Equals(xhtSymbol, canonicalSymbol, StringComparison.Ordinal))
            {
                builder.AddDiagnostic(rt.Span.ToDiagnostic(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.CrossToolSymbolSpaceMismatch,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Cross-tool symbol-space mismatch for type '{0}': XHT's .gen.manifest "
                        + "declares FClass scaffolding symbol '{1}' but XIL2CPP emits "
                        + "the canonical symbol '{2}'. XHT and XIL2CPP must emit the same "
                        + "FClass symbol (XToolchainContract.html Section 10.2; "
                        + "/Documents/XIL2CPP.html Section 10.4).",
                        rt.MetadataName,
                        xhtSymbol,
                        canonicalSymbol),
                    module: unit.Pass1.ModuleName));
            }

            // XIL2CPP149 -- backing-field naming mismatch: the manifest
            // declares a backing-field name for one of the type's
            // auto-properties that differs from __BackingField_<X>.
            foreach (string autoProperty in rt.AutoPropertyNames)
            {
                if (entry.BackingFields.TryGetValue(autoProperty, out string? xhtBacking)
                    && xhtBacking is { Length: > 0 })
                {
                    string canonicalBacking = BackingFieldPrefix + autoProperty;
                    if (!string.Equals(xhtBacking, canonicalBacking, StringComparison.Ordinal))
                    {
                        builder.AddDiagnostic(rt.Span.ToDiagnostic(
                            DiagnosticSeverity.Error,
                            DiagnosticCodes.BackingFieldNamingMismatch,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Backing-field naming mismatch between XHT and XIL2CPP for property "
                                + "'{0}' on type '{1}': XHT's .gen.manifest declares backing field "
                                + "'{2}' but XIL2CPP emits the canonical '{3}'. Both tools must use "
                                + "the '__BackingField_<X>' convention (/Documents/XIL2CPP.html "
                                + "Section 10.5).",
                                autoProperty,
                                rt.MetadataName,
                                xhtBacking,
                                canonicalBacking),
                            module: unit.Pass1.ModuleName));
                    }
                }
            }

            // XIL2CPP151 -- FProperty descriptor missing: the manifest pairs
            // the type but omits an FProperty descriptor for one of the
            // type's reflected ([XProperty]) fields.
            foreach (string reflectedField in rt.ReflectedFieldNames)
            {
                if (!entry.FPropertyDescriptors.Contains(reflectedField))
                {
                    builder.AddDiagnostic(rt.Span.ToDiagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.FPropertyDescriptorMissing,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "FProperty descriptor missing from XHT-emitted reflection metadata for "
                            + "field '{0}' on type '{1}'. The .gen.manifest pairs the type but does "
                            + "not declare an FProperty descriptor for this [XProperty] field "
                            + "(/Documents/XIL2CPP.html Section 10.2).",
                            reflectedField,
                            rt.MetadataName),
                        module: unit.Pass1.ModuleName));
                }
            }

            correlated.Add(new XhtCorrelatedType(
                CSharpTypeName: rt.MetadataName,
                CppTypeName: entry.CppTypeName,
                FClassSymbol: entry.FClassSymbol ?? canonicalSymbol,
                XhtProducesFClass: true));
        }

        // --- Manifest-driven validation: every cross-language [Types] pair
        // the manifest declares must name a C# type this module declares
        // for reflection emit, else the pair is not aligned (XIL2CPP150). --
        foreach (ManifestTypeEntry entry in manifest.TypeEntries)
        {
            if (!entry.HasCrossLanguagePair)
            {
                continue;
            }
            if (!reflectedTypeNames.Contains(entry.CSharpTypeName))
            {
                builder.AddDiagnostic(new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.CrossLanguageTypePairMismatch,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Cross-language type-pair manifest mismatch: the .gen.manifest declares the "
                        + "pair (C# '{0}' / C++ '{1}') but module '{2}' does not declare a "
                        + "reflection-emit ([XClass]) C# type named '{0}'. The pair is not aligned "
                        + "with the C# source (/Documents/XIL2CPP.html Section 10.3).",
                        entry.CSharpTypeName,
                        entry.CppTypeName ?? "<none>",
                        unit.Pass1.ModuleName),
                    File: manifestPath,
                    Module: unit.Pass1.ModuleName));
            }
        }

        correlated.Sort((a, b) => StringComparer.Ordinal.Compare(a.CSharpTypeName, b.CSharpTypeName));

        builder.SetSingleton(new XhtCorrelationTable(
            ManifestPath: manifestPath,
            ManifestFound: true,
            CorrelatedTypes: correlated));
    }

    // =================================================================
    // C#-side reflection-type collection.
    // =================================================================

    /// <summary>
    /// A C# type the module declares for reflection emit (an
    /// <c>[XClass]</c>-marked declared type), with its reflected fields and
    /// auto-property names and the declaration span for diagnostic anchoring.
    /// </summary>
    private sealed record ReflectedType(
        string MetadataName,
        string SimpleName,
        IReadOnlyList<string> AutoPropertyNames,
        IReadOnlyList<string> ReflectedFieldNames,
        SourceSpan Span);

    private static IReadOnlyList<ReflectedType> CollectReflectedTypes(NormalizedUnit unit)
    {
        List<ReflectedType> result = new();
        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol)
            in AnalyzerHelpers.EnumerateTypeDeclarations(unit))
        {
            if (!HasAttribute(symbol, XClassAttributeMetadataName))
            {
                continue;
            }

            // Auto-property names: every property with an accessor list whose
            // accessors are all bodiless (the C# auto-property shape XHT/
            // XIL2CPP synthesize a __BackingField_<X> for).
            List<string> autoProps = new();
            // Reflected field names: every [XProperty]-marked field member.
            List<string> reflectedFields = new();

            foreach (ISymbol member in symbol.GetMembers())
            {
                switch (member)
                {
                    case IPropertySymbol prop when IsAutoProperty(prop):
                        autoProps.Add(prop.Name);
                        break;
                    case IFieldSymbol field
                        when !field.IsImplicitlyDeclared
                            && HasAttribute(field, XPropertyAttributeMetadataName):
                        reflectedFields.Add(field.Name);
                        break;
                }
            }

            autoProps.Sort(StringComparer.Ordinal);
            reflectedFields.Sort(StringComparer.Ordinal);

            result.Add(new ReflectedType(
                MetadataName: GetQualifiedMetadataName(symbol),
                SimpleName: symbol.Name,
                AutoPropertyNames: autoProps,
                ReflectedFieldNames: reflectedFields,
                Span: ToSpan(decl)));
        }

        return result;
    }

    private static bool IsAutoProperty(IPropertySymbol prop)
    {
        // A C# auto-property has accessor declarations with no body and is
        // not abstract / extern. Detect via the syntax: an accessor with no
        // Body and no ExpressionBody. (The symbol API has no direct
        // "is auto-property" flag; the syntax is authoritative here.)
        if (prop.IsAbstract || prop.IsExtern)
        {
            return false;
        }
        foreach (SyntaxReference reference in prop.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is PropertyDeclarationSyntax pds
                && pds.AccessorList is { } accessors)
            {
                bool anyAccessor = false;
                foreach (AccessorDeclarationSyntax accessor in accessors.Accessors)
                {
                    anyAccessor = true;
                    if (accessor.Body is not null || accessor.ExpressionBody is not null)
                    {
                        return false;
                    }
                }
                if (anyAccessor)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeMetadataName)
    {
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            INamedTypeSymbol? attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }
            if (attrClass.MetadataName == attributeMetadataName
                && attrClass.ContainingNamespace is { IsGlobalNamespace: false } ns
                && ns.ToDisplayString() == XPactAttributeNamespace)
            {
                return true;
            }
        }
        return false;
    }

    private static string GetQualifiedMetadataName(INamedTypeSymbol symbol)
    {
        // Fully-qualified-but-without-global:: form so the join key matches
        // the manifest's "Namespace.Type" convention. Use the namespace
        // display string + the metadata simple name.
        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            return ns.ToDisplayString() + "." + symbol.Name;
        }
        return symbol.Name;
    }

    private static SourceSpan ToSpan(TypeDeclarationSyntax decl)
    {
        FileLinePositionSpan flp = decl.Identifier.GetLocation().GetLineSpan();
        // Roslyn line/character are 0-based; SourceSpan is 1-based.
        int line = flp.StartLinePosition.Line + 1;
        int col = flp.StartLinePosition.Character + 1;
        return SourceSpan.Point(decl.SyntaxTree.FilePath, line, col);
    }

    // =================================================================
    // Manifest-path derivation (deterministic; from source layout only).
    // =================================================================

    private static string? DeriveManifestPath(NormalizedUnit unit)
    {
        string moduleName = unit.Pass1.ModuleName;
        foreach (ModuleParser.ParsedFile parsed in unit.Pass1.ParsedFiles)
        {
            string? repoRoot = FindRepoRoot(parsed.AbsolutePath);
            if (repoRoot is null)
            {
                continue;
            }
            // Canonical XHT intermediate layout mirroring
            // TranspileModuleMode.ResolveIntermediateRoot:
            //   <repo>/Intermediate/Build/XHT/<Module>/<Module>.gen.manifest
            return Path.Combine(
                repoRoot,
                "Intermediate", "Build", "XHT", moduleName,
                moduleName + ".gen.manifest");
        }
        return null;
    }

    private static string? FindRepoRoot(string sourceFilePath)
    {
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        DirectoryInfo? cursor = dir is null ? null : new DirectoryInfo(dir);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, "Engine")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        return null;
    }

    // =================================================================
    // Self-contained, lenient manifest reader.
    // =================================================================

    /// <summary>
    /// One parsed type entry: the C# type name (join key), its optional
    /// cross-language C++ pair, its FClass scaffolding symbol, the set of
    /// FProperty descriptor field names, and the auto-property -&gt;
    /// backing-field map the manifest declares.
    /// </summary>
    private sealed class ManifestTypeEntry
    {
        public ManifestTypeEntry(string csharpTypeName)
        {
            CSharpTypeName = csharpTypeName;
        }

        public string CSharpTypeName { get; }
        public string? CppTypeName { get; set; }
        public string? FClassSymbol { get; set; }
        public HashSet<string> FPropertyDescriptors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> BackingFields { get; } = new(StringComparer.Ordinal);

        public bool HasCrossLanguagePair => CppTypeName is { Length: > 0 };
    }

    /// <summary>
    /// The parsed manifest's correlation-relevant content: the
    /// per-C#-type entries keyed by name.
    /// </summary>
    private sealed class ParsedManifest
    {
        private readonly Dictionary<string, ManifestTypeEntry> _byName =
            new(StringComparer.Ordinal);
        private readonly List<ManifestTypeEntry> _ordered = new();

        public IReadOnlyList<ManifestTypeEntry> TypeEntries => _ordered;

        public ManifestTypeEntry GetOrAdd(string csharpTypeName)
        {
            if (!_byName.TryGetValue(csharpTypeName, out ManifestTypeEntry? entry))
            {
                entry = new ManifestTypeEntry(csharpTypeName);
                _byName.Add(csharpTypeName, entry);
                _ordered.Add(entry);
            }
            return entry;
        }

        public bool TryGetType(string csharpTypeName, out ManifestTypeEntry entry)
        {
            if (_byName.TryGetValue(csharpTypeName, out ManifestTypeEntry? found))
            {
                entry = found;
                return true;
            }
            entry = null!;
            return false;
        }
    }

    private static ParsedManifest? TryReadManifest(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return null;
        }

        string text;
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }
            text = File.ReadAllText(manifestPath, System.Text.Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return ParseManifestText(text);
        }
        catch (Exception)
        {
            // Lenient: any malformed content -> no correlations, no crash.
            return null;
        }
    }

    /// <summary>
    /// Parse the correlation-relevant sections of a <c>.gen.manifest</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes the forward-compatible <c>[Types]</c> section
    /// (/Documents/XIL2CPP.html Section 10.3) whose lines have the shape
    /// <c>CSharpType,CppType,FClassSymbol,FProperties,BackingFields</c>
    /// where:
    /// <list type="bullet">
    ///   <item><description><c>CSharpType</c> -- the C# type metadata name (join key; required).</description></item>
    ///   <item><description><c>CppType</c> -- the paired C++ type name (empty = no pair).</description></item>
    ///   <item><description><c>FClassSymbol</c> -- the XHT FClass scaffolding symbol (empty = none).</description></item>
    ///   <item><description><c>FProperties</c> -- semicolon-separated FProperty descriptor field names (empty = none).</description></item>
    ///   <item><description><c>BackingFields</c> -- semicolon-separated <c>Property=BackingFieldName</c> pairs (empty = none).</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Falls back to deriving the FClass scaffolding set from the
    /// <c>[Generated]</c> section's <c>&lt;Type&gt;.gen.h</c> file base names
    /// (the form XHT's current emitter writes) when no <c>[Types]</c> section
    /// is present. The <c>[Generated]</c>-derived entries carry no
    /// cross-language pair / property metadata (XHT's current schema does not
    /// emit it), so they correlate the FClass-scaffolding ownership only.
    /// </para>
    /// </remarks>
    private static ParsedManifest ParseManifestText(string text)
    {
        ParsedManifest manifest = new();
        bool sawTypesSection = false;
        List<string> generatedBaseNames = new();

        string currentSection = string.Empty;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal)
                && line.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = line;
                continue;
            }

            switch (currentSection)
            {
                case "[Types]":
                    sawTypesSection = true;
                    ParseTypeLine(manifest, line);
                    break;
                case "[Generated]":
                    CollectGeneratedFClassBase(line, generatedBaseNames);
                    break;
                default:
                    // [Metadata] / [Inputs] / [Diagnostics] / [End] / unknown
                    // sections carry no correlation data; skip.
                    break;
            }
        }

        if (!sawTypesSection)
        {
            // Fallback: derive FClass-scaffolding ownership from the
            // <Type>.gen.h base names XHT's current emitter produces.
            foreach (string baseName in generatedBaseNames)
            {
                ManifestTypeEntry entry = manifest.GetOrAdd(baseName);
                entry.FClassSymbol = FClassSymbolPrefix + SimpleNameOf(baseName);
            }
        }

        return manifest;
    }

    private static void ParseTypeLine(ParsedManifest manifest, string line)
    {
        // CSharpType,CppType,FClassSymbol,FProperties,BackingFields
        string[] fields = line.Split(',');
        string csharpType = fields[0].Trim();
        if (csharpType.Length == 0)
        {
            return;
        }

        ManifestTypeEntry entry = manifest.GetOrAdd(csharpType);

        if (fields.Length > 1 && fields[1].Trim() is { Length: > 0 } cpp)
        {
            entry.CppTypeName = cpp;
        }
        if (fields.Length > 2 && fields[2].Trim() is { Length: > 0 } fclass)
        {
            entry.FClassSymbol = fclass;
        }
        if (fields.Length > 3)
        {
            foreach (string prop in fields[3].Split(';'))
            {
                string trimmed = prop.Trim();
                if (trimmed.Length > 0)
                {
                    entry.FPropertyDescriptors.Add(trimmed);
                }
            }
        }
        if (fields.Length > 4)
        {
            foreach (string pair in fields[4].Split(';'))
            {
                string trimmed = pair.Trim();
                int eq = trimmed.IndexOf('=');
                if (eq > 0 && eq < trimmed.Length - 1)
                {
                    string propName = trimmed[..eq].Trim();
                    string backing = trimmed[(eq + 1)..].Trim();
                    if (propName.Length > 0 && backing.Length > 0)
                    {
                        entry.BackingFields[propName] = backing;
                    }
                }
            }
        }
    }

    private static void CollectGeneratedFClassBase(string line, List<string> baseNames)
    {
        // [Generated] lines are "RelativePath,ContentHash16" per XHT's
        // GenManifestWriter. We want the <Type>.gen.h base names (the FClass
        // scaffolding header per type).
        int comma = line.IndexOf(',');
        string pathField = comma < 0 ? line : line[..comma];
        string path = pathField.Trim().Replace('\\', '/');
        string fileName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        const string suffix = ".gen.h";
        if (fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            string typeName = fileName[..^suffix.Length];
            if (typeName.Length > 0 && !baseNames.Contains(typeName))
            {
                baseNames.Add(typeName);
            }
        }
    }

    private static string SimpleNameOf(string qualifiedName)
    {
        int dot = qualifiedName.LastIndexOf('.');
        return dot < 0 ? qualifiedName : qualifiedName[(dot + 1)..];
    }
}
