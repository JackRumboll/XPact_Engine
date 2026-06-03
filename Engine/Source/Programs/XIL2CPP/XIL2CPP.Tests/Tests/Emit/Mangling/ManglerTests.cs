// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Mangling;

/// <summary>
/// Tests for <see cref="Mangler"/>: it reproduces the Contract Section 2.2 doc
/// example symbol byte-for-byte, is deterministic across runs (gate
/// X-IL2CPP-MANGLE-DET), and applies the ctor / cctor / getter / operator /
/// ref-parameter discriminators per Contract Section 2.2; <c>ref readonly</c>
/// raises XIL2CPP179.
/// </summary>
public sealed class ManglerTests
{
    private const string Tag = "1ab12cd34";

    /// <summary>
    /// The Contract Section 2.2 doc example: <c>Valve : Actor</c> in
    /// <c>Simgenics.XPact.GameFramework</c> with a
    /// <c>void SetOpenFraction(float inFraction)</c> method.
    /// </summary>
    private const string DocExampleSource = """
        namespace Simgenics.XPact.GameFramework
        {
            public class Actor { }
            public class Valve : Actor
            {
                public void SetOpenFraction(float inFraction) { }
            }
        }
        """;

    [Fact]
    public void MangleMethod_ReproducesContractSection22DocExample()
    {
        IMethodSymbol method = FindMethod(DocExampleSource, "Valve", "SetOpenFraction");
        MangledName mangled = Mangler.MangleMethod(method, Tag);

        // Conceptual form per Contract Section 2.2 (the doc's example line).
        Assert.Equal(
            "_v1ab12cd34__Simgenics::XPact::GameFramework::Valve::SetOpenFraction_P_(R Simgenics::XPact::GameFramework::Valve, V float)",
            mangled.CanonicalForm);

        // Linker-visible form per Contract Section 2.3 (the doc's extern "C" line).
        Assert.Equal(
            "_v1ab12cd34__Simgenics__XPact__GameFramework__Valve__SetOpenFraction_P_R_Simgenics__XPact__GameFramework__Valve_V_float",
            mangled.LinkerSymbol);

        Assert.Empty(mangled.Diagnostics);
    }

    [Fact]
    public void MangleMethod_IsByteIdenticalAcrossRuns()
    {
        // Build TWO independent compilations of the same source so the symbols
        // are distinct CLR objects; the mangle must still be byte-identical.
        IMethodSymbol a = FindMethod(DocExampleSource, "Valve", "SetOpenFraction");
        IMethodSymbol b = FindMethod(DocExampleSource, "Valve", "SetOpenFraction");

        MangledName ma = Mangler.MangleMethod(a, Tag);
        MangledName mb = Mangler.MangleMethod(b, Tag);

        Assert.Equal(ma.CanonicalForm, mb.CanonicalForm);
        Assert.Equal(ma.LinkerSymbol, mb.LinkerSymbol);
    }

