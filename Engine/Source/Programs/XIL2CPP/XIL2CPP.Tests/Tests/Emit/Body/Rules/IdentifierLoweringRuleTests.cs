// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="IdentifierLoweringRule"/>: <c>this</c> lowers to the
/// <c>self</c> receiver token; <c>base</c> lowers to a pointer cast of
/// <c>self</c>; a local / parameter lowers to its verbatim name; an instance
/// field lowers to <c>self-&gt;&lt;field&gt;</c>; a static field lowers to the
/// qualified <c>&lt;Type&gt;::&lt;field&gt;</c> form; and a bare instance
/// property read lowers identically to its member-access form.
/// </summary>
public sealed class IdentifierLoweringRuleTests
{
    [Fact]
    public void This_LowersToSelfToken()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public Widget Self() { return this; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        ThisExpressionSyntax thisExpr = First<ThisExpressionSyntax>(ctx);
        string output = Lower(ctx, thisExpr);

        Assert.Equal("self", output);
    }

    [Fact]
    public void Base_LowersToStaticCastOfSelf()
    {
        const string source = """
            namespace Game
            {
                public class Animal
                {
                    public virtual int Legs() { return 4; }
                }

                public class Dog : Animal
                {
                    public override int Legs() { return base.Legs(); }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        BaseExpressionSyntax baseExpr = First<BaseExpressionSyntax>(ctx);
        string output = Lower(ctx, baseExpr);

        // base -> static_cast<::Game::Animal*>(self): a receiver cast fragment.
        Assert.Equal("static_cast<::Game::Animal*>(self)", output);
    }

    [Fact]
    public void Local_LowersToVerbatimName()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Compute()
                    {
                        int total = 7;
                        return total;
                    }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IdentifierNameSyntax reference = ReturnedIdentifier(ctx, "total");
        string output = Lower(ctx, reference);

        Assert.Equal("total", output);
    }

    [Fact]
    public void Parameter_LowersToVerbatimName()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Echo(int n) { return n; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IdentifierNameSyntax reference = ReturnedIdentifier(ctx, "n");
        string output = Lower(ctx, reference);

        Assert.Equal("n", output);
    }

    [Fact]
    public void InstanceField_LowersToSelfArrowField()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Count;
                    public int Read() { return Count; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IdentifierNameSyntax reference = ReturnedIdentifier(ctx, "Count");
        string output = Lower(ctx, reference);

        Assert.Equal("self->Count", output);
    }

    [Fact]
    public void StaticField_LowersToQualifiedTypeScopedName()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public static int Shared;
                    public int Read() { return Shared; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IdentifierNameSyntax reference = ReturnedIdentifier(ctx, "Shared");
        string output = Lower(ctx, reference);

        Assert.Equal("::Game::Widget::Shared", output);
    }

    [Fact]
    public void BareIdentifier_AndThisDotMember_LowerIdentically()
    {
        // Two equivalent reads of the same instance field: a bare `Count` and a
        // `this.Count`. With both this rule + the member-access rule registered,
        // they MUST lower to the identical C++ (self->Count).
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Count;
                    public int ReadBare() { return Count; }
                    public int ReadThis() { return this.Count; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);

        IdentifierNameSyntax bare = ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Select(r => r.Expression)
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.ValueText == "Count");

        MemberAccessExpressionSyntax thisDot = ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .First(m => m.Name.Identifier.ValueText == "Count");

        string bareOutput = Lower(ctx, bare);
        string thisDotOutput = Lower(ctx, thisDot);

        Assert.Equal("self->Count", bareOutput);
        Assert.Equal(bareOutput, thisDotOutput);
    }

    [Fact]
    public void InstanceProperty_LowersToGetterLinkerSymbolCallOnSelf()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Health { get; set; }
                    public int Read() { return Health; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IdentifierNameSyntax reference = ReturnedIdentifier(ctx, "Health");
        string output = Lower(ctx, reference);

        string getterSymbol = GetterLinkerSymbol(ctx, reference);
        // <get_LinkerSymbol>(self) -- the implicit `this` is the self argument.
        Assert.Equal(getterSymbol + "(self)", output);
    }

    /// <summary>
    /// Lower <paramref name="node"/> through a statement emitter whose registry
    /// holds the identifier rule + the member-access rule (so a property read
    /// resolves its getter and a <c>this.x</c> access lowers its <c>this</c>
    /// receiver consistently).
    /// </summary>
    private static string Lower(EmitContext ctx, SyntaxNode node)
    {
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new IdentifierLoweringRule(),
            new MemberAccessLoweringRule(),
        });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }

    private static T First<T>(EmitContext ctx) where T : SyntaxNode
        => ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<T>()
            .First();

    /// <summary>
    /// The <see cref="IdentifierNameSyntax"/> that is the direct returned
    /// expression of a <c>return &lt;name&gt;;</c> statement (a value reference
    /// site, never a type-name occurrence).
    /// </summary>
    private static IdentifierNameSyntax ReturnedIdentifier(EmitContext ctx, string name)
        => ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Select(r => r.Expression)
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.ValueText == name);

    private static string GetterLinkerSymbol(EmitContext ctx, IdentifierNameSyntax reference)
    {
        SemanticModel model = ctx.GetSemanticModel(reference.SyntaxTree);
        var property = (IPropertySymbol)model.GetSymbolInfo(reference).Symbol!;
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(property.GetMethod!))!.Value;
        return record.LinkerSymbol;
    }
}
