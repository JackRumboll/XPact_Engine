// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="SimpleTypePatternLoweringRule"/>: a simple type pattern
/// (a <see cref="TypePatternSyntax"/> as the whole <c>is</c>-pattern) lowers to
/// <c>dynamic_cast&lt;Foo*&gt;(&lt;e&gt;) != nullptr</c>; a declaration /
/// constant / other complex pattern emits a DEFERRED comment.
/// </summary>
/// <remarks>
/// Roslyn never parses a bare <c>o is Foo</c> as an
/// <see cref="IsPatternExpressionSyntax"/> (it is the legacy
/// <c>IsExpression</c>); a top-level <see cref="TypePatternSyntax"/> arises only
/// from synthesis / a parenthesized-pattern unwrap. So the simple-pattern test
/// grafts a synthesized <c>IsPatternExpression(TypePattern(Foo))</c> over a real
/// <c>o is Foo f</c> declaration pattern -- reusing its already-bound <c>Foo</c>
/// type reference and <c>o</c> receiver -- then drives the real pipeline so the
/// rule resolves the target type against a genuine semantic model.
/// </remarks>
public sealed class SimpleTypePatternLoweringRuleTests
{
    [Fact]
    public void CanHandle_OnlyIsPattern()
    {
        SimpleTypePatternLoweringRule rule = new();
        (EmitContext _, IsPatternExpressionSyntax isPattern) = GraftedSimpleTypePattern();

        Assert.True(rule.CanHandle(isPattern));
        Assert.False(rule.CanHandle(isPattern.Expression));
    }

    [Fact]
    public void SimpleTypePattern_EmitsDynamicCastNotNull()
    {
        SimpleTypePatternLoweringRule rule = new();
        (EmitContext ctx, IsPatternExpressionSyntax isPattern) = GraftedSimpleTypePattern();

        string cpp = Emit(rule, ctx, isPattern);

        Assert.StartsWith("dynamic_cast<::G::Foo*>(", cpp);
        Assert.EndsWith(") != nullptr", cpp);
        Assert.DoesNotContain("DEFERRED", cpp);
    }

    [Fact]
    public void DeclarationPattern_EmitsDeferred()
    {
        SimpleTypePatternLoweringRule rule = new();
        (EmitContext ctx, IsPatternExpressionSyntax isPattern) = ParsedIsPattern("o is Foo f");

        string cpp = Emit(rule, ctx, isPattern);

        Assert.Contains("DEFERRED(6.e):", cpp);
        Assert.Contains("not yet lowered", cpp);
        Assert.DoesNotContain("dynamic_cast", cpp);
    }

    [Fact]
    public void ConstantPattern_EmitsDeferred()
    {
        SimpleTypePatternLoweringRule rule = new();
        (EmitContext ctx, IsPatternExpressionSyntax isPattern) = ParsedIsPattern("o is null");

        string cpp = Emit(rule, ctx, isPattern);

        Assert.Contains("DEFERRED(6.e):", cpp);
        Assert.DoesNotContain("dynamic_cast", cpp);
    }

    // -----------------------------------------------------------------

    private const string Source =
        "namespace G { class Foo { } class C { bool M(object o) { return @EXPR@; } } }";

    /// <summary>
    /// Build an <see cref="EmitContext"/> over source containing
    /// <paramref name="expr"/> and return the first parsed
    /// <see cref="IsPatternExpressionSyntax"/>.
    /// </summary>
    private static (EmitContext, IsPatternExpressionSyntax) ParsedIsPattern(string expr)
    {
        (EmitContext ctx, SyntaxTree tree) = BuildContext(Source.Replace("@EXPR@", expr));
        IsPatternExpressionSyntax node = tree.GetRoot()
            .DescendantNodes().OfType<IsPatternExpressionSyntax>().First();
        return (ctx, node);
    }

    /// <summary>
    /// Build an <see cref="EmitContext"/> whose tree carries a synthesized
    /// <c>o is Foo</c> simple type pattern (grafted over a real <c>o is Foo f</c>
    /// declaration pattern so the <c>Foo</c> type and <c>o</c> receiver bind).
    /// </summary>
    private static (EmitContext, IsPatternExpressionSyntax) GraftedSimpleTypePattern()
    {
        // Parse the real declaration-pattern form first so 'Foo' / 'o' bind.
        SyntaxTree seed = CSharpSyntaxTree.ParseText(Source.Replace("@EXPR@", "o is Foo f"));
        IsPatternExpressionSyntax declForm = seed.GetRoot()
            .DescendantNodes().OfType<IsPatternExpressionSyntax>().First();
        DeclarationPatternSyntax declPattern = (DeclarationPatternSyntax)declForm.Pattern;

        // Replace the declaration pattern with a TypePattern reusing its 'Foo'.
        TypePatternSyntax typePattern = SyntaxFactory.TypePattern(declPattern.Type)
            .WithTriviaFrom(declPattern);
        IsPatternExpressionSyntax simpleForm = declForm.WithPattern(typePattern);

        SyntaxTree grafted = seed.WithRootAndOptions(
            seed.GetRoot().ReplaceNode(declForm, simpleForm), seed.Options);

        (EmitContext ctx, SyntaxTree tree) = BuildContextFromTree(grafted);
        IsPatternExpressionSyntax node = tree.GetRoot()
            .DescendantNodes().OfType<IsPatternExpressionSyntax>().First();
        Assert.IsType<TypePatternSyntax>(node.Pattern);
        return (ctx, node);
    }

    private static (EmitContext, SyntaxTree) BuildContext(string source)
        => BuildContextFromTree(CSharpSyntaxTree.ParseText(source));

    private static (EmitContext, SyntaxTree) BuildContextFromTree(SyntaxTree tree)
    {
        ModuleParser.ParsedFile parsed = new("Source0.cs", tree, new List<Diagnostic>());

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "TestModule",
            syntaxTrees: new[] { tree },
            references: FrontendTestHelpers.BclReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Pass1Result pass1 = new(
            "TestModule",
            new[] { parsed },
            compilation,
            new List<DiagnosticRecord>(),
            isSimPath: false);

        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new CrossModuleNoThrowAnalyzer() });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);
        ManglingTable manglingTable = Pass5Driver.Run(
            unit, tierTable, EmitTestHelpers.ContractVersionTag);

        EmitContext context = new(
            unit,
            pass3,
            tierTable,
            manglingTable,
            EmitTestHelpers.ContractVersionTag,
            EmitTestHelpers.GCRootAbi,
            EmitTestHelpers.ExceptionAbi,
            EmitTestHelpers.ManglingSchemeTag);

        return (context, tree);
    }

    private static string Emit(IBodyLoweringRule rule, EmitContext context, SyntaxNode node)
    {
        BodyLoweringRuleRegistry registry = new(new[] { rule });
        CppWriter writer = new();
        StatementEmitter emitter = new(context, writer, registry);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }
}
