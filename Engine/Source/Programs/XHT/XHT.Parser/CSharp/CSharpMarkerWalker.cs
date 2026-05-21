// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;
using XhtDiagnosticSeverity = Simgenics.XPact.XHT.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XHT.Parser.CSharp;

/// <summary>
/// Roslyn-based reflection-marker walker for one C# source file per
/// <c>/Documents/XHT.html</c> Rev 7 Section 3.2 (Roslyn-based C# parser)
/// + Section 3.3 (cross-language considerations) + Section 7 (markers).
/// The C# peer of <c>Cpp.CppMarkerScanner</c>: walks the syntax tree,
/// recognises the X-attribute family (<c>[XClass]</c>, <c>[XStruct]</c>,
/// <c>[XEnum]</c>, <c>[XInterface]</c>, <c>[XDelegate]</c>,
/// <c>[XFunction]</c>, <c>[XProperty]</c>, <c>[XParam]</c>,
/// <c>[XMeta]</c>), extracts specifier arguments via
/// <see cref="CSharpSpecifierExtractor"/>, and emits corresponding
/// <see cref="XhtTypeBase"/>-derived records into the caller-supplied
/// <see cref="SymbolTable"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parse-only.</b> Per Section 3.2: Roslyn is used only as a syntax-
/// tree producer. No <see cref="SemanticModel"/>, no
/// <c>CSharpCompilation</c>, no analyzer hosting. The walker reads
/// attribute names literally; symbol resolution happens in Phase 1d's
/// resolver against XHT's own symbol table.
/// </para>
/// <para>
/// <b>Attribute name matching.</b> C# attribute names are case-sensitive
/// per the language spec. <c>[XClass]</c> and <c>[xclass]</c> are
/// distinct in C#; the walker matches case-sensitively. The X-attribute
/// classes themselves are not declared anywhere in XHT (the walker
/// reads the bare name string only, not a resolved type), but the
/// convention is the PascalCase forms enumerated in Section 7.1's table.
/// </para>
/// <para>
/// <b>Cross-language registration.</b> The walker pushes its emitted
/// types into the <em>same</em> <see cref="SymbolTable"/> the C++ side
/// (<c>Cpp.CppMarkerScanner</c>) populates. The table's caseless-key
/// normalisation (<see cref="XhtTypeBase.CaselessKey"/>) lets a C++
/// <c>XValve</c> and a C# <c>XValve</c> collide on the engine name
/// <c>"xvalve"</c>; the resolver disambiguates by language tag in
/// Phase 1d. (Per Round-2: XPact engine source uses only the permanent
/// <c>X</c> prefix; legacy A / U / I / F prefix-stripping is gone.)
/// </para>
/// <para>
/// <b>Partial-class handling.</b> A C# <c>partial class Foo</c> can
/// span multiple source files. Phase 1c.2b emits one
/// <see cref="XhtClass"/> per partial declaration (each registered in
/// the SymbolTable, distinguished only by being at different source
/// spans). Merging across partials is deferred to Phase 1d's resolver,
/// which detects same <see cref="XhtTypeBase.FullyQualifiedName"/> +
/// same <see cref="XhtTypeBase.Language"/> in <c>SymbolTable.AllTypes</c>
/// and folds them. <see cref="XhtClass"/> in <c>XHT.AST</c> is frozen
/// for Phase 1c.2b; an <c>IsPartial</c> flag is intentionally not added.
/// </para>
/// <para>
/// <b>Thread safety.</b> Two walker instances on two threads share no
/// state; the underlying <see cref="SymbolTable"/> is itself thread-safe
/// (concurrent dictionary). The walker constructs its own private
/// diagnostic list and Roslyn syntax tree.
/// </para>
/// </remarks>
public sealed class CSharpMarkerWalker
{
    /// <summary>Diagnostic code: caseless symbol-table collision.</summary>
    public const string DiagDuplicateType = "XHT040";

    /// <summary>Diagnostic code: marker followed by declaration we don't recognise.</summary>
    public const string DiagMalformedMarkerDeclaration = "XHT115";

    /// <summary>
    /// Diagnostic code: a generic-attribute form is not supported by
    /// Phase 1 XHT (e.g. <c>[XClass&lt;T&gt;]</c>). Emitted at attribute-
    /// match time so the type-argument silent-drop is no longer
    /// invisible. Per C2 audit.
    /// </summary>
    public const string DiagGenericAttributeUnsupported = "XHT044";

    /// <summary>
    /// Diagnostic code: the same X-attribute appears multiple times on
    /// the same target (e.g. <c>[XClass, XClass]</c>). The first
    /// instance is used; subsequent instances are ignored with this
    /// warning. Per C2 audit.
    /// </summary>
    public const string DiagDuplicateMarkerAttribute = "XHT045";

