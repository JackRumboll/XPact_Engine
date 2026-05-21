// Copyright Simgenics. All Rights Reserved.

using System;
using System.Linq;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Tables;

/// <summary>
/// Tests for <see cref="SpecifierRegistry"/>: registration, case-insensitive
/// lookup, context-intersection semantics, and the conflict-detection rule
/// (identical-warn vs non-identical-throw) per
/// <c>/Documents/XHT.html</c> Rev 5 Section 18.1.
/// </summary>
public class SpecifierRegistryTests
{
    private static SpecifierRegistry NewEmptyRegistry()
    {
        return new SpecifierRegistry(registerBuiltIns: false);
    }

    private static SpecifierDefinition MakeFlag(string name, SpecifierContext ctx = SpecifierContext.Class)
    {
        return new SpecifierDefinition(name, ctx, SpecifierValueKind.Flag, false, null);
    }

    [Fact]
    public void Register_AddsDefinitionAndAppearsInAllSpecifiers()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition def = MakeFlag("FooSpecifier");

        r.Register(def);

        Assert.Single(r.AllSpecifiers);
        Assert.Contains(def, r.AllSpecifiers);
    }

    [Fact]
    public void Register_Null_Throws()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        Assert.Throws<ArgumentNullException>(() => r.Register(null!));
    }

    [Fact]
    public void Resolve_HitsRegisteredSpecifier_WhenContextMatches()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition def = MakeFlag("EditAnywhere", SpecifierContext.PropertyMember);
        r.Register(def);

        SpecifierDefinition? hit = r.Resolve("EditAnywhere", SpecifierContext.PropertyMember);

        Assert.Same(def, hit);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_PerSection72()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition def = MakeFlag("BlueprintReadWrite", SpecifierContext.PropertyMember);
        r.Register(def);

        Assert.Same(def, r.Resolve("blueprintreadwrite", SpecifierContext.PropertyMember));
        Assert.Same(def, r.Resolve("BLUEPRINTREADWRITE", SpecifierContext.PropertyMember));
        Assert.Same(def, r.Resolve("BlueprintReadWrite", SpecifierContext.PropertyMember));
        Assert.Same(def, r.Resolve("blueprintREADwrite", SpecifierContext.PropertyMember));
    }

    [Fact]
    public void Resolve_Miss_ReturnsNull()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        Assert.Null(r.Resolve("NoSuchSpecifier", SpecifierContext.Class));
    }

    [Fact]
    public void Resolve_ContextMismatch_ReturnsNull()
    {
        // Specifier registered as Class-only; lookup with PropertyMember
        // context must miss.
        SpecifierRegistry r = NewEmptyRegistry();
        r.Register(MakeFlag("Abstract", SpecifierContext.Class));

        Assert.Null(r.Resolve("Abstract", SpecifierContext.PropertyMember));
        Assert.Null(r.Resolve("Abstract", SpecifierContext.Function));
    }

    [Fact]
    public void Resolve_ContextIntersection_HitsOnUnionMask()
    {
        // Specifier legal on both Class and Struct via union mask.
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition def = MakeFlag("BlueprintType",
            SpecifierContext.Class | SpecifierContext.Struct | SpecifierContext.Enum);
        r.Register(def);

        Assert.Same(def, r.Resolve("BlueprintType", SpecifierContext.Class));
        Assert.Same(def, r.Resolve("BlueprintType", SpecifierContext.Struct));
        Assert.Same(def, r.Resolve("BlueprintType", SpecifierContext.Enum));
        Assert.Null(r.Resolve("BlueprintType", SpecifierContext.Function));
    }

    [Fact]
    public void TryResolve_Hit_ReturnsTrueAndDef()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition def = MakeFlag("SaveGame", SpecifierContext.PropertyMember);
        r.Register(def);

        bool ok = r.TryResolve("SaveGame", SpecifierContext.PropertyMember, out SpecifierDefinition? hit);

        Assert.True(ok);
        Assert.Same(def, hit);
    }

    [Fact]
    public void TryResolve_Miss_ReturnsFalseAndNull()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        bool ok = r.TryResolve("NoSuch", SpecifierContext.Class, out SpecifierDefinition? hit);

        Assert.False(ok);
        Assert.Null(hit);
    }

    [Fact]
    public void Register_Conflict_DifferentShape_Throws()
    {
        // Two definitions sharing a name but differing on ValueKind --
        // structurally different, must throw per Section 18.1 / XHT140.
        SpecifierRegistry r = NewEmptyRegistry();
        r.Register(new SpecifierDefinition("Within", SpecifierContext.Class, SpecifierValueKind.Reference, false, null));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            r.Register(new SpecifierDefinition("Within", SpecifierContext.Class, SpecifierValueKind.SingleValue, false, null)));

        Assert.Contains("Within", ex.Message, StringComparison.Ordinal);
        Assert.Contains("XHT140", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_IdenticalReregistration_DoesNotThrow()
    {
        // Two structurally-identical definitions must NOT throw (benign
        // duplicate). The registry emits a Logger.Warning but the call
        // completes successfully and the registry still has one entry.
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition def1 = new("Replicated", SpecifierContext.PropertyMember,
            SpecifierValueKind.Flag, false, "doc");
        SpecifierDefinition def2 = new("Replicated", SpecifierContext.PropertyMember,
            SpecifierValueKind.Flag, false, "doc");

        r.Register(def1);
        r.Register(def2);

        Assert.Single(r.AllSpecifiers);
    }

    [Fact]
    public void BuiltInSpecifiersRegisterAllInto_RoundTrip_CountsAllPresent()
    {
        // A fresh registry pre-populated by the BuiltInSpecifiers list
        // contains the same number of entries as the source list.
        SpecifierRegistry r = new(registerBuiltIns: true);

        Assert.Equal(BuiltInSpecifiers.All.Count, r.AllSpecifiers.Count);

        // Each declared specifier must round-trip through Resolve at the
        // primary applicable context.
        foreach (SpecifierDefinition def in BuiltInSpecifiers.All)
        {
            // Pick the lowest-bit context in ApplicableTo as the
            // representative for resolution.
            SpecifierContext representative = FirstBit(def.ApplicableTo);
            SpecifierDefinition? hit = r.Resolve(def.Name, representative);
            Assert.NotNull(hit);
            Assert.Equal(def.Name, hit!.Name);
        }
    }

    [Fact]
    public void DefaultConstructor_RegistersBuiltIns()
    {
        SpecifierRegistry r = new();
        // The Phase-1 vocabulary is non-trivial in size; sanity check.
        Assert.NotEmpty(r.AllSpecifiers);
        Assert.True(r.AllSpecifiers.Count >= 20,
            $"Expected at least 20 built-in specifiers; got {r.AllSpecifiers.Count}.");
    }

    [Fact]
    public void Resolve_Null_Throws()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        Assert.Throws<ArgumentNullException>(() => r.Resolve(null!, SpecifierContext.Class));
    }

    [Fact]
    public void TryResolve_Null_Throws()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        Assert.Throws<ArgumentNullException>(() =>
            r.TryResolve(null!, SpecifierContext.Class, out _));
    }

    [Fact]
    public void AllSpecifiers_ReflectsConcurrentRegistrations()
    {
        // Sequential check: registering N entries surfaces all of them.
        // (Concurrent stress is covered by the ConcurrentDictionary's own
        // contract; SymbolTableTests already exercises the same backing
        // pattern under contention.)
        SpecifierRegistry r = NewEmptyRegistry();
        for (int i = 0; i < 10; i++)
        {
            r.Register(MakeFlag($"Spec{i}", SpecifierContext.Class));
        }

        Assert.Equal(10, r.AllSpecifiers.Count);
        Assert.Equal(10, r.AllSpecifiers.Select(s => s.Name).Distinct().Count());
    }

    private static SpecifierContext FirstBit(SpecifierContext ctx)
    {
        for (int bit = 0; bit < 16; bit++)
        {
            SpecifierContext candidate = (SpecifierContext)(1 << bit);
            if ((ctx & candidate) != 0)
            {
                return candidate;
            }
        }
        return ctx;
    }

    // ===== M5 audit: AllowOverride plugin extensibility =========

    [Fact]
    public void Register_BothAllowOverride_SecondWins()
    {
        // Per M5 audit (XHT.html Section 18.1): when both an existing
        // entry and an incoming entry set AllowOverride=true, the
        // incoming wins. Plugin-extensibility path.
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition base_ = new("PluginSpec", SpecifierContext.Class,
            SpecifierValueKind.Flag, AllowMultiple: false, Documentation: "base",
            AllowOverride: true);
        SpecifierDefinition derived = new("PluginSpec", SpecifierContext.Property,
            SpecifierValueKind.SingleValue, AllowMultiple: true, Documentation: "derived",
            AllowOverride: true);

        r.Register(base_);
        r.Register(derived);

        SpecifierDefinition? hit = r.Resolve("PluginSpec", SpecifierContext.Property);
        Assert.NotNull(hit);
        Assert.Equal("derived", hit!.Documentation);
        Assert.True(hit.AllowMultiple);
    }

    [Fact]
    public void Register_OnlyOneAllowOverride_StillThrowsOnConflict()
    {
        SpecifierRegistry r = NewEmptyRegistry();
        SpecifierDefinition base_ = new("PluginSpec", SpecifierContext.Class,
            SpecifierValueKind.Flag, false, null, AllowOverride: true);
        SpecifierDefinition derived = new("PluginSpec", SpecifierContext.Property,
            SpecifierValueKind.SingleValue, false, null, AllowOverride: false);

        r.Register(base_);
        Assert.Throws<InvalidOperationException>(() => r.Register(derived));
    }
}
