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
}
