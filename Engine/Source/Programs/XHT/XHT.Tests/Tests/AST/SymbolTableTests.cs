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
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 + Section 5.4. Round-2
/// directive removed the A / U / I / F prefix-strip; the engine-name is
/// the source identifier lowercased verbatim.
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
        XhtClass c = MakeClass("XValve");

        t.Register(c);

        Assert.Same(c, t.Lookup("XValve"));
    }

    [Fact]
    public void Lookup_CaseInsensitive()
    {
        // Round-2: the engine-name is the lowercased source identifier
        // verbatim. The lookup is case-insensitive. The X prefix is
        // preserved (it is XPact's permanent prefix, not a UE-convention
        // adornment).
        SymbolTable t = new();
        t.Register(MakeClass("XValve"));

        Assert.NotNull(t.Lookup("XValve"));
        Assert.NotNull(t.Lookup("XVALVE"));
        Assert.NotNull(t.Lookup("xvalve"));
        Assert.NotNull(t.Lookup("xValve"));
        // "Valve" (no X) is a DIFFERENT engine name -- no implicit
        // pairing because the legacy prefix-strip is gone.
        Assert.Null(t.Lookup("Valve"));
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
        XhtClass c = MakeClass("XValve");
        t.Register(c);

        bool ok = t.TryLookup("XValve", out XhtTypeBase? value);
        Assert.True(ok);
        Assert.Same(c, value);
    }

    [Fact]
    public void Register_CrossLanguagePairing_XPrefixForms_CollideCaselessly()
    {
        // Round-2 cross-language pairing: both C++ and C# side spell the
        // type as XValve; they collide on the engine name "xvalve".
        // Phase 1d's resolver folds them into one logical entry; the
        // first-registered side wins the slot.
        SymbolTable t = new();
        t.Register(MakeClass("XValve", Language.Cpp));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => t.Register(MakeClass("XValve", Language.CSharp)));

        Assert.Contains("xvalve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_DifferentEngineNames_BothRegister()
    {
        // XValve and Valve are DIFFERENT engine names under Round-2 (no
        // prefix-strip). Both register successfully.
        SymbolTable t = new();
        t.Register(MakeClass("XValve", Language.Cpp));
        t.Register(MakeClass("Valve", Language.CSharp));

        Assert.Equal(2, t.Count);
        Assert.NotNull(t.Lookup("XValve"));
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
        t.Register(MakeClass("XValve"));
        t.Register(MakeClass("Pump"));
        t.Register(MakeClass("Tank"));

        HashSet<string> names = t.AllTypes
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, t.Count);
        Assert.Contains("XValve", names);
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
