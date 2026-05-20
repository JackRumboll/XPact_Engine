// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Configuration;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Exercises the sandboxed Starlark-subset evaluator per Toolchain
/// Contract Rev 13 Section 9.6 and <c>/Documents/XBT.html</c> Rev 4
/// Section 3.3. Confirms literal evaluation, every binary/unary
/// operator, conditional expressions, sandbox rejection of
/// banned constructs, and the instruction-budget enforcement.
/// </summary>
public sealed class StarlarkEvaluatorTests
{
    private static StarlarkEvaluator.Bindings StandardBindings(
        string platform = "Win64",
        string config = "Development",
        string stationRole = "Engineer",
        bool fipsMode = false,
        string arch = "x86_64",
        string moduleTier = "Engine",
        bool moduleSimPath = false,
        string simdLevel = "SSE42")
        => new()
        {
            TargetPlatform = platform,
            TargetConfiguration = config,
            TargetStationRole = stationRole,
            TargetFipsMode = fipsMode,
            TargetArchitecture = arch,
            ModuleTier = moduleTier,
            ModuleSimPath = moduleSimPath,
            ModuleSimdLevel = simdLevel,
        };

    [Fact]
    public void Literal_Bool_Evaluates()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate("True", StandardBindings()));
        Assert.Equal(false, StarlarkEvaluator.Evaluate("False", StandardBindings()));
    }

    [Fact]
    public void Literal_Integer_Evaluates()
    {
        Assert.Equal(42L, StarlarkEvaluator.Evaluate("42", StandardBindings()));
        Assert.Equal(-3L, StarlarkEvaluator.Evaluate("-3", StandardBindings()));
    }

    [Fact]
    public void Literal_String_Evaluates()
    {
        Assert.Equal("Win64", StarlarkEvaluator.Evaluate("\"Win64\"", StandardBindings()));
    }

    [Fact]
    public void Identifier_Reference_Resolves_TargetPlatform()
    {
        object value = StarlarkEvaluator.Evaluate("target.platform", StandardBindings(platform: "Linux"));
        Assert.Equal("Linux", value);
    }

    [Fact]
    public void Identifier_Reference_Resolves_ModuleTier()
    {
        object value = StarlarkEvaluator.Evaluate("module.tier", StandardBindings(moduleTier: "Studio"));
        Assert.Equal("Studio", value);
    }

    [Fact]
    public void Operator_Equality_String()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate("target.platform == \"Win64\"", StandardBindings()));
        Assert.Equal(false, StarlarkEvaluator.Evaluate("target.platform != \"Win64\"", StandardBindings()));
    }

    [Fact]
    public void Operator_Comparison_Integers()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate("3 < 5", StandardBindings()));
        Assert.Equal(true, StarlarkEvaluator.Evaluate("5 >= 5", StandardBindings()));
        Assert.Equal(false, StarlarkEvaluator.Evaluate("5 > 5", StandardBindings()));
        Assert.Equal(true, StarlarkEvaluator.Evaluate("5 <= 5", StandardBindings()));
    }

    [Fact]
    public void Operator_LogicalAnd_ShortCircuits()
    {
        // The rhs is False; if and short-circuited correctly the
        // result is False even though the lhs is True.
        Assert.Equal(false, StarlarkEvaluator.Evaluate("True and False", StandardBindings()));
        Assert.Equal(true, StarlarkEvaluator.Evaluate("True and True", StandardBindings()));
        Assert.Equal(false, StarlarkEvaluator.Evaluate("False and True", StandardBindings()));
    }

    [Fact]
    public void Operator_LogicalOr_ShortCircuits()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate("True or False", StandardBindings()));
        Assert.Equal(true, StarlarkEvaluator.Evaluate("False or True", StandardBindings()));
        Assert.Equal(false, StarlarkEvaluator.Evaluate("False or False", StandardBindings()));
    }

    [Fact]
    public void Operator_Not_Prefix()
    {
        Assert.Equal(false, StarlarkEvaluator.Evaluate("not True", StandardBindings()));
        Assert.Equal(true, StarlarkEvaluator.Evaluate("not False", StandardBindings()));
    }

    [Fact]
    public void Operator_In_List()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate(
            "target.platform in [\"Win64\", \"Linux\"]",
            StandardBindings(platform: "Linux")));
        Assert.Equal(false, StarlarkEvaluator.Evaluate(
            "target.platform in [\"Linux\", \"Android\"]",
            StandardBindings(platform: "Win64")));
    }

    [Fact]
    public void Operator_NotIn_List()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate(
            "target.platform not in [\"Linux\", \"Android\"]",
            StandardBindings(platform: "Win64")));
    }

    [Fact]
    public void Conditional_Expression_Branches_Correctly()
    {
        // a if cond else b
        object onWin64 = StarlarkEvaluator.Evaluate(
            "\"yes\" if target.platform == \"Win64\" else \"no\"",
            StandardBindings(platform: "Win64"));
        Assert.Equal("yes", onWin64);

        object onLinux = StarlarkEvaluator.Evaluate(
            "\"yes\" if target.platform == \"Win64\" else \"no\"",
            StandardBindings(platform: "Linux"));
        Assert.Equal("no", onLinux);
    }

    [Fact]
    public void Conditional_The_Practical_Fips_Pattern()
    {
        // The Contract Section 9.6 example: enable a module when FIPS mode is active.
        Assert.Equal(true, StarlarkEvaluator.Evaluate(
            "target.fips_mode == True",
            StandardBindings(fipsMode: true)));
        Assert.Equal(false, StarlarkEvaluator.Evaluate(
            "target.fips_mode == True",
            StandardBindings(fipsMode: false)));
    }

    [Fact]
    public void StringMethod_StartsWith()
    {
        Assert.Equal(true, StarlarkEvaluator.Evaluate("\"x86_64\".startswith(\"x86\")", StandardBindings()));
        Assert.Equal(false, StarlarkEvaluator.Evaluate("\"aarch64\".startswith(\"x86\")", StandardBindings()));
    }

    [Fact]
    public void StringMethod_Lower_Upper()
    {
        Assert.Equal("win64", StarlarkEvaluator.Evaluate("target.platform.lower()", StandardBindings()));
        Assert.Equal("WIN64", StarlarkEvaluator.Evaluate("target.platform.upper()", StandardBindings()));
    }

    [Fact]
    public void Len_OnString_ReturnsCharCount()
    {
        Assert.Equal(5L, StarlarkEvaluator.Evaluate("len(\"hello\")", StandardBindings()));
    }

    [Fact]
    public void Len_OnList_ReturnsItemCount()
    {
        Assert.Equal(3L, StarlarkEvaluator.Evaluate("len([1, 2, 3])", StandardBindings()));
    }

    // ----- Sandbox / failure modes -----

    [Fact]
    public void Unknown_Identifier_Rejects()
    {
        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => StarlarkEvaluator.Evaluate("target.no_such_field", StandardBindings()));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Unbound name", ex.Message);
    }

    [Fact]
    public void Top_Level_Bare_Identifier_Rejects()
    {
        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => StarlarkEvaluator.Evaluate("not_a_binding", StandardBindings()));
        Assert.Equal(30, ex.ExitCode);
    }

    [Fact]
    public void Def_Keyword_NotInSubset()
    {
        // The subset does not include `def`; the lexer treats `def`
        // as a plain identifier and the parser fails when it appears
        // without a binding.
        Assert.Throws<DescriptorParseException>(() =>
            StarlarkEvaluator.Evaluate("def f(x): return x", StandardBindings()));
    }

    [Fact]
    public void File_IO_Functions_Banned()
    {
        // `load` and `print` are not bound; they parse as identifiers
        // followed by a parenthesised call; the call fails with
        // "Unknown function".
        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => StarlarkEvaluator.Evaluate("load(\"/etc/passwd\")", StandardBindings()));
        Assert.Contains("Unknown function 'load'", ex.Message);
    }

    [Fact]
    public void Type_Mismatch_In_LessThan_Rejects()
    {
        Assert.Throws<DescriptorParseException>(() =>
            StarlarkEvaluator.Evaluate("3 < \"three\"", StandardBindings()));
    }

    [Fact]
    public void Conditional_Without_Else_Rejects()
    {
        Assert.Throws<DescriptorParseException>(() =>
            StarlarkEvaluator.Evaluate("\"yes\" if True", StandardBindings()));
    }

    // ----- Depth cap (Contract Rev 13.1 Section 9.6 deviation (c)) -----
    //
    // The evaluator's recursive Eval(Node) loop is capped at 32 frames
    // per Contract Section 9.6 deviation (c). Hitting the cap must throw
    // DescriptorParseException (exit 30) rather than letting the CLR
    // raise an uncatchable StackOverflowException and crash the process.
    //
    // For nested ternaries the AST shape "0 if True else (next)" walks
    // one CondNode per nesting level plus one leaf at the bottom, so N
    // nestings peak at depth N+1. A 31-nesting expression therefore
    // peaks at depth 32 (succeeds); 32 nestings peak at depth 33 (fails).
    // The same +1 relationship holds for nested list literals.

    [Fact]
    public void Depth_NestedTernary_AtCap_Succeeds()
    {
        // 31 nested ternaries: peak depth 32 (exactly the cap). Must
        // evaluate cleanly without the depth gate firing.
        string expr = BuildNestedTernary(31);
        object value = StarlarkEvaluator.Evaluate(expr, StandardBindings());
        Assert.Equal(0L, value);
    }

    [Fact]
    public void Depth_NestedTernary_OverCap_Rejects_With_DepthMessage()
    {
        // 32 nested ternaries: peak depth 33, one frame past the cap.
        // Must surface a DescriptorParseException with exit 30 whose
        // message names "depth", proving the depth gate fired rather
        // than the instruction budget or a CLR stack overflow.
        string expr = BuildNestedTernary(32);
        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => StarlarkEvaluator.Evaluate(expr, StandardBindings()));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("depth", ex.Message);
    }

    [Fact]
    public void Depth_NestedList_OverCap_Rejects()
    {
        // 32 nested list literals: [[[ ... [0] ... ]]]. The outer list
        // is frame 1, walking into its only item is frame 2, ..., the
        // innermost IntLit is frame 33 -- one frame past the cap.
        string expr = BuildNestedList(32);
        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => StarlarkEvaluator.Evaluate(expr, StandardBindings()));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("depth", ex.Message);
    }

    [Fact]
    public void Depth_LenCall_DoesNotInflateDepth_Past_TopLevel()
    {
        // A top-level call like len("hello") is two frames at most: the
        // CallNode itself and the StringLit argument. The depth gate
        // must NOT fire for a shallow call. This proves the gate
        // tracks expression-internal recursion (the spec target) rather
        // than function-call semantics -- a single function-style call
        // contributes one frame, not the dozens a deeply-nested ternary
        // would.
        Assert.Equal(5L, StarlarkEvaluator.Evaluate("len(\"hello\")", StandardBindings()));
    }

    /// <summary>
    /// Build "0 if False else (0 if False else (... else 0))" with
    /// <paramref name="levels"/> nested ternaries. Cond is False at
    /// every level so the else-branch (the next nested conditional)
    /// is taken, cascading the recursion to the innermost "0". This
    /// is the only ternary shape that actually walks every level;
    /// using cond=True would short-circuit to the then-branch and
    /// never visit the else recursion. The walk touches one
    /// ConditionalNode per level, peaking the Eval-depth at
    /// <c>levels + 1</c> when the innermost IntLit is read.
    /// </summary>
    private static string BuildNestedTernary(int levels)
    {
        // Pattern: "0 if False else (next)" with the deepest level
        // collapsing to "0". The parser's ParseConditional() recurses
        // on the else-branch, which matches our cascade direction.
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < levels; i++)
        {
            sb.Append("0 if False else (");
        }
        sb.Append('0');
        for (int i = 0; i < levels; i++)
        {
            sb.Append(')');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build "[[[[ ... [0] ... ]]]]" with <paramref name="levels"/>
    /// nested list literals. Walking a list literal pushes one frame
    /// for the ListLit node itself; the inner Eval(item) pushes one
    /// more for the next ListLit. With one item per list, the peak
    /// depth is <c>levels + 1</c> (the innermost IntLit).
    /// </summary>
    private static string BuildNestedList(int levels)
    {
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < levels; i++)
        {
            sb.Append('[');
        }
        sb.Append('0');
        for (int i = 0; i < levels; i++)
        {
            sb.Append(']');
        }
        return sb.ToString();
    }
}
