// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Parser.Cpp;
using Simgenics.XPact.XHT.Parser.CSharp;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Integration tests that exercise the C++ and C# parsers running
/// against the same <see cref="SymbolTable"/>. Verifies the
/// cross-language registration model per <c>/Documents/XHT.html</c>
/// Rev 5 Section 3.3 + Section 5.7 (cross-language type-reference
/// checks).
/// </summary>
public class CrossLanguageParserTests
{
    private const string Module = "XScoring";

    private static (IReadOnlyList<XhtTypeBase> cppRoots, IReadOnlyList<XhtTypeBase> cSharpRoots, SymbolTable table)
        Run(string cppSource, string cppPath, string cSharpSource, string cSharpPath)
    {
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);

        CppMarkerScanner cppScanner = new(cppPath, cppSource, Module, registry, table);
        IReadOnlyList<XhtTypeBase> cppRoots = cppScanner.Scan();

        CSharpMarkerWalker cSharpWalker = new(cSharpPath, cSharpSource, Module, registry, table);
        IReadOnlyList<XhtTypeBase> cSharpRoots = cSharpWalker.Walk();

        return (cppRoots, cSharpRoots, table);
    }

    [Fact]
    public void CppAndCSharpClass_DifferentEngineNames_BothRegister()
    {
        const string cpp = @"
XCLASS()
class AXValve { };
";
        const string cs = @"
[XClass]
public class Inventory { }
";
        var (cppRoots, csRoots, table) = Run(cpp, "Valve.h", cs, "Inventory.cs");
        Assert.Single(cppRoots);
        Assert.Single(csRoots);
        // 'AXValve' strips 'A' -> 'xvalve'; 'Inventory' has no prefix -> 'inventory'.
        // Different caseless keys, both registered.
        Assert.Equal(2, table.Count);
        Assert.NotNull(table.Lookup("AXValve"));
        Assert.NotNull(table.Lookup("Inventory"));
    }

    [Fact]
    public void CppAndCSharp_SameEngineName_FirstWins_SecondCollides()
    {
        // Cross-language collision: AXValve (strips A -> 'xvalve') and
        // C# 'XValve' (no prefix -> 'xvalve'). The first registered
        // wins; the second emits XHT040.
        const string cpp = @"
XCLASS()
class AXValve { };
";
        const string cs = @"
[XClass]
public class XValve { }
";
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);

        CppMarkerScanner cppScanner = new("Valve.h", cpp, Module, registry, table);
        IReadOnlyList<XhtTypeBase> cppRoots = cppScanner.Scan();

        CSharpMarkerWalker cSharpWalker = new("Valve.cs", cs, Module, registry, table);
        IReadOnlyList<XhtTypeBase> csRoots = cSharpWalker.Walk();

        // SymbolTable has only one entry (the C++ one registered first).
        Assert.Single(cppRoots);
        Assert.Equal(1, table.Count);
        XhtTypeBase entry = Assert.Single(table.AllTypes);
        Assert.Equal("AXValve", entry.Name);
        Assert.Equal(Language.Cpp, entry.Language);
        // The C# walker still emits its node into 'roots' for the resolver
        // to inspect, but it could not register; we verify the duplicate
        // diagnostic was emitted.
        Assert.Single(csRoots);
        Assert.Contains(cSharpWalker.Diagnostics, d => d.Code == CSharpMarkerWalker.DiagDuplicateType);
    }

    [Fact]
    public void CppValveAndCSharpValve_SymbolTableLookupResolvesToCpp()
    {
        // C++ XValve (no UE prefix; permanent X prefix preserved) and
        // C# Valve fold differently: 'xvalve' vs 'valve'. Verify both
        // register and can be looked up by their authored names.
        const string cpp = @"
XCLASS()
class XValve { };
";
        const string cs = @"
[XClass]
public class Valve { }
";
        var (_, _, table) = Run(cpp, "XValve.h", cs, "Valve.cs");
        Assert.Equal(2, table.Count);
        XhtTypeBase? cppEntry = table.Lookup("XValve");
        Assert.NotNull(cppEntry);
        Assert.Equal(Language.Cpp, cppEntry!.Language);

        XhtTypeBase? csEntry = table.Lookup("Valve");
        Assert.NotNull(csEntry);
        Assert.Equal(Language.CSharp, csEntry!.Language);
    }

    [Fact]
    public void TwoCSharpPartials_BothEmittedAsRoots_SecondCollidesAtTable()
    {
        // Per Phase 1c.2b: the walker emits one XhtClass per partial
        // declaration. Merging in the SymbolTable is deferred to
        // Phase 1d resolver.
        const string cs1 = @"
[XClass]
public partial class Foo { }
";
        const string cs2 = @"
[XClass]
public partial class Foo { }
";
        SymbolTable table = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);

        CSharpMarkerWalker w1 = new("Foo.A.cs", cs1, Module, registry, table);
        IReadOnlyList<XhtTypeBase> roots1 = w1.Walk();

        CSharpMarkerWalker w2 = new("Foo.B.cs", cs2, Module, registry, table);
        IReadOnlyList<XhtTypeBase> roots2 = w2.Walk();

        Assert.Single(roots1);
        Assert.Single(roots2);
        Assert.Equal(Language.CSharp, roots1[0].Language);
        Assert.Equal(Language.CSharp, roots2[0].Language);

        // SymbolTable has one entry (the first partial); second emits XHT040.
        Assert.Equal(1, table.Count);
        Assert.Contains(w2.Diagnostics, d => d.Code == CSharpMarkerWalker.DiagDuplicateType);
    }

    [Fact]
    public void Integration_RepresentativeMixedModule_PopulatesSymbolTableForBoth()
    {
        const string cpp = @"
// Copyright Simgenics. All Rights Reserved.
#pragma once

XSTRUCT()
struct FCalibration
{
    XPROPERTY(EditAnywhere)
    float MinValue;
};

XCLASS(Blueprintable)
class XSCORING_API AXValve : public AXActor
{
    XGENERATED_BODY()
    XPROPERTY(EditAnywhere)
    int32 Health;
};
";
        const string cs = @"
// Copyright Simgenics. All Rights Reserved.

namespace XPact.Scoring;

[XInterface]
public interface IInteractable { }

[XClass]
public class Inventory
{
    [XProperty(EditAnywhere)]
    public int SlotCount { get; set; }
}
";
        var (cppRoots, csRoots, table) = Run(cpp, "Valve.h", cs, "Inventory.cs");

        Assert.Equal(2, cppRoots.Count);
        Assert.Equal(2, csRoots.Count);

        // 4 total registered (FCalibration, AXValve, IInteractable, Inventory).
        Assert.Equal(4, table.Count);

        // C++ side
        XhtTypeBase? valve = table.Lookup("AXValve");
        Assert.NotNull(valve);
        Assert.Equal(Language.Cpp, valve!.Language);

        XhtTypeBase? calib = table.Lookup("FCalibration");
        Assert.NotNull(calib);
        Assert.Equal(Language.Cpp, calib!.Language);

        // C# side
        XhtTypeBase? iface = table.Lookup("IInteractable");
        Assert.NotNull(iface);
        Assert.Equal(Language.CSharp, iface!.Language);

        XhtTypeBase? inv = table.Lookup("Inventory");
        Assert.NotNull(inv);
        Assert.Equal(Language.CSharp, inv!.Language);
    }
}
