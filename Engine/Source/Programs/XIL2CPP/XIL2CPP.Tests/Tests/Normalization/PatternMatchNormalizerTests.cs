// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="PatternMatchNormalizer"/> (XIL2CPP Phase 6.b, WU-11)
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.6. Each fixture builds a
/// Pass-1 result from synthetic C# source, runs JUST the pattern-match
/// normalizer in isolation via
/// <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>,
/// then asserts the lowered <see cref="PatternDecision"/> tree the normalizer
/// recorded on the pattern-bearing node.
/// </summary>
public sealed class PatternMatchNormalizerTests
{
    // ---------------------------------------------------------------
    // Fixture 1: is type-pattern.
    // ---------------------------------------------------------------

    [Fact]
    public void IsTypePattern_LowersToTypePatternDecisionWithBinding()
    {
        const string source = """
            namespace N;
            public class Animal { }
            public class Dog : Animal { }
            public class M
            {
                public bool IsDog(Animal a) => a is Dog d;
            }
            """;

        NormalizedUnit unit = Run(source);
        IsPatternExpressionSyntax node = First<IsPatternExpressionSyntax>(unit);

        PatternMatchAnnotation? ann = unit.GetAnnotation<PatternMatchAnnotation>(node);
        Assert.NotNull(ann);
        Assert.Equal("pattern-match", ann!.Kind);
        Assert.Equal(PatternConstructKind.IsPattern, ann.Construct);
        Assert.Empty(ann.Arms);

        TypePatternDecision type = Assert.IsType<TypePatternDecision>(ann.Decision);
        Assert.Equal("type", type.DecisionKind);
        Assert.Equal("d", type.BindingName);
        Assert.Contains("Dog", type.TypeName);
    }

    // ---------------------------------------------------------------
    // Fixture 2: switch expression.
    // ---------------------------------------------------------------

    [Fact]
    public void SwitchExpression_LowersEachArmInSourceOrder()
    {
        const string source = """
            namespace N;
            public class M
            {
                public string Run(int x) => x switch
                {
                    0 => "zero",
                    1 => "one",
                    _ => "many",
                };
            }
            """;

        NormalizedUnit unit = Run(source);
        SwitchExpressionSyntax node = First<SwitchExpressionSyntax>(unit);

        PatternMatchAnnotation? ann = unit.GetAnnotation<PatternMatchAnnotation>(node);
        Assert.NotNull(ann);
        Assert.Equal(PatternConstructKind.SwitchExpression, ann!.Construct);
        Assert.Equal(3, ann.Arms.Count);

        // Arm 0: constant 0.
        ConstantPatternDecision zero = Assert.IsType<ConstantPatternDecision>(ann.Arms[0].Decision);
        Assert.Equal("0", zero.ConstantText);
        Assert.False(ann.Arms[0].HasWhenGuard);

        // Arm 1: constant 1.
        ConstantPatternDecision one = Assert.IsType<ConstantPatternDecision>(ann.Arms[1].Decision);
        Assert.Equal("1", one.ConstantText);

        // Arm 2: discard catch-all.
        Assert.IsType<DiscardPatternDecision>(ann.Arms[2].Decision);
    }

    // ---------------------------------------------------------------
    // Fixture 3: list pattern.
    // ---------------------------------------------------------------

    [Fact]
    public void ListPattern_LowersToListDecisionWithSliceSubPattern()
    {
        const string source = """
            namespace N;
            public class M
            {
                public bool Match(int[] xs) => xs is [1, 2, .. var rest];
            }
            """;

        NormalizedUnit unit = Run(source);
        IsPatternExpressionSyntax node = First<IsPatternExpressionSyntax>(unit);

        PatternMatchAnnotation? ann = unit.GetAnnotation<PatternMatchAnnotation>(node);
        Assert.NotNull(ann);

        ListPatternDecision list = Assert.IsType<ListPatternDecision>(ann!.Decision);
        Assert.Equal("list", list.DecisionKind);

        // Two fixed (non-slice) elements: constants 1 and 2.
        Assert.Equal(2, list.Elements.Count);
        Assert.Equal("1", Assert.IsType<ConstantPatternDecision>(list.Elements[0]).ConstantText);
        Assert.Equal("2", Assert.IsType<ConstantPatternDecision>(list.Elements[1]).ConstantText);

        // Slice present at position 2 with a 'var rest' sub-pattern.
        Assert.True(list.HasSlice);
        Assert.Equal(2, list.SlicePosition);
        VarPatternDecision rest = Assert.IsType<VarPatternDecision>(list.SliceSubPattern);
        Assert.Equal("rest", rest.BindingName);
    }

