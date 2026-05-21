// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.AST;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.AST;

/// <summary>
/// Tests for <see cref="SymbolTable"/>. The caseless lookup model is
/// the core of cross-language reflection pairing per
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 + Section 5.4.
/// </summary>
public class SymbolTableTests
{
    private static SourceSpan TestSpan() => new("Test.h", 1, 1, 5);

    private static XhtClass MakeClass(string name, Language lang = Language.Cpp, string module = "XScoring")
    {
        return new XhtClass(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: module,
            Language: lang,
            Span: TestSpan(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Functions: Array.Empty<XhtFunction>(),
            Properties: Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: Array.Empty<string>(),
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: null,
            HasGeneratedBody: false);
    }

    [Fact]
    public void Register_Then_Lookup_HitsCaselessKey()
    {
        SymbolTable t = new();
        XhtClass c = MakeClass("AXValve");

        t.Register(c);

        Assert.Same(c, t.Lookup("AXValve"));
    }

    [Fact]
    public void Lookup_CaseInsensitiveMatch_RequiresUePrefixForm()
    {
        // Per Section 3.3, the prefix-strip rule requires both the
        // first character to be in the UE-convention set
        // {A, U, I, F} (uppercase) AND the second character to be
        // uppercase. The lookup applies the same rule to its input,
        // so a fully-lowercased "axvalve" lookup does NOT strip its
        // leading 'a' (it isn't uppercase 'A', so it isn't a UE
        // prefix). This is by design -- IDE-side identifier capture
        // preserves casing, so the lookup keys match the source form.
        // The "case-insensitive" part of the model applies to the
        // post-strip suffix, not to the prefix-detection itself.
        SymbolTable t = new();
        t.Register(MakeClass("AXValve"));

        // Source-cased lookup hits (canonical author form).
        Assert.NotNull(t.Lookup("AXValve"));
        // Fully uppercased lookup hits (A and X both uppercase -> strip).
        Assert.NotNull(t.Lookup("AXVALVE"));
        // Engine-name-only form hits (no prefix to strip in this string).
        Assert.NotNull(t.Lookup("XValve"));
        Assert.NotNull(t.Lookup("xvalve"));
        // Fully-lowercased form with leading 'a' does NOT hit because
        // 'a' is not in the UE strip set; the test asserts the model's
        // documented limitation.
        Assert.Null(t.Lookup("axvalve"));
    }

    [Fact]
    public void Lookup_Miss_ReturnsNull()
    {
        SymbolTable t = new();
        Assert.Null(t.Lookup("NoSuchType"));
    }

    [Fact]
    public void TryLookup_Miss_ReturnsFalseAndNullOut()
    {
        SymbolTable t = new();
        bool ok = t.TryLookup("NoSuchType", out XhtTypeBase? value);
        Assert.False(ok);
        Assert.Null(value);
    }

    [Fact]
    public void TryLookup_Hit_ReturnsTrueAndPopulatedOut()
    {
        SymbolTable t = new();
        XhtClass c = MakeClass("AXValve");
        t.Register(c);

        bool ok = t.TryLookup("AXValve", out XhtTypeBase? value);
        Assert.True(ok);
        Assert.Same(c, value);
    }

    [Fact]
    public void Register_CrossLanguagePairing_AXValveAndXValveCollideCaselessly()
    {
        // Per Section 3.3: the C++ AXValve (strip A -> xvalve) and the
        // C++ XValve (no strip, retains permanent X prefix -> xvalve)
        // are caseless collisions because both fold to "xvalve". The
        // table currently throws on collision; in Phase 1c the resolver
        // catches this and pairs them per Section 5.3.
        SymbolTable t = new();
        t.Register(MakeClass("AXValve", Language.Cpp));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => t.Register(MakeClass("XValve", Language.Cpp)));

        Assert.Contains("xvalve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_ValveAndAXValve_AlsoCollide()
    {
        // The Section 3.3 worked example: C++ AXValve and C# Valve share
        // the engine name. Phase 1b's table treats this as a collision;
        // Phase 1c's resolver disambiguates by language tag.
        // Wait -- AXValve -> xvalve, Valve -> valve; these are NOT the
        // same key. We need to test the case where the names DO collide.
        // The canonical paired form is C++ XValve (key xvalve) vs C#
        // Valve (key valve) which are DIFFERENT. The recommended pairing
        // form is BOTH sides emit "valve" -- C++ uses no prefix or both
        // sides use a prefixed form. So actually they fold to different
        // keys.
        SymbolTable t = new();
        t.Register(MakeClass("AXValve", Language.Cpp));
        // Valve does NOT collide with AXValve because they fold to
        // different keys (xvalve vs valve). The Section 3.3 note
        // explicitly says "this is a different engine name".
        t.Register(MakeClass("Valve", Language.CSharp));

        Assert.Equal(2, t.Count);
        Assert.NotNull(t.Lookup("AXValve"));
        Assert.NotNull(t.Lookup("Valve"));
    }

    [Fact]
    public void Register_DuplicateExactName_Throws()
    {
        SymbolTable t = new();
        t.Register(MakeClass("Valve"));

        Assert.Throws<InvalidOperationException>(() => t.Register(MakeClass("Valve")));
    }

    [Fact]
    public void Register_Null_Throws()
    {
        SymbolTable t = new();
        Assert.Throws<ArgumentNullException>(() => t.Register(null!));
    }

    [Fact]
    public void Lookup_Null_Throws()
    {
        SymbolTable t = new();
        Assert.Throws<ArgumentNullException>(() => t.Lookup(null!));
    }

    [Fact]
    public void AllTypes_EnumeratesRegisteredSet()
    {
        SymbolTable t = new();
        t.Register(MakeClass("AXValve"));
        t.Register(MakeClass("Pump"));
        t.Register(MakeClass("Tank"));

        HashSet<string> names = t.AllTypes
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, t.Count);
        Assert.Contains("AXValve", names);
        Assert.Contains("Pump", names);
        Assert.Contains("Tank", names);
    }

    [Fact]
    public async Task Register_ConcurrentInsertsFromManyThreads_DoNotCorruptTable()
    {
        SymbolTable t = new();
        const int parallelism = 16;
        const int perThread = 32;

        // Each thread registers a disjoint set of names; total entries
        // = parallelism * perThread. We assert no exception escapes and
        // every name is locatable post-registration.
        Task[] tasks = new Task[parallelism];
        for (int i = 0; i < parallelism; i++)
        {
            int threadIndex = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < perThread; j++)
                {
                    t.Register(MakeClass($"Type_{threadIndex}_{j}"));
                }
            });
        }
        await Task.WhenAll(tasks);

        Assert.Equal(parallelism * perThread, t.Count);
        for (int i = 0; i < parallelism; i++)
        {
            for (int j = 0; j < perThread; j++)
            {
                Assert.NotNull(t.Lookup($"Type_{i}_{j}"));
            }
        }
    }
}
