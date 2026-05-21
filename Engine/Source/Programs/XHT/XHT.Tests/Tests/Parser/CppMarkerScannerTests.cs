// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Parser.Cpp;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Tests for <see cref="CppMarkerScanner"/>. Walks the marker-driven
/// dispatch path end-to-end against synthetic source snippets per
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.4 + Section 7.
/// </summary>
public class CppMarkerScannerTests
{
    private const string Module = "XScoring";
    private const string Path = "Test.h";

    private static (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags)
        Scan(string source)
    {
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);
        CppMarkerScanner scanner = new(Path, source, Module, registry, table);
        IReadOnlyList<XhtTypeBase> roots = scanner.Scan();
        return (roots, table, scanner.Diagnostics);
    }

    // ===== Top-level classes ====================================

    [Fact]
    public void XClass_BareSimpleClass_Registered()
    {
        const string src = @"
XCLASS()
class XValve : public XActor
{
    XGENERATED_BODY()
};
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags) = Scan(src);
        Assert.Single(roots);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("XValve", cls.Name);
        Assert.Equal("XActor", cls.SuperIdentifier);
        Assert.Equal(Module, cls.ModuleName);
        Assert.Equal(Language.Cpp, cls.Language);
        Assert.True(cls.HasGeneratedBody);
        Assert.NotNull(table.Lookup("XValve"));
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void XClass_WithRequiredApiMacro_IsRecorded()
    {
        const string src = @"
XCLASS()
class XSCORING_API XValve : public XActor
{
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("XSCORING_API", cls.RequiredAPIMacroName);
    }

    [Fact]
    public void XClass_WithInterfaceBases_CapturesAllBases()
    {
        const string src = @"
XCLASS()
class XValve : public XActor, public IPickable, public IInteractable
{
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("XActor", cls.SuperIdentifier);
        Assert.Equal(2, cls.InterfaceIdentifiers.Count);
        Assert.Contains("IPickable", cls.InterfaceIdentifiers);
        Assert.Contains("IInteractable", cls.InterfaceIdentifiers);
    }

    [Fact]
    public void XClass_Specifiers_AttachedToType()
    {
        const string src = @"
XCLASS(Blueprintable, Abstract)
class XBase
{
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal(2, cls.Specifiers.Count);
        Assert.Contains(cls.Specifiers, s => s.Key == "Blueprintable");
        Assert.Contains(cls.Specifiers, s => s.Key == "Abstract");
    }

    // ===== Properties + functions inside class ===================

    [Fact]
    public void XClass_WithProperty_AttachesToClass()
    {
        const string src = @"
XCLASS()
class XValve
{
    XGENERATED_BODY()
    XPROPERTY(EditAnywhere, BlueprintReadWrite, Category=""Setup"")
    int32 Health;
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Properties);
        XhtProperty prop = cls.Properties[0];
        Assert.Equal("Health", prop.Name);
        Assert.Equal("int32", prop.TypeIdentifier);
        Assert.Equal("Setup", prop.Category);
        Assert.Equal(3, prop.Specifiers.Count);
    }

    [Fact]
    public void XClass_WithFunction_AttachesToClass()
    {
        const string src = @"
XCLASS()
class XValve
{
    XGENERATED_BODY()
    XFUNCTION(BlueprintCallable)
    void Open(float Fraction);
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Single(cls.Functions);
        XhtFunction fn = cls.Functions[0];
        Assert.Equal("Open", fn.Name);
        Assert.Single(fn.Parameters);
        Assert.Equal("Fraction", fn.Parameters[0].Name);
        Assert.Equal("float", fn.Parameters[0].TypeIdentifier);
    }

    [Fact]
    public void XClass_FunctionWithMultipleParams_AllCaptured()
    {
        const string src = @"
XCLASS()
class XValve
{
    XGENERATED_BODY()
    XFUNCTION()
    bool TrySet(int32 Value, float Tolerance);
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        XhtFunction fn = cls.Functions[0];
        Assert.Equal(2, fn.Parameters.Count);
        Assert.Equal("Value", fn.Parameters[0].Name);
        Assert.Equal("Tolerance", fn.Parameters[1].Name);
    }

    [Fact]
    public void XClass_ConstStaticVirtualFunction_FlagsCaptured()
    {
        const string src = @"
XCLASS()
class XValve
{
    XFUNCTION()
    static int32 ComputeSize();
    XFUNCTION()
    virtual void Tick(float DeltaTime);
    XFUNCTION()
    void Inspect() const;
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal(3, cls.Functions.Count);
        Assert.True(cls.Functions[0].IsStatic);
        Assert.True(cls.Functions[1].IsVirtual);
        Assert.True(cls.Functions[2].IsConst);
    }

    [Fact]
    public void XParam_InlineSpecifiers_AttachedToParameter()
    {
        const string src = @"
XCLASS()
class XValve
{
    XFUNCTION()
    void Compute(XPARAM(Out) int32& Result, int32 Input);
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        XhtFunction fn = cls.Functions[0];
        Assert.Equal(2, fn.Parameters.Count);
        Assert.Single(fn.Parameters[0].Specifiers);
        Assert.Equal("Out", fn.Parameters[0].Specifiers[0].Key);
        Assert.True(fn.Parameters[0].IsOut);
    }

    // ===== Structs ==============================================

    [Fact]
    public void XStruct_Basic_Registered()
    {
        const string src = @"
XSTRUCT()
struct FValveSettings
{
    XPROPERTY()
    float Tolerance;
};
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Scan(src);
        Assert.Single(roots);
        XhtStruct st = Assert.IsType<XhtStruct>(roots[0]);
        Assert.Equal("FValveSettings", st.Name);
        Assert.Single(st.Properties);
        Assert.NotNull(table.Lookup("FValveSettings"));
    }

    // ===== Enums ================================================

    [Fact]
    public void XEnum_WithUnderlying_AndMembers_Registered()
    {
        const string src = @"
XENUM()
enum class EValveKind : uint8
{
    Ball,
    Gate = 2,
    Globe,
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        Assert.Single(roots);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.Equal("EValveKind", e.Name);
        Assert.Equal("uint8", e.UnderlyingType);
        Assert.Equal(3, e.Values.Count);
        Assert.Equal("Ball", e.Values[0].Name);
        Assert.Equal(0, e.Values[0].Value);
        Assert.Equal("Gate", e.Values[1].Name);
        Assert.Equal(2, e.Values[1].Value);
        Assert.Equal("Globe", e.Values[2].Name);
        Assert.Equal(3, e.Values[2].Value);
    }

    [Fact]
    public void XEnum_BitmaskSpecifier_SetsIsFlags()
    {
        const string src = @"
XENUM(Bitmask)
enum class EValveFlags : uint32
{
    None = 0,
    Open = 1,
    Closed = 2,
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.True(e.IsFlags);
    }

    [Fact]
    public void XMeta_OnEnumValue_AttachedAsSpecifier()
    {
        const string src = @"
XENUM()
enum class EKind : uint8
{
    XMETA(Hidden)
    Reserved,
    Public,
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.Equal(2, e.Values.Count);
        Assert.Single(e.Values[0].Specifiers);
        Assert.Equal("Hidden", e.Values[0].Specifiers[0].Key);
    }

    // ===== Interfaces ===========================================

    [Fact]
    public void XInterface_Basic_Registered()
    {
        const string src = @"
XINTERFACE()
class IPickable
{
    XGENERATED_BODY()
};
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Scan(src);
        Assert.Single(roots);
        XhtInterface iface = Assert.IsType<XhtInterface>(roots[0]);
        Assert.Equal("IPickable", iface.Name);
        Assert.NotNull(table.Lookup("IPickable"));
    }

    // ===== Delegates ============================================

    [Fact]
    public void XDelegate_MulticastNoArgs_Registered()
    {
        const string src = @"
XDELEGATE()
DECLARE_DYNAMIC_MULTICAST_DELEGATE(FOnValveOpened);
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        Assert.Single(roots);
        XhtDelegate del = Assert.IsType<XhtDelegate>(roots[0]);
        Assert.Equal("FOnValveOpened", del.Name);
        Assert.True(del.IsMulticast);
    }

    [Fact]
    public void XDelegate_FalsePositive_MulticastSubstringInUnrelatedMacro_NotMisidentified()
    {
        // Per M7 audit: identifiers containing 'MULTICAST' that AREN'T
        // DECLARE_DYNAMIC_MULTICAST_DELEGATE_* must not be flagged as
        // multicast delegates. The legacy substring-match logic
        // false-positived MY_OWN_MULTICAST_HELPER and similar.
        const string src = @"
XDELEGATE()
DECLARE_DYNAMIC_DELEGATE_OneParam(FOnSimpleSingleCast, int32, Value);
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        Assert.Single(roots);
        XhtDelegate del = Assert.IsType<XhtDelegate>(roots[0]);
        Assert.Equal("FOnSimpleSingleCast", del.Name);
        Assert.False(del.IsMulticast);
    }

    // ===== Namespaces ===========================================

    [Fact]
    public void Namespace_FullyQualifiedNameComposedFromStack()
    {
        const string src = @"
namespace Industrial {
    XCLASS()
    class XValve
    {
    };
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Industrial::XValve", cls.FullyQualifiedName);
    }

    [Fact]
    public void Namespace_NestedNamespaceTrackedCorrectly()
    {
        const string src = @"
namespace Outer {
namespace Inner {
    XCLASS()
    class XValve
    {
    };
}
}
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.Equal("Outer::Inner::XValve", cls.FullyQualifiedName);
    }

    // ===== XGENERATED_BODY without parens =======================

    [Fact]
    public void XGeneratedBody_WithoutParens_AlsoTracked()
    {
        const string src = @"
XCLASS()
class XValve
{
    XGENERATED_BODY
};
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        XhtClass cls = Assert.IsType<XhtClass>(roots[0]);
        Assert.True(cls.HasGeneratedBody);
    }

    // ===== Error recovery ======================================

    [Fact]
    public void UnknownSpecifier_DoesNotAbort_AndContinuesParsing()
    {
        const string src = @"
XCLASS(NotARealSpecifier)
class XFirst { };

XCLASS()
class XSecond { };
";
        (IReadOnlyList<XhtTypeBase> roots, _, IReadOnlyList<DiagnosticRecord> diags) = Scan(src);
        Assert.Equal(2, roots.Count);
        Assert.Contains(diags, d => d.Code == CppSpecifierParser.DiagUnknownSpecifier);
    }

    [Fact]
    public void XFunction_OutsideClass_EmitsContextError()
    {
        const string src = @"
XFUNCTION()
void OrphanFunction();
";
        (_, _, IReadOnlyList<DiagnosticRecord> diags) = Scan(src);
        Assert.Contains(diags, d => d.Code == CppMarkerScanner.DiagMarkerContextError);
    }

    // ===== Multiple types per file ==============================

    [Fact]
    public void MultipleMarkers_AllRegistered_InSourceOrder()
    {
        const string src = @"
XCLASS()
class XValve { };

XCLASS()
class XTank { };

XSTRUCT()
struct FConfig { };

XENUM()
enum class EKind : uint8 { A, B };
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, _) = Scan(src);
        Assert.Equal(4, roots.Count);
        Assert.Equal("XValve", roots[0].Name);
        Assert.Equal("XTank", roots[1].Name);
        Assert.Equal("FConfig", roots[2].Name);
        Assert.Equal("EKind", roots[3].Name);
        Assert.Equal(4, table.Count);
    }

    // ===== Caseless collisions ==================================

    [Fact]
    public void DuplicateType_CaselessCollision_EmitsXHT040()
    {
        const string src = @"
XCLASS()
class XValve { };

XCLASS()
class XVALVE { };  // same engine-name 'xvalve' after lowercasing; collides.
";
        (IReadOnlyList<XhtTypeBase> _, _, IReadOnlyList<DiagnosticRecord> diags) = Scan(src);
        Assert.Contains(diags, d => d.Code == CppMarkerScanner.DiagDuplicateType);
    }

    // ===== Non-reflected content tolerance ======================

    [Fact]
    public void NonReflectedClass_IgnoredEntirely()
    {
        const string src = @"
class NotReflected { int x; };

XCLASS()
class XValve { };
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        Assert.Single(roots);
        Assert.Equal("XValve", roots[0].Name);
    }

    [Fact]
    public void IncludeDirectives_AndMacros_AreSkipped()
    {
        const string src = @"
#include ""SomeHeader.h""
#pragma once

XCLASS()
class XValve { };
";
        (IReadOnlyList<XhtTypeBase> roots, _, _) = Scan(src);
        Assert.Single(roots);
    }

    // ===== Integration: representative header ==================

    [Fact]
    public void Integration_RepresentativeXPactHeader_ProducesCorrectShape()
    {
        const string src = @"
// Copyright Simgenics. All Rights Reserved.
#pragma once
#include ""XCoreXObject.h""
#include ""XValve.gen.h""

XENUM(Bitmask)
enum class EValveFlags : uint32
{
    None = 0,
    Open = 1,
    Locked = 2,
};

XSTRUCT()
struct FValveCalibration
{
    XPROPERTY(EditAnywhere)
    float MinTorque;
    XPROPERTY(EditAnywhere)
    float MaxTorque;
};

XCLASS(Blueprintable)
class XSCORING_API XValve : public XActor, public IPickable
{
    XGENERATED_BODY()

    XPROPERTY(EditAnywhere, BlueprintReadWrite, Category=""Setup"")
    int32 Health;

    XPROPERTY(BlueprintReadOnly)
    float OpenFraction;

    XFUNCTION(BlueprintCallable)
    void Open(float Fraction);

    XFUNCTION(BlueprintCallable, Server, Reliable)
    bool TryLatch(int32 Index);
};

XCLASS()
class XTank : public XActor
{
    XGENERATED_BODY()
    XPROPERTY()
    float Volume;
};
";
        (IReadOnlyList<XhtTypeBase> roots, SymbolTable table, IReadOnlyList<DiagnosticRecord> diags) = Scan(src);

        // 4 top-level reflected types.
        Assert.Equal(4, roots.Count);

        XhtEnum e = Assert.IsType<XhtEnum>(roots[0]);
        Assert.Equal("EValveFlags", e.Name);
        Assert.True(e.IsFlags);
        Assert.Equal(3, e.Values.Count);

        XhtStruct s = Assert.IsType<XhtStruct>(roots[1]);
        Assert.Equal("FValveCalibration", s.Name);
        Assert.Equal(2, s.Properties.Count);

        XhtClass cls = Assert.IsType<XhtClass>(roots[2]);
        Assert.Equal("XValve", cls.Name);
        Assert.Equal("XActor", cls.SuperIdentifier);
        Assert.Contains("IPickable", cls.InterfaceIdentifiers);
        Assert.Equal("XSCORING_API", cls.RequiredAPIMacroName);
        Assert.True(cls.HasGeneratedBody);
        Assert.Equal(2, cls.Properties.Count);
        Assert.Equal(2, cls.Functions.Count);

        XhtClass tank = Assert.IsType<XhtClass>(roots[3]);
        Assert.Equal("XTank", tank.Name);
        Assert.Single(tank.Properties);

        // All four registered.
        Assert.Equal(4, table.Count);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