    // ---------------------------------------------------------------
    // Fixture 4: relational pattern.
    // ---------------------------------------------------------------

    [Fact]
    public void RelationalPattern_LowersToRelationalDecision()
    {
        const string source = """
            namespace N;
            public class M
            {
                public bool Positive(int x) => x is > 0;
            }
            """;

        NormalizedUnit unit = Run(source);
        IsPatternExpressionSyntax node = First<IsPatternExpressionSyntax>(unit);

        PatternMatchAnnotation? ann = unit.GetAnnotation<PatternMatchAnnotation>(node);
        Assert.NotNull(ann);

        RelationalPatternDecision rel = Assert.IsType<RelationalPatternDecision>(ann!.Decision);
        Assert.Equal("relational", rel.DecisionKind);
        Assert.Equal(">", rel.Operator);
        Assert.Equal("0", rel.Operand);
    }

    // ---------------------------------------------------------------
    // Fixture 5: logical (and / or / not) pattern.
    // ---------------------------------------------------------------

    [Fact]
    public void LogicalPattern_LowersAndOrNotToLogicalDecisions()
    {
        const string source = """
            namespace N;
            public class M
            {
                public bool InRange(int x) => x is (> 0 and < 10) or not 42;
            }
            """;

        NormalizedUnit unit = Run(source);
        IsPatternExpressionSyntax node = First<IsPatternExpressionSyntax>(unit);

        PatternMatchAnnotation? ann = unit.GetAnnotation<PatternMatchAnnotation>(node);
        Assert.NotNull(ann);

        // Top level is 'or' over [ (> 0 and < 10), not 42 ].
        LogicalPatternDecision or = Assert.IsType<LogicalPatternDecision>(ann!.Decision);
        Assert.Equal(LogicalPatternOperator.Or, or.Operator);
        Assert.Equal(2, or.Operands.Count);

        // Left operand: 'and' over [ > 0, < 10 ].
        LogicalPatternDecision and = Assert.IsType<LogicalPatternDecision>(or.Operands[0]);
        Assert.Equal(LogicalPatternOperator.And, and.Operator);
        Assert.Equal(2, and.Operands.Count);
        Assert.Equal(">", Assert.IsType<RelationalPatternDecision>(and.Operands[0]).Operator);
        Assert.Equal("<", Assert.IsType<RelationalPatternDecision>(and.Operands[1]).Operator);

        // Right operand: 'not 42'.
        LogicalPatternDecision not = Assert.IsType<LogicalPatternDecision>(or.Operands[1]);
        Assert.Equal(LogicalPatternOperator.Not, not.Operator);
        Assert.Single(not.Operands);
        Assert.Equal("42", Assert.IsType<ConstantPatternDecision>(not.Operands[0]).ConstantText);
    }

    // ---------------------------------------------------------------
    // Fixture 6: pattern with a when-clause.
    // ---------------------------------------------------------------

    [Fact]
    public void WhenClause_WrapsArmDecisionInWhenGuard()
    {
        const string source = """
            namespace N;
            public class M
            {
                public string Run(int x) => x switch
                {
                    int n when n < 0 => "negative",
                    int n => "non-negative",
                };
            }
            """;

        NormalizedUnit unit = Run(source);
        SwitchExpressionSyntax node = First<SwitchExpressionSyntax>(unit);

        PatternMatchAnnotation? ann = unit.GetAnnotation<PatternMatchAnnotation>(node);
        Assert.NotNull(ann);
        Assert.Equal(2, ann!.Arms.Count);

        // Arm 0 carries a when guard wrapping its type-pattern decision.
        Assert.True(ann.Arms[0].HasWhenGuard);
        WhenGuardDecision guard = Assert.IsType<WhenGuardDecision>(ann.Arms[0].Decision);
        Assert.Equal("when-guard", guard.DecisionKind);
        TypePatternDecision guarded = Assert.IsType<TypePatternDecision>(guard.Inner);
        Assert.Equal("n", guarded.BindingName);

        // Arm 1 has no when guard: a bare type pattern.
        Assert.False(ann.Arms[1].HasWhenGuard);
        Assert.IsType<TypePatternDecision>(ann.Arms[1].Decision);
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    private static NormalizedUnit Run(string source)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        return Pass2Driver.Run(pass1, new INormalizer[] { new PatternMatchNormalizer() });
    }

    private static T First<T>(NormalizedUnit unit) where T : SyntaxNode
        => unit.Pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<T>().First();
}