    private readonly string _sourcePath;
    private readonly string _sourceText;
    private readonly string _moduleName;
    private readonly ISpecifierRegistry _registry;
    private readonly SymbolTable _symbolTable;
    private readonly List<DiagnosticRecord> _diagnostics = new();
    private readonly List<XhtClass> _extraPartials = new();

    /// <summary>
    /// Construct a walker over one C# source file. Construction does not
    /// walk; call <see cref="Walk"/> to traverse the syntax tree.
    /// </summary>
    /// <param name="sourcePath">Absolute source-file path. Must not be null.</param>
    /// <param name="sourceText">Source text (already UTF-8-decoded to .NET string). Must not be null.</param>
    /// <param name="moduleName">Owning module name from XBT manifest. Must not be null.</param>
    /// <param name="specifierRegistry">Specifier registry for context validation. Must not be null.</param>
    /// <param name="symbolTable">Caseless symbol table to populate. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public CSharpMarkerWalker(
        string sourcePath,
        string sourceText,
        string moduleName,
        ISpecifierRegistry specifierRegistry,
        SymbolTable symbolTable)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(moduleName);
        ArgumentNullException.ThrowIfNull(specifierRegistry);
        ArgumentNullException.ThrowIfNull(symbolTable);

        _sourcePath = sourcePath;
        _sourceText = sourceText;
        _moduleName = moduleName;
        _registry = specifierRegistry;
        _symbolTable = symbolTable;
    }

    /// <summary>
    /// Diagnostics accumulated during the walk. Includes specifier-
    /// extraction diagnostics (XHT110/111/114), marker-context errors
    /// (XHT115), and caseless symbol-table collisions (XHT040). The
    /// list is appended in walk order.
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics => _diagnostics;

    /// <summary>
    /// Extra partial-class declarations the walker collected but could
    /// not register in the <see cref="SymbolTable"/> because the
    /// canonical (first-registered) entry already occupied the
    /// caseless engine-name key. The resolver's
    /// <c>StepResolvePairings</c> phase consults this list to drive
    /// the partial-class merge per C3 audit (XHT.html Section 3.3).
    /// </summary>
    public IReadOnlyList<XhtClass> ExtraPartials => _extraPartials;

    /// <summary>
    /// Walk the source and emit AST nodes into the
    /// <see cref="SymbolTable"/>. Returns the list of top-level
    /// reflected types emitted (in source-discovery order). Nested types
    /// are also pushed into the symbol table but do not appear in the
    /// returned list.
    /// </summary>
    /// <returns>The top-level reflected types emitted; never null.</returns>
    public IReadOnlyList<XhtTypeBase> Walk()
    {
        // Parse the source text into a Roslyn syntax tree.
        CSharpParseOptions options = new CSharpParseOptions(
            languageVersion: LanguageVersion.CSharp12,
            documentationMode: DocumentationMode.Parse,
            kind: SourceCodeKind.Regular);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            text: _sourceText,
            options: options,
            path: _sourcePath);

        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();

        // Walk the tree with our custom visitor.
        List<XhtTypeBase> roots = new();
        XhtAttributeWalker walker = new(this, roots);
        walker.Visit(root);
        return roots;
    }

    // =================================================================
    // Visitor / walker.
    // =================================================================

    private sealed class XhtAttributeWalker : CSharpSyntaxWalker
    {
        private readonly CSharpMarkerWalker _outer;
        private readonly List<XhtTypeBase> _roots;
        private readonly List<string> _namespaceStack = new();
        private readonly List<string> _typeStack = new();

        // For each nesting level: the in-progress builder slot. We use
        // a simple list-of-builders so XFunction / XProperty / etc.
        // visit-time hooks can attach to the closest enclosing builder.
        private readonly List<TypeBuilder> _typeBuilders = new();

        public XhtAttributeWalker(CSharpMarkerWalker outer, List<XhtTypeBase> roots)
            // Visit into trivia? No -- we don't need trivia for attribute
            // discovery in this phase. (Section 6.4's tooltip extraction
            // is Phase 1d work.)
            : base(SyntaxWalkerDepth.Node)
        {
            _outer = outer;
            _roots = roots;
        }

        // ----- Namespace handling --------------------------------------

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            string nsName = node.Name.ToString();
            _namespaceStack.Add(nsName);
            base.VisitNamespaceDeclaration(node);
            _namespaceStack.RemoveAt(_namespaceStack.Count - 1);
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            // 'namespace X.Y;' form -- pushes for the rest of the file.
            string nsName = node.Name.ToString();
            _namespaceStack.Add(nsName);
            base.VisitFileScopedNamespaceDeclaration(node);
            // No pop -- file-scoped namespace owns the rest of the file.
            // CSharpSyntaxWalker's default traversal returns from this
            // visit naturally; the namespace remains on the stack but
            // there are no more declarations to visit.
        }

        // ----- Class / Struct / Interface / Enum / Delegate ------------

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XClass");
            if (marker is not null)
            {
                HandleXClass(node, marker);
            }
            else
            {
                // Even without a [XClass], the type-stack must track
                // class nesting so an inner [XClass] inside an
                // unmarked outer class records the correct OuterName.
                _typeStack.Add(node.Identifier.Text);
                _typeBuilders.Add(TypeBuilder.UnreflectedScope(node.Identifier.Text));
                base.VisitClassDeclaration(node);
                _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
                _typeStack.RemoveAt(_typeStack.Count - 1);
                return;
            }
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XStruct");
            if (marker is not null)
            {
                HandleXStruct(node, marker);
            }
            else
            {
                _typeStack.Add(node.Identifier.Text);
                _typeBuilders.Add(TypeBuilder.UnreflectedScope(node.Identifier.Text));
                base.VisitStructDeclaration(node);
                _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
                _typeStack.RemoveAt(_typeStack.Count - 1);
            }
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XInterface");
            if (marker is not null)
            {
                HandleXInterface(node, marker);
            }
            else
            {
                _typeStack.Add(node.Identifier.Text);
                _typeBuilders.Add(TypeBuilder.UnreflectedScope(node.Identifier.Text));
                base.VisitInterfaceDeclaration(node);
                _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
                _typeStack.RemoveAt(_typeStack.Count - 1);
            }
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XEnum");
            if (marker is not null)
            {
                HandleXEnum(node, marker);
            }
            // Don't descend into enum bodies as named types; enums hold
            // members handled by HandleXEnum directly.
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XDelegate");
            if (marker is not null)
            {
                HandleXDelegate(node, marker);
            }
        }

        // ----- Function / Property -------------------------------------

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XFunction");
            if (marker is not null)
            {
                HandleXFunction(node, marker);
            }
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            AttributeSyntax? marker = FindXAttribute(node.AttributeLists, "XProperty");
            if (marker is not null)
            {
                HandleXProperty(node, marker);
            }
        }

        // ----- Handlers ------------------------------------------------

        private void HandleXClass(ClassDeclarationSyntax node, AttributeSyntax marker)
        {
            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker, SpecifierContext.Class, _outer._registry, _outer._sourcePath, _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);

            // Base list: BaseList.Types is in source order. The first
            // base is conventionally the super-class; subsequent bases
            // are interfaces. C# (unlike C++) does not let you tell
            // class-vs-interface from the source alone without semantic
            // info; we use UE-precedent heuristic (interfaces have
            // names starting with 'I' followed by an uppercase letter).
            string? superId = null;
            List<string> interfaces = new();
            if (node.BaseList is not null)
            {
                bool first = true;
                foreach (BaseTypeSyntax bt in node.BaseList.Types)
                {
                    string typeText = bt.Type.ToString();
                    if (first)
                    {
                        first = false;
                        if (LooksLikeInterfaceName(typeText))
                        {
                            interfaces.Add(typeText);
                        }
                        else
                        {
                            superId = typeText;
                        }
                    }
                    else
                    {
                        interfaces.Add(typeText);
                    }
                }
            }

            string? withinId = ExtractFirstValue(specifiers, "Within");
            string fqn = ComposeFqn(name);
            string? outerName = _typeStack.Count > 0 ? _typeStack[^1] : null;

            // Detect the C# 'partial' modifier. The resolver merges
            // partials with the same FullyQualifiedName + Language per
            // C3 audit (XHT.html Section 3.3).
            bool isPartial = false;
            foreach (SyntaxToken m in node.Modifiers)
            {
                if (m.IsKind(SyntaxKind.PartialKeyword))
                {
                    isPartial = true;
                    break;
                }
            }

            // Per /Documents/XHT.html Rev 7 Section 7.5: C# attribute
            // anchored types always have generated body in XPact's model
            // (no XGENERATED_BODY equivalent is needed because Roslyn
            // gives the walker the target directly). Set
            // HasGeneratedBody=true for C# classes.
            // RequiredAPIMacroName is null on the C# side: the XIL2CPP
            // transpiler emits the corresponding _API-decorated symbol
            // (Section 7.4 RequiredAPI source rule).
            XhtClass cls = new(
                Name: name,
                FullyQualifiedName: fqn,
                OuterName: outerName,
                ModuleName: _outer._moduleName,
                Language: Language.CSharp,
                Span: span,
                Specifiers: specifiers,
                SuperIdentifier: superId,
                Super: null,
                Functions: new List<XhtFunction>(),
                Properties: new List<XhtProperty>(),
                InterfaceIdentifiers: interfaces,
                Interfaces: Array.Empty<XhtInterface>(),
                WithinIdentifier: withinId,
                WithinClass: null,
                RequiredAPIMacroName: null,
                HasGeneratedBody: true,
                IsPartial: isPartial,
                PartialSourcePaths: new[] { span.SourceFilePath });

            // Push a TypeBuilder so XFunction / XProperty visits inside
            // the class attach to this entry. We finalise on pop.
            _typeStack.Add(name);
            TypeBuilder tb = TypeBuilder.ForClass(name, cls);
            _typeBuilders.Add(tb);

            // Descend into members.
            base.VisitClassDeclaration(node);

            // Pop + finalise.
            _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
            _typeStack.RemoveAt(_typeStack.Count - 1);
            XhtClass finalisedCls = cls with
            {
                Functions = tb.Functions,
                Properties = tb.Properties,
            };
            _outer.TryRegister(finalisedCls, _roots, isRoot: !HasEnclosingReflectedType());
        }

        private void HandleXStruct(StructDeclarationSyntax node, AttributeSyntax marker)
        {
            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker, SpecifierContext.Struct, _outer._registry, _outer._sourcePath, _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);

            string? superId = null;
            if (node.BaseList is { Types.Count: > 0 } baseList)
            {
                // Rare in C# but possible -- e.g. struct implements an
                // interface (still first entry).
                superId = baseList.Types[0].Type.ToString();
            }

            string fqn = ComposeFqn(name);
            string? outerName = _typeStack.Count > 0 ? _typeStack[^1] : null;

            XhtStruct st = new(
                Name: name,
                FullyQualifiedName: fqn,
                OuterName: outerName,
                ModuleName: _outer._moduleName,
                Language: Language.CSharp,
                Span: span,
                Specifiers: specifiers,
                SuperIdentifier: superId,
                Super: null,
                Properties: new List<XhtProperty>(),
                IsFastArraySerializer: false);

            _typeStack.Add(name);
            TypeBuilder tb = TypeBuilder.ForStruct(name, st);
            _typeBuilders.Add(tb);

            base.VisitStructDeclaration(node);

            _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
            _typeStack.RemoveAt(_typeStack.Count - 1);
            XhtStruct finalisedSt = st with { Properties = tb.Properties };
            _outer.TryRegister(finalisedSt, _roots, isRoot: !HasEnclosingReflectedType());
        }

        private void HandleXInterface(InterfaceDeclarationSyntax node, AttributeSyntax marker)
        {
            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker, SpecifierContext.Interface, _outer._registry, _outer._sourcePath, _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);

            string? superId = null;
            if (node.BaseList is { Types.Count: > 0 } baseList)
            {
                superId = baseList.Types[0].Type.ToString();
            }

            string fqn = ComposeFqn(name);
            string? outerName = _typeStack.Count > 0 ? _typeStack[^1] : null;

            XhtInterface iface = new(
                Name: name,
                FullyQualifiedName: fqn,
                OuterName: outerName,
                ModuleName: _outer._moduleName,
                Language: Language.CSharp,
                Span: span,
                Specifiers: specifiers,
                SuperIdentifier: superId,
                Super: null,
                Functions: new List<XhtFunction>());

            _typeStack.Add(name);
            TypeBuilder tb = TypeBuilder.ForInterface(name, iface);
            _typeBuilders.Add(tb);

            base.VisitInterfaceDeclaration(node);

            _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
            _typeStack.RemoveAt(_typeStack.Count - 1);
            XhtInterface finalisedIf = iface with { Functions = tb.Functions };
            _outer.TryRegister(finalisedIf, _roots, isRoot: !HasEnclosingReflectedType());
        }

        private void HandleXEnum(EnumDeclarationSyntax node, AttributeSyntax marker)
        {
            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker, SpecifierContext.Enum, _outer._registry, _outer._sourcePath, _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);

            // Underlying type (colon syntax 'enum E : int').
            string? underlying = null;
            if (node.BaseList is { Types.Count: > 0 } baseList)
            {
                underlying = baseList.Types[0].Type.ToString();
            }

            // IsFlags: either a [Flags] attribute is present alongside
            // [XEnum], or a 'Bitmask' specifier appears on the
            // [XEnum(...)] argument list (matches the C++ side which
            // detects 'Bitmask' specifier).
            bool isFlags = HasFlagsAttribute(node.AttributeLists)
                || HasSpecifier(specifiers, "Bitmask");

            // Enum members.
            List<XhtEnumValue> values = new();
            long autoCounter = 0;
            foreach (EnumMemberDeclarationSyntax mem in node.Members)
            {
                string memName = mem.Identifier.Text;
                SourceSpan memSpan = _outer.SpanFromToken(mem.Identifier);
                long memValue = autoCounter;
                if (mem.EqualsValue is { Value: ExpressionSyntax val })
                {
                    if (TryEvalIntLiteral(val, out long parsed))
                    {
                        memValue = parsed;
                    }
                }

                // XMeta specifiers on the value.
                List<Specifier> memSpecs = new();
                AttributeSyntax? meta = FindXAttribute(mem.AttributeLists, "XMeta");
                if (meta is not null)
                {
                    IReadOnlyList<Specifier> parsedMeta = CSharpSpecifierExtractor.Extract(
                        meta, SpecifierContext.EnumValue, _outer._registry, _outer._sourcePath, _outer._diagnostics);
                    memSpecs.AddRange(parsedMeta);
                }

                values.Add(new XhtEnumValue(memName, memValue, memSpecs, memSpan));
                autoCounter = memValue + 1;
            }

            string fqn = ComposeFqn(name);
            string? outerName = _typeStack.Count > 0 ? _typeStack[^1] : null;

            XhtEnum e = new(
                Name: name,
                FullyQualifiedName: fqn,
                OuterName: outerName,
                ModuleName: _outer._moduleName,
                Language: Language.CSharp,
                Span: span,
                Specifiers: specifiers,
                UnderlyingType: underlying,
                IsFlags: isFlags,
                Values: values);

            _outer.TryRegister(e, _roots, isRoot: !HasEnclosingReflectedType());
        }

        private void HandleXDelegate(DelegateDeclarationSyntax node, AttributeSyntax marker)
        {
            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker, SpecifierContext.Delegate, _outer._registry, _outer._sourcePath, _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);

            string returnType = node.ReturnType.ToString();
            List<XhtParam> parameters = new();
            foreach (ParameterSyntax p in node.ParameterList.Parameters)
            {
                parameters.Add(ParameterToXhtParam(p));
            }

            string fqn = ComposeFqn(name);
            string? outerName = _typeStack.Count > 0 ? _typeStack[^1] : null;

            // Phase 1c.2b note: C# delegate-syntax via [XDelegate] is
            // treated as single-cast (IsMulticast=false). The C# language
            // does not formally distinguish multicast / single-cast at
            // delegate-declaration time (Action / Func vs custom delegate
            // type); a future [XMulticastDelegate] attribute (parallel
            // to the C++ DECLARE_DYNAMIC_MULTICAST_DELEGATE_* macros)
            // would flip this. See XHT.html Rev 7 Section 7 marker table.
            XhtDelegate del = new(
                Name: name,
                FullyQualifiedName: fqn,
                OuterName: outerName,
                ModuleName: _outer._moduleName,
                Language: Language.CSharp,
                Span: span,
                Specifiers: specifiers,
                ReturnType: returnType,
                Parameters: parameters,
                IsMulticast: false);

            _outer.TryRegister(del, _roots, isRoot: !HasEnclosingReflectedType());
        }

        private void HandleXFunction(MethodDeclarationSyntax node, AttributeSyntax marker)
        {
            TypeBuilder? owner = ClosestReflectedBuilder();
            if (owner is null)
            {
                FileLinePositionSpan pos = marker.GetLocation().GetLineSpan();
                _outer._diagnostics.Add(new DiagnosticRecord(
                    XhtDiagnosticSeverity.Error,
                    DiagMalformedMarkerDeclaration,
                    "[XFunction] must appear on a method inside a reflected class / struct / interface.",
                    File: _outer._sourcePath,
                    Line: pos.StartLinePosition.Line + 1,
                    Column: pos.StartLinePosition.Character + 1));
                return;
            }

            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker, SpecifierContext.Function, _outer._registry, _outer._sourcePath, _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);
            string returnType = node.ReturnType.ToString();

            bool isStatic = false;
            foreach (SyntaxToken m in node.Modifiers)
            {
                if (m.IsKind(SyntaxKind.StaticKeyword)) { isStatic = true; }
            }
            // C# parser leaves IsVirtual / IsConst false per XHT.AST.XhtFunction:
            // "C#-side functions never set IsVirtual / IsConst". The
            // 'virtual' C# modifier maps to IsVirtual=false in Phase 1
            // because the C# semantics (virtual default, explicit
            // override) differ from C++'s. Phase 1d resolver may
            // reconcile.

            List<XhtParam> parameters = new();
            foreach (ParameterSyntax p in node.ParameterList.Parameters)
            {
                parameters.Add(ParameterToXhtParam(p));
            }

            XhtFunction fn = new(
                Name: name,
                ReturnType: returnType,
                Parameters: parameters,
                Specifiers: specifiers,
                IsStatic: isStatic,
                IsVirtual: false,
                IsConst: false,
                Span: span);

            owner.Functions.Add(fn);
        }

        private void HandleXProperty(PropertyDeclarationSyntax node, AttributeSyntax marker)
        {
            TypeBuilder? owner = ClosestReflectedBuilder();
            if (owner is null)
            {
                FileLinePositionSpan pos = marker.GetLocation().GetLineSpan();
                _outer._diagnostics.Add(new DiagnosticRecord(
                    XhtDiagnosticSeverity.Error,
                    DiagMalformedMarkerDeclaration,
                    "[XProperty] must appear on a property inside a reflected class / struct.",
                    File: _outer._sourcePath,
                    Line: pos.StartLinePosition.Line + 1,
                    Column: pos.StartLinePosition.Character + 1));
                return;
            }

            // PropertyMember context per Section 18.1 (C# properties are
            // class fields, not function parameters).
            IReadOnlyList<Specifier> specifiers = CSharpSpecifierExtractor.Extract(
                marker,
                SpecifierContext.Property | SpecifierContext.PropertyMember,
                _outer._registry,
                _outer._sourcePath,
                _outer._diagnostics);

            string name = node.Identifier.Text;
            SourceSpan span = _outer.SpanFromToken(node.Identifier);
            string typeStr = node.Type.ToString();

            bool isContainer = typeStr.Contains("List<", StringComparison.Ordinal)
                || typeStr.Contains("Dictionary<", StringComparison.Ordinal)
                || typeStr.Contains("HashSet<", StringComparison.Ordinal)
                || typeStr.Contains("TArray<", StringComparison.Ordinal)
                || typeStr.Contains("TMap<", StringComparison.Ordinal)
                || typeStr.Contains("TSet<", StringComparison.Ordinal);

            string? category = ExtractFirstValue(specifiers, "Category");
            string? repNotify = ExtractFirstValue(specifiers, "ReplicatedUsing");

            XhtProperty prop = new(
                Name: name,
                TypeIdentifier: typeStr,
                Specifiers: specifiers,
                IsContainer: isContainer,
                RepNotifyFunctionName: repNotify,
                Category: category,
                Span: span);

            owner.Properties.Add(prop);
        }

        private XhtParam ParameterToXhtParam(ParameterSyntax p)
        {
            string pname = p.Identifier.Text;
            string ptype = p.Type?.ToString() ?? string.Empty;
            SourceSpan span = _outer.SpanFromToken(p.Identifier);

            bool isOut = false;
            bool isRef = false;
            foreach (SyntaxToken m in p.Modifiers)
            {
                if (m.IsKind(SyntaxKind.OutKeyword)) { isOut = true; }
                else if (m.IsKind(SyntaxKind.RefKeyword)) { isRef = true; }
                else if (m.IsKind(SyntaxKind.InKeyword)) { isRef = true; }
            }

            // XParam-attached specifiers, if any.
            List<Specifier> paramSpecs = new();
            AttributeSyntax? xparam = FindXAttribute(p.AttributeLists, "XParam");
            if (xparam is not null)
            {
                IReadOnlyList<Specifier> parsed = CSharpSpecifierExtractor.Extract(
                    xparam, SpecifierContext.Param, _outer._registry, _outer._sourcePath, _outer._diagnostics);
                paramSpecs.AddRange(parsed);
                foreach (Specifier s in parsed)
                {
                    if (string.Equals(s.Key, "Out", StringComparison.OrdinalIgnoreCase)) { isOut = true; }
                    if (string.Equals(s.Key, "Ref", StringComparison.OrdinalIgnoreCase)) { isRef = true; }
                }
            }

            return new XhtParam(pname, ptype, paramSpecs, isOut, isRef, span);
        }

        // ----- Helpers -------------------------------------------------

        /// <summary>
        /// True if any builder on the type stack represents a reflected
        /// type (i.e. was constructed via ForClass / ForStruct / ForInterface,
        /// not UnreflectedScope). Used to decide whether a newly-emitted
        /// type is a 'root' (top-level under this walker's view).
        /// </summary>
        private bool HasEnclosingReflectedType()
        {
            foreach (TypeBuilder tb in _typeBuilders)
            {
                if (tb.IsReflected) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Return the closest enclosing reflected builder, or null if
        /// there is no enclosing reflected type. Used by XFunction /
        /// XProperty attachment.
        /// </summary>
        private TypeBuilder? ClosestReflectedBuilder()
        {
            for (int i = _typeBuilders.Count - 1; i >= 0; i--)
            {
                if (_typeBuilders[i].IsReflected) { return _typeBuilders[i]; }
            }
            return null;
        }

        private AttributeSyntax? FindXAttribute(SyntaxList<AttributeListSyntax> lists, string targetName)
        {
            AttributeSyntax? firstMatch = null;
            foreach (AttributeListSyntax list in lists)
            {
                foreach (AttributeSyntax attr in list.Attributes)
                {
                    string name = AttributeSimpleName(attr.Name);
                    // Case-sensitive match per C# language spec; permit
                    // the optional 'Attribute' suffix the language
                    // allows ([XClassAttribute] is equivalent to
                    // [XClass] in C#).
                    if (name == targetName || name == targetName + "Attribute")
                    {
                        // C2 audit: emit XHT044 for generic-attribute
                        // forms (e.g. [XClass<T>]) because Phase 1 XHT
                        // does not interpret the type-argument list;
                        // silently dropping it would be a correctness
                        // gap. The first non-generic occurrence is
                        // preferred when both forms appear.
                        if (attr.Name is GenericNameSyntax)
                        {
                            FileLinePositionSpan pos = attr.GetLocation().GetLineSpan();
                            _outer._diagnostics.Add(new DiagnosticRecord(
                                XhtDiagnosticSeverity.Error,
                                DiagGenericAttributeUnsupported,
                                $"Generic attribute form '[{targetName}<...>]' is not supported in Phase 1 XHT; remove the type argument or open a Phase 2 feature request.",
                                File: _outer._sourcePath,
                                Line: pos.StartLinePosition.Line + 1,
                                Column: pos.StartLinePosition.Character + 1));
                            // Skip: never accept a generic form -- we
                            // would silently lose the type argument.
                            continue;
                        }
                        if (firstMatch is null)
                        {
                            firstMatch = attr;
                        }
                        else
                        {
                            // C2 audit: duplicate [XClass]/[XClass]
                            // pattern. Warn and keep the first.
                            FileLinePositionSpan pos = attr.GetLocation().GetLineSpan();
                            _outer._diagnostics.Add(new DiagnosticRecord(
                                XhtDiagnosticSeverity.Warning,
                                DiagDuplicateMarkerAttribute,
                                $"Duplicate '[{targetName}]' marker on the same target; first occurrence wins, this instance is ignored.",
                                File: _outer._sourcePath,
                                Line: pos.StartLinePosition.Line + 1,
                                Column: pos.StartLinePosition.Character + 1));
                        }
                    }
                }
            }
            return firstMatch;
        }

        private static bool HasFlagsAttribute(SyntaxList<AttributeListSyntax> lists)
        {
            // The 'System.' / 'global::System.' qualified forms reduce
            // via AttributeSimpleName to the simple "Flags" /
            // "FlagsAttribute" leaf names, so a bare equality check on
            // the leaf is sufficient.
            foreach (AttributeListSyntax list in lists)
            {
                foreach (AttributeSyntax attr in list.Attributes)
                {
                    string name = AttributeSimpleName(attr.Name);
                    if (name == "Flags" || name == "FlagsAttribute")
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string AttributeSimpleName(NameSyntax name)
        {
            // Walk to the rightmost simple-name identifier per C2 audit
            // (XHT.html Section 3.2). Handles:
            //   - 'XClass'                       -> "XClass"
            //   - 'Reflection.XClass'            -> "XClass"
            //   - 'Sg.Reflection.XClass'         -> "XClass" (nested qualified)
            //   - 'global::Sg.Reflection.XClass' -> "XClass" (alias-qualified at the head)
            //   - 'XClass<T>'                    -> "XClass" (generic form; the
            //         caller emits a diagnostic when it sees a
            //         GenericNameSyntax because the type arg is silently
            //         dropped otherwise).
            // The recursion through QualifiedNameSyntax.Right handles
            // arbitrary nesting depth.
            return name switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                QualifiedNameSyntax qn => AttributeSimpleName(qn.Right),
                AliasQualifiedNameSyntax aq => AttributeSimpleName(aq.Name),
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => name.ToString(),
            };
        }

        private static bool LooksLikeInterfaceName(string s)
        {
            // UE / XPact convention: interfaces start with 'I' followed
            // by an uppercase letter. C# precedent is the same. This is
            // a heuristic; the resolver in Phase 1d may correct based
            // on the resolved target type's actual kind.
            if (s.Length < 2) { return false; }
            if (s[0] != 'I') { return false; }
            char c = s[1];
            return c >= 'A' && c <= 'Z';
        }

        private static string? ExtractFirstValue(IReadOnlyList<Specifier> specs, string key)
        {
            foreach (Specifier s in specs)
            {
                if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)
                    && s.Values.Count > 0)
                {
                    return s.Values[0];
                }
            }
            return null;
        }

        private static bool HasSpecifier(IReadOnlyList<Specifier> specs, string key)
        {
            foreach (Specifier s in specs)
            {
                if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private string ComposeFqn(string name)
        {
            // C# FQN uses dot separators (matches the C# language
            // convention; the C++ side uses '::'). The caseless lookup
            // is independent of the separator because CaselessKey
            // derives from the leaf name only.
            StringBuilder sb = new();
            foreach (string ns in _namespaceStack)
            {
                if (ns.Length == 0) { continue; }
                if (sb.Length > 0) { sb.Append('.'); }
                sb.Append(ns);
            }
            foreach (string typeName in _typeStack)
            {
                if (sb.Length > 0) { sb.Append('.'); }
                sb.Append(typeName);
            }
            if (sb.Length > 0) { sb.Append('.'); }
            sb.Append(name);
            return sb.ToString();
        }

        private static bool TryEvalIntLiteral(ExpressionSyntax expr, out long value)
        {
            value = 0;
            switch (expr)
            {
                case LiteralExpressionSyntax lit when lit.Token.IsKind(SyntaxKind.NumericLiteralToken):
                {
                    object? v = lit.Token.Value;
                    if (v is long l) { value = l; return true; }
                    if (v is int i) { value = i; return true; }
                    if (v is uint ui) { value = ui; return true; }
                    if (v is ulong ul) { value = (long)ul; return true; }
                    return false;
                }
                case PrefixUnaryExpressionSyntax pu when pu.OperatorToken.IsKind(SyntaxKind.MinusToken):
                {
                    if (TryEvalIntLiteral(pu.Operand, out long inner))
                    {
                        value = -inner;
                        return true;
                    }
                    return false;
                }
                case PrefixUnaryExpressionSyntax pu when pu.OperatorToken.IsKind(SyntaxKind.PlusToken):
                {
                    return TryEvalIntLiteral(pu.Operand, out value);
                }
                default:
                    return false;
            }
        }
    }

    // =================================================================
    // Symbol-table registration.
    // =================================================================

    private void TryRegister(XhtTypeBase t, List<XhtTypeBase> roots, bool isRoot)
    {
        try
        {
            _symbolTable.Register(t);
            if (isRoot) { roots.Add(t); }
        }
        catch (InvalidOperationException ex)
        {
            // Caseless-key collision. Distinguish three cases:
            //   1. Both sides are partial-class C# declarations of the
            //      same FullyQualifiedName -> legitimate C# partial-
            //      class spread. Stash in ExtraPartials so the
            //      resolver's pairings phase can merge them. Do NOT
            //      emit XHT040; the merge is correctness-preserving.
            //   2. Both sides share the caseless key but differ in
            //      FullyQualifiedName / Language / kind -> a real
            //      collision; emit XHT040.
            //   3. One side is partial, the other isn't -> the
            //      author meant for both to merge but forgot the
            //      modifier. Emit XHT040 with a clarifying message.
            XhtTypeBase? existing = _symbolTable.Lookup(t.Name);
            if (t is XhtClass incoming
                && existing is XhtClass canonical
                && incoming.Language == Language.CSharp
                && canonical.Language == Language.CSharp
                && incoming.IsPartial
                && canonical.IsPartial
                && string.Equals(incoming.FullyQualifiedName, canonical.FullyQualifiedName, System.StringComparison.Ordinal))
            {
                // Partial-class merge candidate. Stash the duplicate;
                // resolver does the union.
                _extraPartials.Add(incoming);
                if (isRoot) { roots.Add(incoming); }
                return;
            }

            _diagnostics.Add(new DiagnosticRecord(
                XhtDiagnosticSeverity.Error,
                DiagDuplicateType,
                $"Duplicate type '{t.Name}' (caseless engine-name collision): {ex.Message}",
                File: t.Span.SourceFilePath,
                Line: t.Span.Line,
                Column: t.Span.Column,
                Module: t.ModuleName));
            // Still add to roots when isRoot is true so the caller sees
            // every walker-emitted decl. The SymbolTable is the
            // authoritative dedup store; roots is per-walker output.
            if (isRoot) { roots.Add(t); }
        }
    }

    // =================================================================
    // Span helpers.
    // =================================================================

    /// <summary>
    /// Construct a <see cref="SourceSpan"/> from a Roslyn
    /// <see cref="SyntaxToken"/>. Lines and columns are 1-based per
    /// MSBuild convention; the underlying Roslyn <c>LineSpan</c> is
    /// 0-based.
    /// </summary>
    private SourceSpan SpanFromToken(SyntaxToken tok)
    {
        FileLinePositionSpan pos = tok.GetLocation().GetLineSpan();
        int line = pos.StartLinePosition.Line + 1;
        int col = pos.StartLinePosition.Character + 1;
        int length = tok.Span.Length;
        return new SourceSpan(_sourcePath, line, col, length);
    }

    // =================================================================
    // Builder slots.
    // =================================================================

    /// <summary>
    /// Per-scope mutable accumulator. Used so XFunction / XProperty
    /// visits inside an enclosing class can attach to the right type
    /// entry without needing a forward reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsReflected"/> distinguishes a reflected scope (the
    /// enclosing class has a marker so members attach) from an
    /// unreflected scope (the type is part of the nesting chain only;
    /// inner reflected types still appear as roots).
    /// </para>
    /// </remarks>
    private sealed class TypeBuilder
    {
        public string Name { get; }
        public bool IsReflected { get; }
        public List<XhtFunction> Functions { get; } = new();
        public List<XhtProperty> Properties { get; } = new();

        private TypeBuilder(string name, bool isReflected)
        {
            Name = name;
            IsReflected = isReflected;
        }

        public static TypeBuilder UnreflectedScope(string name) => new(name, false);
        public static TypeBuilder ForClass(string name, XhtClass _) => new(name, true);
        public static TypeBuilder ForStruct(string name, XhtStruct _) => new(name, true);
        public static TypeBuilder ForInterface(string name, XhtInterface _) => new(name, true);
    }
}
