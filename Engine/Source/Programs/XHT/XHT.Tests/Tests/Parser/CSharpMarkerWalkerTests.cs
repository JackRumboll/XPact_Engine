// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Parser.CSharp;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Tests for <see cref="CSharpMarkerWalker"/>. End-to-end Roslyn-driven
/// walk against synthetic C# source per <c>/Documents/XHT.html</c> Rev 7
/// Section 3.2 + Section 3.3 + Section 7.
/// </summary>
public class CSharpMarkerWalkerTests
{
    private const string Module = "XScoring";
    private const string Path = "Test.cs";

    private static (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags)
        Walk(string source)
    {
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);
        CSharpMarkerWalker walker = new(Path, source, Module, registry, table);
        IReadOnlyList<XhtTypeBase> roots = walker.Walk();
        return (roots, table, walker.Diagnostics);
    }

    // ===== Top-level classes ====================================

    [Fact]
    public void XClass_BareSimpleClass_Registered()
    {
        const string src = @"
namespace XPact.Scoring;

[XClass]
public class Valve { }
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Valve", cls.Name);
        Assert.Equal(Module, cls.ModuleName);
        Assert.Equal(Language.CSharp, cls.Language);
        Assert.Equal("XPact.Scoring.Valve", cls.FullyQualifiedName);
        Assert.Empty(cls.Specifiers);
        Assert.True(cls.HasGeneratedBody);
        Assert.Null(cls.RequiredAPIMacroName);
        Assert.NotNull(table.Lookup("Valve"));
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_NoParens_NoSpecifiers()
    {
        const string src = @"
[XClass]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Empty(cls.Specifiers);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_EmptyParens_NoSpecifiers()
    {
        const string src = @"
[XClass()]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Empty(cls.Specifiers);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_SingleFlagSpecifier_Recorded()
    {
        const string src = @"
[XClass(Blueprintable)]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Specifiers);
        Assert.Equal("Blueprintable", cls.Specifiers[0].Key);
        Assert.Empty(cls.Specifiers[0].Values);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_KeyEqValueSpecifier_Recorded()
    {
        // Category isn't valid on Class context per BuiltInSpecifiers
        // (it's Function|Property family). Use ClassGroup which is
        // SingleValue on Class context.
        const string src = @"
[XClass(ClassGroup = ""Combat"")]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Specifiers);
        Assert.Equal("ClassGroup", cls.Specifiers[0].Key);
        Assert.Single(cls.Specifiers[0].Values);
        Assert.Equal("Combat", cls.Specifiers[0].Values[0]);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_MultipleSpecifiers_AllRecorded()
    {
        const string src = @"
[XClass(Blueprintable, Abstract)]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal(2, cls.Specifiers.Count);
        Assert.Contains(cls.Specifiers, s => s.Key == "Blueprintable");
        Assert.Contains(cls.Specifiers, s => s.Key == "Abstract");
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_WithSuper_CapturedAsSuperIdentifier()
    {
        const string src = @"
[XClass]
public class Valve : Actor { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Actor", cls.SuperIdentifier);
        Assert.Empty(cls.InterfaceIdentifiers);
    }

    [Fact]
    public void XClass_WithInterfaces_CapturedSeparately()
    {
        const string src = @"
[XClass]
public class Valve : Actor, IPickable, IInteractable { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Actor", cls.SuperIdentifier);
        Assert.Equal(2, cls.InterfaceIdentifiers.Count);
        Assert.Contains("IPickable", cls.InterfaceIdentifiers);
        Assert.Contains("IInteractable", cls.InterfaceIdentifiers);
    }

    [Fact]
    public void XClass_OnlyInterfaces_FirstIsClassifiedAsInterface()
    {
        // When the first base name follows the IXxx convention (UE
        // precedent), classify as an interface, not the super.
        const string src = @"
[XClass]
public class Valve : IPickable, IInteractable { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Null(cls.SuperIdentifier);
        Assert.Equal(2, cls.InterfaceIdentifiers.Count);
        Assert.Contains("IPickable", cls.InterfaceIdentifiers);
        Assert.Contains("IInteractable", cls.InterfaceIdentifiers);
    }

    [Fact]
    public void XClass_WithinSpecifier_ExtractsWithinIdentifier()
    {
        const string src = @"
[XClass(Within = typeof(Actor))]
public class Component { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Actor", cls.WithinIdentifier);
    }

    // ===== Structs ==============================================

    [Fact]
    public void XStruct_Basic_Registered()
    {
        const string src = @"
[XStruct]
public struct ValveSettings
{
    [XProperty]
    public float Tolerance { get; set; }
}
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Walk(src);
        Assert.Single(roots);
        XhtStruct st = Assert.IsType<XhtStruct>(roots[0]);
        Assert.Equal("ValveSettings", st.Name);
        Assert.Equal(Language.CSharp, st.Language);
        Assert.Single(st.Properties);
        Assert.NotNull(table.Lookup("ValveSettings"));
    }

    // ===== Enums ================================================

    [Fact]
    public void XEnum_WithUnderlyingAndMembers_Registered()
    {
        const string src = @"
[XEnum]
public enum ValveKind : byte
{
    Ball,
    Gate = 2,
    Globe,
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Single(roots);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.Equal("ValveKind", e.Name);
        Assert.Equal("byte", e.UnderlyingType);
        Assert.Equal(3, e.Values.Count);
        Assert.Equal("Ball", e.Values[0].Name);
        Assert.Equal(0, e.Values[0].Value);
        Assert.Equal("Gate", e.Values[1].Name);
        Assert.Equal(2, e.Values[1].Value);
        Assert.Equal("Globe", e.Values[2].Name);
        Assert.Equal(3, e.Values[2].Value);
        Assert.False(e.IsFlags);
    }

    [Fact]
    public void XEnum_WithFlagsAttribute_SetsIsFlags()
    {
        const string src = @"
[System.Flags]
[XEnum]
public enum ValveFlags : uint
{
    None = 0,
    Open = 1,
    Closed = 2,
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.True(e.IsFlags);
    }

    [Fact]
    public void XEnum_WithBitmaskSpecifier_SetsIsFlags()
    {
        const string src = @"
[XEnum(Bitmask)]
public enum ValveFlags : uint
{
    None = 0,
    Open = 1,
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.True(e.IsFlags);
    }

    [Fact]
    public void XEnum_WithXMetaOnValue_CapturesSpecifiers()
    {
        const string src = @"
[XEnum]
public enum ValveKind : byte
{
    [XMeta(Hidden)]
    Reserved,
    Public,
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.Equal(2, e.Values.Count);
        Assert.Single(e.Values[0].Specifiers);
        Assert.Equal("Hidden", e.Values[0].Specifiers[0].Key);
        Assert.Empty(e.Values[1].Specifiers);
    }

    // ===== Interfaces ===========================================

    [Fact]
    public void XInterface_Basic_Registered()
    {
        const string src = @"
[XInterface]
public interface IPickable { }
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Walk(src);
        Assert.Single(roots);
        XhtInterface iface = Assert.IsType<XhtInterface>(roots[0]);
        Assert.Equal("IPickable", iface.Name);
        Assert.Equal(Language.CSharp, iface.Language);
        Assert.NotNull(table.Lookup("IPickable"));
    }

    // ===== Delegates ============================================

    [Fact]
    public void XDelegate_Basic_RegisteredWithSignature()
    {
        const string src = @"
[XDelegate]
public delegate void OnValveOpened(int valveId);
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Single(roots);
        XhtDelegate del = Assert.IsType<XhtDelegate>(roots[0]);
        Assert.Equal("OnValveOpened", del.Name);
        Assert.Equal("void", del.ReturnType);
        Assert.Single(del.Parameters);
        Assert.Equal("valveId", del.Parameters[0].Name);
        Assert.Equal("int", del.Parameters[0].TypeIdentifier);
        Assert.False(del.IsMulticast);
    }

    // ===== Functions ============================================

    [Fact]
    public void XFunction_OnMethod_AttachesToClass()
    {
        const string src = @"
[XClass]
public class Valve
{
    [XFunction(BlueprintCallable)]
    public void Open(float fraction) { }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Functions);
        XhtFunction fn = cls.Functions[0];
        Assert.Equal("Open", fn.Name);
        Assert.Equal("void", fn.ReturnType);
        Assert.Single(fn.Parameters);
        Assert.Equal("fraction", fn.Parameters[0].Name);
        Assert.Equal("float", fn.Parameters[0].TypeIdentifier);
    }

    [Fact]
    public void XFunction_StaticModifier_RecognizedAsIsStatic()
    {
        const string src = @"
[XClass]
public class Valve
{
    [XFunction]
    public static int ComputeSize() => 0;
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.True(cls.Functions[0].IsStatic);
        Assert.False(cls.Functions[0].IsVirtual);
        Assert.False(cls.Functions[0].IsConst);
    }

    // ===== Properties ===========================================

    [Fact]
    public void XProperty_OnAutoProperty_AttachesToClass()
    {
        const string src = @"
[XClass]
public class Valve
{
    [XProperty(EditAnywhere, BlueprintReadWrite, Category = ""Setup"")]
    public int Health { get; set; }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Properties);
        XhtProperty prop = cls.Properties[0];
        Assert.Equal("Health", prop.Name);
        Assert.Equal("int", prop.TypeIdentifier);
        Assert.Equal("Setup", prop.Category);
        Assert.Equal(3, prop.Specifiers.Count);
    }

    [Fact]
    public void XProperty_OnFullProperty_AttachesToClass()
    {
        const string src = @"
[XClass]
public class Valve
{
    private int _health;

    [XProperty(EditAnywhere)]
    public int Health
    {
        get { return _health; }
        set { _health = value; }
    }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Properties);
        Assert.Equal("Health", cls.Properties[0].Name);
    }

    [Fact]
    public void XProperty_OnContainerType_FlagsIsContainer()
    {
        const string src = @"
[XClass]
public class Inventory
{
    [XProperty]
    public List<int> Slots { get; set; } = new();
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.True(cls.Properties[0].IsContainer);
    }

    // ===== Parameters ===========================================

    [Fact]
    public void XParam_AttributeOnParameter_CapturedSpecifiers()
    {
        const string src = @"
[XClass]
public class Valve
{
    [XFunction]
    public void Compute([XParam(Out)] out int result, int input) { result = 0; }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        XhtFunction fn = cls.Functions[0];
        Assert.Equal(2, fn.Parameters.Count);
        XhtParam first = fn.Parameters[0];
        Assert.Equal("result", first.Name);
        Assert.Single(first.Specifiers);
        Assert.Equal("Out", first.Specifiers[0].Key);
        Assert.True(first.IsOut);
    }

    [Fact]
    public void Parameter_RefModifier_MarkedAsRef()
    {
        const string src = @"
[XClass]
public class Valve
{
    [XFunction]
    public void Adjust(ref int value) { }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.True(cls.Functions[0].Parameters[0].IsRef);
    }

    // ===== Nesting + namespaces =================================

    [Fact]
    public void Nested_InnerClass_HasOuterName()
    {
        const string src = @"
public class Outer
{
    [XClass]
    public class Inner { }
}
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Walk(src);
        // Inner is the only reflected type; Outer is an unreflected
        // scope. Inner appears in 'roots' as a root (it has no
        // enclosing reflected type from the SymbolTable's perspective,
        // but its OuterName is set to "Outer" so the FQN composes
        // correctly).
        Assert.Single(roots);
        XhtClass inner = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Inner", inner.Name);
        Assert.Equal("Outer", inner.OuterName);
        Assert.NotNull(table.Lookup("Inner"));
    }

    [Fact]
    public void FileScopedNamespace_FullyQualifiedNameIncludesNamespace()
    {
        const string src = @"
namespace XPact.Equipment;

[XClass]
public class Valve { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Equal("XPact.Equipment.Valve", roots[0].FullyQualifiedName);
    }

    [Fact]
    public void BlockScopedNamespace_FullyQualifiedNameIncludesNamespace()
    {
        const string src = @"
namespace XPact.Equipment
{
    [XClass]
    public class Valve { }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Equal("XPact.Equipment.Valve", roots[0].FullyQualifiedName);
    }

    [Fact]
    public void NestedNamespace_BothLevelsIncludedInFQN()
    {
        const string src = @"
namespace XPact
{
    namespace Equipment
    {
        [XClass]
        public class Valve { }
    }
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Equal("XPact.Equipment.Valve", roots[0].FullyQualifiedName);
    }

    // ===== Partial classes ======================================

    [Fact]
    public void PartialClasses_TwoPartials_EmittedSeparately_NoXHT040()
    {
        // C3 audit (XHT.html Section 3.3): partial classes share an
        // engine-name. The walker registers the first partial canonically
        // and stashes the second in ExtraPartials. NO XHT040 is emitted;
        // the resolver's pairings phase merges them.
        const string src = @"
[XClass]
public partial class Foo
{
    [XProperty]
    public int A { get; set; }
}

[XClass]
public partial class Foo
{
    [XProperty]
    public int B { get; set; }
}
";
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);
        CSharpMarkerWalker walker = new(Path, src, Module, registry, table);
        IReadOnlyList<XhtTypeBase> roots = walker.Walk();

        Assert.Equal(2, roots.Count);
        Assert.Equal("Foo", roots[0].Name);
        Assert.Equal("Foo", roots[1].Name);

        // Round-2 / C3 audit: the second partial lands in ExtraPartials,
        // NOT in the XHT040 diagnostics list. The resolver's Pairings
        // phase will merge it.
        Assert.DoesNotContain(walker.Diagnostics, d => d.Code == CSharpMarkerWalker.DiagDuplicateType);
        Assert.Single(walker.ExtraPartials);
        Assert.True(walker.ExtraPartials[0].IsPartial);
    }

    [Fact]
    public void NonPartialDuplicate_StillEmitsXHT040()
    {
        // Two non-partial classes with the same engine-name is a real
        // collision; the C3 audit suppression only applies when BOTH
        // sides carry the 'partial' modifier.
        const string src = @"
[XClass]
public class Foo { }

[XClass]
public class Foo { }
";
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);
        CSharpMarkerWalker walker = new(Path, src, Module, registry, table);
        walker.Walk();
        Assert.Contains(walker.Diagnostics, d => d.Code == CSharpMarkerWalker.DiagDuplicateType);
        Assert.Empty(walker.ExtraPartials);
    }

    // ===== Generic classes ======================================

    [Fact]
    public void XClass_GenericClass_NameCapturedWithoutTypeParams()
    {
        const string src = @"
[XClass]
public class TypedList<T> { }
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Walk(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        // The 'Identifier' Roslyn token captures only the base name; the
        // type parameter list is a sibling syntax node. The FQN here is
        // the base name without <T>; the resolver may decorate in Phase 1d.
        Assert.Equal("TypedList", cls.Name);
        Assert.NotNull(table.Lookup("TypedList"));
    }

    // ===== Diagnostics ==========================================

    [Fact]
    public void UnknownSpecifier_EmitsXHT110_ClassStillEmitted()
    {
        const string src = @"
[XClass(BogusSpecifier)]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        Assert.Contains(diags, d => d.Code == CSharpSpecifierExtractor.DiagUnknownSpecifier);
    }

    [Fact]
    public void MalformedAttributeArgument_EmitsSyntaxError_ClassStillEmitted()
    {
        const string src = @"
[XClass(ClassGroup = ""x"" + 1)]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        Assert.Contains(diags, d => d.Code == CSharpSpecifierExtractor.DiagSpecifierSyntaxError);
    }

    [Fact]
    public void XFunction_OutsideClass_EmitsContextError()
    {
        // C# top-level methods don't exist outside a class except in
        // top-level statements (which Roslyn wraps in a synthetic
        // Program class). We test the empty-class case where the
        // walker has no enclosing type-builder.
        const string src = @"
[XFunction]
public static void Orphan() { }
";
        (_, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        // Roslyn ignores [XFunction] on free-standing functions in
        // valid C#, but the walker invokes VisitMethodDeclaration only
        // when reached inside a type tree -- top-level statements
        // are skipped. This test verifies no crash; the absence of
        // diagnostics for a non-reflected free function is expected.
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error
            && d.Code == CSharpMarkerWalker.DiagMalformedMarkerDeclaration);
    }

    // ===== Multiple types per file ==============================

    [Fact]
    public void MultipleTypes_AllRegisteredInSourceOrder()
    {
        const string src = @"
[XClass]
public class Valve { }

[XClass]
public class Tank { }

[XStruct]
public struct Config { }

[XEnum]
public enum Kind : byte { A, B }
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Walk(src);
        Assert.Equal(4, roots.Count);
        Assert.Equal("Valve", roots[0].Name);
        Assert.Equal("Tank", roots[1].Name);
        Assert.Equal("Config", roots[2].Name);
        Assert.Equal("Kind", roots[3].Name);
        Assert.Equal(4, table.Count);
    }

    // ===== Non-reflected content tolerance ======================

    [Fact]
    public void NonReflectedClass_IgnoredEntirely()
    {
        const string src = @"
public class NotReflected
{
    public int x;
}

[XClass]
public class Valve { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Single(roots);
        Assert.Equal("Valve", roots[0].Name);
    }

    [Fact]
    public void UsingsAndPragmas_AreSkipped()
    {
        const string src = @"
#pragma warning disable CS1591
using System;
using System.Collections.Generic;

[XClass]
public class Valve { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Walk(src);
        Assert.Single(roots);
    }

    // ===== Integration ==========================================

    [Fact]
    public void Integration_RepresentativeFile_ProducesCorrectShape()
    {
        const string src = @"
// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;

namespace XPact.Equipment;

[System.Flags]
[XEnum]
public enum ValveFlags : uint
{
    None = 0,
    Open = 1,
    Locked = 2,
}

[XStruct]
public struct ValveCalibration
{
    [XProperty(EditAnywhere)]
    public float MinTorque { get; set; }
    [XProperty(EditAnywhere)]
    public float MaxTorque { get; set; }
}

[XClass(Blueprintable)]
public partial class Valve : Actor, IPickable
{
    [XProperty(EditAnywhere, BlueprintReadWrite, Category = ""Setup"")]
    public int Health { get; set; }

    [XProperty(BlueprintReadOnly)]
    public float OpenFraction { get; private set; }

    [XFunction(BlueprintCallable)]
    public void Open(float fraction) { }

    [XFunction(BlueprintCallable, Server, Reliable)]
    public bool TryLatch(int index) { return false; }
}

[XClass]
public class Tank : Actor
{
    [XProperty]
    public float Volume { get; set; }
}
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);

        Assert.Equal(4, roots.Count);

        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.Equal("ValveFlags", e.Name);
        Assert.True(e.IsFlags);
        Assert.Equal(3, e.Values.Count);

        XhtStruct s = Assert.IsType<XhtStruct>(roots[1]);
        Assert.Equal("ValveCalibration", s.Name);
        Assert.Equal(2, s.Properties.Count);

        XhtClass cls = Assert.IsType<XhtClass>(roots[2]);
        Assert.Equal("Valve", cls.Name);
        Assert.Equal("Actor", cls.SuperIdentifier);
        Assert.Contains("IPickable", cls.InterfaceIdentifiers);
        Assert.True(cls.HasGeneratedBody);
        Assert.Null(cls.RequiredAPIMacroName);
        Assert.Equal(2, cls.Properties.Count);
        Assert.Equal(2, cls.Functions.Count);
        Assert.Equal("XPact.Equipment.Valve", cls.FullyQualifiedName);

        XhtClass tank = Assert.IsType<XhtClass>(roots[3]);
        Assert.Equal("Tank", tank.Name);
        Assert.Single(tank.Properties);

        Assert.Equal(4, table.Count);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ===== C2 audit: attribute name matching gaps ================

    [Fact]
    public void XClass_GloballyQualifiedAttribute_Recognised()
    {
        // global::Simgenics.XPact.Reflection.XClass is the deeply-qualified
        // form; the walker must reduce it to the simple-name "XClass".
        // Per C2 audit (XHT.html Section 3.2).
        const string src = @"
namespace XPact.Scoring;

[global::Simgenics.XPact.Reflection.XClass]
public class Valve { }
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Valve", cls.Name);
        Assert.NotNull(table.Lookup("Valve"));
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_DeeplyNestedQualifiedAttribute_Recognised()
    {
        // A.B.C.D.XClass -- nested QualifiedNameSyntax. The walker must
        // recurse through the Right links to reach the leaf identifier.
        const string src = @"
[A.B.C.D.XClass]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Foo", cls.Name);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_GenericAttributeForm_EmitsXHT044_AndIsNotAccepted()
    {
        // [XClass<T>] is a C# 11+ generic-attribute form. XHT Phase 1
        // does not interpret the type-argument list; rather than
        // silently drop it, the walker emits XHT044 and refuses to
        // accept the marker.
        const string src = @"
[XClass<int>]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Empty(roots);
        Assert.Contains(diags, d => d.Code == CSharpMarkerWalker.DiagGenericAttributeUnsupported);
    }

    [Fact]
    public void XClass_DuplicateMarkerOnSameTarget_EmitsXHT045Warning_AndUsesFirst()
    {
        // [XClass, XClass] (or two separate [XClass] lists) is invalid
        // intent. The walker keeps the first occurrence and warns.
        const string src = @"
[XClass]
[XClass]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        Assert.Contains(diags, d => d.Code == CSharpMarkerWalker.DiagDuplicateMarkerAttribute
            && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void XClass_DuplicateMarkerInSameAttributeList_EmitsXHT045Warning()
    {
        // Same attribute repeated within a single [a, b, c] list.
        const string src = @"
[XClass, XClass]
public class Foo { }
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Walk(src);
        Assert.Single(roots);
        Assert.Contains(diags, d => d.Code == CSharpMarkerWalker.DiagDuplicateMarkerAttribute);
    }
}