    [Fact]
    public void MangleMethod_Constructor_UsesDollarCtorToken()
    {
        const string source = """
            namespace N
            {
                public class C { public C(int x) { } }
            }
            """;
        IMethodSymbol ctor = FindConstructor(source, "C");
        MangledName mangled = Mangler.MangleMethod(ctor, Tag);

        Assert.Contains("::C::$ctor", mangled.CanonicalForm);
        // A constructor takes no implicit self in the conceptual param list
        // (the doc example self is only on a plain instance method); the only
        // param is the int.
        Assert.Contains("_P_(V int)", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_StaticConstructor_UsesDollarCctorToken()
    {
        const string source = """
            namespace N
            {
                public class C { static C() { } }
            }
            """;
        IMethodSymbol cctor = FindStaticConstructor(source, "C");
        MangledName mangled = Mangler.MangleMethod(cctor, Tag);

        Assert.Contains("::C::$cctor", mangled.CanonicalForm);
        Assert.Contains("_P_()", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_PropertyGetter_UsesGetUnderscoreToken()
    {
        const string source = """
            namespace N
            {
                public class C { public int Value { get; set; } }
            }
            """;
        IMethodSymbol getter = FindAccessor(source, "C", "Value", isGetter: true);
        MangledName mangled = Mangler.MangleMethod(getter, Tag);

        Assert.Contains("::C::get_Value", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_PropertySetter_UsesSetUnderscoreToken()
    {
        const string source = """
            namespace N
            {
                public class C { public int Value { get; set; } }
            }
            """;
        IMethodSymbol setter = FindAccessor(source, "C", "Value", isGetter: false);
        MangledName mangled = Mangler.MangleMethod(setter, Tag);

        Assert.Contains("::C::set_Value", mangled.CanonicalForm);
        // The setter's value parameter follows the implicit self.
        Assert.Contains("V int", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_Operator_UsesClrOpName()
    {
        const string source = """
            namespace N
            {
                public class C
                {
                    public static C operator +(C a, C b) { return a; }
                }
            }
            """;
        IMethodSymbol op = FindMethodByKind(source, "C", MethodKind.UserDefinedOperator);
        MangledName mangled = Mangler.MangleMethod(op, Tag);

        Assert.Contains("::C::op_Addition", mangled.CanonicalForm);
        // A static operator has no implicit self.
        Assert.Contains("_P_(R N::C, R N::C)", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_RefParameter_UsesByRefDiscriminatorB()
    {
        const string source = """
            namespace N
            {
                public class C { public void M(ref int x) { } }
            }
            """;
        IMethodSymbol m = FindMethod(source, "C", "M");
        MangledName mangled = Mangler.MangleMethod(m, Tag);

        Assert.Contains("B int", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_InParameter_UsesInDiscriminatorI()
    {
        const string source = """
            namespace N
            {
                public class C { public void M(in int x) { } }
            }
            """;
        IMethodSymbol m = FindMethod(source, "C", "M");
        MangledName mangled = Mangler.MangleMethod(m, Tag);

        Assert.Contains("I int", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_OutParameter_UsesOutDiscriminatorO()
    {
        const string source = """
            namespace N
            {
                public class C { public void M(out int x) { x = 0; } }
            }
            """;
        IMethodSymbol m = FindMethod(source, "C", "M");
        MangledName mangled = Mangler.MangleMethod(m, Tag);

        Assert.Contains("O int", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_ReferenceTypeParameter_UsesReferenceDiscriminatorR()
    {
        const string source = """
            namespace N
            {
                public class C { public void M(string s) { } }
            }
            """;
        IMethodSymbol m = FindMethod(source, "C", "M");
        MangledName mangled = Mangler.MangleMethod(m, Tag);

        Assert.Contains("R string", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_RefReadonlyParameter_EmitsKqExtensionAndXil2cpp179()
    {
        const string source = """
            namespace N
            {
                public class C { public void M(ref readonly int x) { } }
            }
            """;
        IMethodSymbol m = FindMethod(source, "C", "M");
        MangledName mangled = Mangler.MangleMethod(m, Tag);

        // The XIL2CPP-private _KQ_ forward-commit marker precedes the K
        // discriminator.
        Assert.Contains(Mangler.RefReadonlyExtensionMarker + "K int", mangled.CanonicalForm);

        DiagnosticRecord diag = Assert.Single(mangled.Diagnostics);
        Assert.Equal(DiagnosticCodes.ManglingDiscriminatorsMissing, diag.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
    }

    [Fact]
    public void MangleMethod_StaticMethod_HasNoImplicitSelf()
    {
        const string source = """
            namespace N
            {
                public class C { public static void M(int x) { } }
            }
            """;
        IMethodSymbol m = FindMethod(source, "C", "M");
        MangledName mangled = Mangler.MangleMethod(m, Tag);

        // No "R N::C" self; only the int param.
        Assert.Equal("_v1ab12cd34__N::C::M_P_(V int)", mangled.CanonicalForm);
    }

    [Fact]
    public void MangleMethod_GenericMethod_OneLevelArgs_EmitsGenericMarker()
    {
        const string source = """
            namespace N
            {
                public class C { public void M<T>(T t) { } }
            }
            """;
        IMethodSymbol open = FindMethod(source, "C", "M");
        // Construct the generic method with a known special type (int).
        IMethodSymbol withInt = open.Construct(IntType(source));
        MangledName mangled = Mangler.MangleMethod(withInt, Tag);

        Assert.Contains(Mangler.GenericArgMarker, mangled.CanonicalForm);
        Assert.Contains("<int>", mangled.CanonicalForm);
    }

    [Fact]
    public void CanonicalToLinkerSymbol_TranslatesPerContractSection23()
    {
        const string canonical = "_v1__A::B::C_P_(R A::B, V float)";
        Assert.Equal(
            "_v1__A__B__C_P_R_A__B_V_float",
            Mangler.CanonicalToLinkerSymbol(canonical));
    }

    // ---- helpers ----

    private static IMethodSymbol FindMethod(string source, string typeName, string methodName)
    {
        INamedTypeSymbol type = FindType(source, typeName);
        return type.GetMembers(methodName).OfType<IMethodSymbol>().First();
    }

    private static IMethodSymbol FindConstructor(string source, string typeName)
    {
        INamedTypeSymbol type = FindType(source, typeName);
        return type.InstanceConstructors.First();
    }

    private static IMethodSymbol FindStaticConstructor(string source, string typeName)
    {
        INamedTypeSymbol type = FindType(source, typeName);
        return type.StaticConstructors.First();
    }

    private static IMethodSymbol FindAccessor(string source, string typeName, string propertyName, bool isGetter)
    {
        INamedTypeSymbol type = FindType(source, typeName);
        IPropertySymbol property = type.GetMembers(propertyName).OfType<IPropertySymbol>().First();
        return (isGetter ? property.GetMethod : property.SetMethod)!;
    }

    private static IMethodSymbol FindMethodByKind(string source, string typeName, MethodKind kind)
    {
        INamedTypeSymbol type = FindType(source, typeName);
        return type.GetMembers().OfType<IMethodSymbol>().First(m => m.MethodKind == kind);
    }

    private static INamedTypeSymbol IntType(string source)
        => Compilation(source).GetSpecialType(SpecialType.System_Int32);

    private static INamedTypeSymbol FindType(string source, string typeName)
    {
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation = Compilation(source);
        INamedTypeSymbol? found = FindTypeRecursive(compilation.GlobalNamespace, typeName);
        Assert.NotNull(found);
        return found!;
    }

    private static INamedTypeSymbol? FindTypeRecursive(INamespaceOrTypeSymbol container, string typeName)
    {
        foreach (INamedTypeSymbol type in container.GetTypeMembers())
        {
            if (type.Name == typeName)
            {
                return type;
            }
        }
        if (container is INamespaceSymbol ns)
        {
            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            {
                INamedTypeSymbol? found = FindTypeRecursive(child, typeName);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation(string source)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        return pass1.Compilation;
    }
}
