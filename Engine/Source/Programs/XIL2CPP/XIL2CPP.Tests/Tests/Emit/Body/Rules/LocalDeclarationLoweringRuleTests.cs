// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="LocalDeclarationLoweringRule"/> (WU-D1): a <c>var</c>
/// declaration lowers to <c>auto</c>, an explicit-type declaration lowers to the
/// resolved C++ type spelling (the fixed-width <c>&lt;cstdint&gt;</c> primitive
/// forms), and a declaration with no initializer omits the <c>=</c>. The rule is
/// exercised through a real <see cref="StatementEmitter"/> over a registry
/// containing JUST this rule (the foundation plug-in contract).
/// </summary>
public sealed class LocalDeclarationLoweringRuleTests
{
    private static string Lower(string methodBodyStatement, string declaredType = "int")
    {
        // A self-contained class whose single method holds the target local
        // declaration; the EmitContext is built from this same source so the
        // node's tree is in the context's parsed files (semantic-model lookup
        // succeeds in production form).
        string source =
            $$"""
            namespace Game
            {
                public class Widget
                {
                    public void M({{declaredType}} seed)
                    {
                        {{methodBodyStatement}}
                    }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        LocalDeclarationStatementSyntax decl = FindFirstLocalDeclaration(ctx);

        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new LocalDeclarationLoweringRule(),
        });
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.EmitStatement(decl);
        return writer.Build();
    }

    private static LocalDeclarationStatementSyntax FindFirstLocalDeclaration(EmitContext ctx)
    {
        foreach (ModuleParser.ParsedFile parsed in ctx.Unit.Pass1.ParsedFiles)
        {
            LocalDeclarationStatementSyntax? found = parsed.Tree.GetRoot()
                .DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        Assert.Fail("no LocalDeclarationStatementSyntax found in the emit context's parsed files");
        return null!;
    }

    [Fact]
    public void VarDeclaration_LowersToAuto()
    {
        // The initializer "1 + 2" has no rule in this single-rule registry, so
        // it falls to the expression emitter's TODO comment; the assertion
        // covers the declaration's "auto x = " lead-in + the trailing ";".
        string cpp = Lower("var x = 5;");

        Assert.StartsWith("auto x = ", cpp);
        Assert.EndsWith(";\n", cpp);
    }

    [Fact]
    public void ExplicitIntDeclaration_LowersToInt32T()
    {
        string cpp = Lower("int x = 5;");

        Assert.StartsWith("int32_t x = ", cpp);
        Assert.EndsWith(";\n", cpp);
        Assert.DoesNotContain("auto", cpp);
    }

    [Fact]
    public void ExplicitBoolDeclaration_LowersToBool()
    {
        string cpp = Lower("bool ok = true;");

        Assert.StartsWith("bool ok = ", cpp);
    }

    [Fact]
    public void ExplicitFloatDeclaration_LowersToFloat()
    {
        string cpp = Lower("float f = 1.0f;");

        Assert.StartsWith("float f = ", cpp);
    }

    [Fact]
    public void ExplicitLongDeclaration_LowersToInt64T()
    {
        string cpp = Lower("long n = 5L;");

        Assert.StartsWith("int64_t n = ", cpp);
    }

    [Fact]
    public void ExplicitCharDeclaration_LowersToChar16T()
    {
        // C# char is a 16-bit UTF-16 code unit -> char16_t (NOT wchar_t),
        // per /Documents/XIL2CPP.html Rev 4 Section 5.3 (FIX-B-HIGH-23).
        string cpp = Lower("char c = 'a';");

        Assert.StartsWith("char16_t c = ", cpp);
        Assert.DoesNotContain("wchar_t", cpp);
    }

    [Fact]
    public void DeclarationWithoutInitializer_OmitsEquals()
    {
        string cpp = Lower("int x;");

        Assert.Equal("int32_t x;\n", cpp);
    }

    [Fact]
    public void SpellCppType_MapsEachPrimitiveToItsCstdintForm()
    {
        // A direct, source-free check of the primitive mapping table so the
        // ABI-locked spelling of every primitive is pinned independent of
        // statement emission.
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        Compilation compilation = ctx.Unit.Pass1.Compilation;

        Assert.Equal("void", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Void)));
        Assert.Equal("bool", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Boolean)));
        Assert.Equal("char16_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Char)));
        Assert.Equal("int8_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_SByte)));
        Assert.Equal("uint8_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Byte)));
        Assert.Equal("int16_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Int16)));
        Assert.Equal("uint16_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_UInt16)));
        Assert.Equal("int32_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Int32)));
        Assert.Equal("uint32_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_UInt32)));
        Assert.Equal("int64_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Int64)));
        Assert.Equal("uint64_t", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_UInt64)));
        Assert.Equal("float", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Single)));
        Assert.Equal("double", LocalDeclarationLoweringRule.SpellCppType(Sp(compilation, SpecialType.System_Double)));
    }

    [Fact]
    public void SpellCppType_NullType_ReturnsNull()
        => Assert.Null(LocalDeclarationLoweringRule.SpellCppType(null));

    // =================================================================
    // FIX 4: an explicit-typed XObject-derived (reference) local is spelled as
    // a POINTER over its `::`-qualified C++ name (matching the method / member
    // emitters), so the declared type agrees with its pointer-typed initializer.
    // =================================================================

    [Fact]
    public void ExplicitXObjectDerivedLocal_LowersToQualifiedPointer()
    {
        string cpp = LowerXObjectLocal(
            "Actor chosen = fallback;",
            paramType: "Actor");

        // The explicit XObject-derived type spells as `::Game::Actor*`; the
        // pointer-typed parameter initializer (`fallback`) agrees with it.
        Assert.StartsWith("::Game::Actor* chosen = ", cpp, System.StringComparison.Ordinal);
        Assert.EndsWith(";\n", cpp);
        Assert.DoesNotContain("auto", cpp);
        // NOT the old, bare-value-name (type-mismatch) spelling.
        Assert.DoesNotContain("Actor chosen", cpp.Replace("::Game::Actor*", string.Empty));
    }

    [Fact]
    public void VarXObjectDerivedLocal_StillLowersToAuto()
    {
        // var/auto already deduces the pointer type and must be left unchanged.
        string cpp = LowerXObjectLocal(
            "var chosen = fallback;",
            paramType: "Actor");

        Assert.StartsWith("auto chosen = ", cpp, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Lower a method-body statement whose method takes a single XObject-derived
    /// parameter (so an XObject local can be initialised from it). The XObject
    /// stub is recognised by AnalyzerHelpers.IsXObjectDerived's metadata-name +
    /// namespace fallback. The registry pairs the local rule with the identifier
    /// rule so the initializer lowers to real C++.
    /// </summary>
    private static string LowerXObjectLocal(string methodBodyStatement, string paramType)
    {
        const string xobjectStub =
            "namespace XPact.CoreXObject { public abstract class XObject { } }";
        string source =
            $$"""
            namespace Game
            {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Widget
                {
                    public void M({{paramType}} fallback)
                    {
                        {{methodBodyStatement}}
                    }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(xobjectStub, source);
        LocalDeclarationStatementSyntax decl = FindFirstLocalDeclaration(ctx);

        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new LocalDeclarationLoweringRule(),
            new IdentifierLoweringRule(),
        });
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.EmitStatement(decl);
        return writer.Build();
    }

    private static ITypeSymbol Sp(Compilation compilation, SpecialType special)
        => compilation.GetSpecialType(special);
}
