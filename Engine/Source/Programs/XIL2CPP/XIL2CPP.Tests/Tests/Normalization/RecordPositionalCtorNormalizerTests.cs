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
/// Tests for <see cref="RecordPositionalCtorNormalizer"/> (WU-7) per
/// /Documents/XIL2CPP.html Rev 4 Section 3.2 (line 415) + Section 5.1 / 5.4:
/// each positional record / record-struct gets an explicit synthesized
/// surface (positional parameters, init-only properties, primary-ctor
/// assignments, and the value-semantics member flags) attached to its
/// <see cref="INamedTypeSymbol"/> and mirrored on its declaration node.
/// </summary>
/// <remarks>
/// Each test drives JUST this normalizer in isolation through the explicit-set
/// <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>
/// overload so the assertions are independent of the rest of the normalizer
/// roster.
/// </remarks>
public sealed class RecordPositionalCtorNormalizerTests
{
    // ---------------------------------------------------------------
    // Fixture 1: simple positional record.
    // ---------------------------------------------------------------

    [Fact]
    public void SimpleRecord_SynthesizesParametersInitPropertiesAndAssignments()
    {
        const string source = "namespace N; public record Point(int X, int Y);";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Point");
        SynthesizedRecordMembers members = AttachedMembers(unit, symbol);

        Assert.Equal("Point", members.RecordName);
        Assert.False(members.IsRecordStruct);

        // Positional parameters: name / type / index, in declaration order.
        Assert.Equal(2, members.Parameters.Count);
        Assert.Equal("X", members.Parameters[0].Name);
        Assert.Equal("int", members.Parameters[0].TypeDisplay);
        Assert.Equal(0, members.Parameters[0].Index);
        Assert.Equal("Y", members.Parameters[1].Name);
        Assert.Equal("int", members.Parameters[1].TypeDisplay);
        Assert.Equal(1, members.Parameters[1].Index);

        // One synthesized init-only property per parameter, same order.
        Assert.Equal(
            new[] { "X", "Y" },
            members.InitProperties.Select(p => p.Name).ToArray());
        Assert.All(members.InitProperties, p => Assert.Equal("int", p.TypeDisplay));

        // Primary-ctor body: this.X = X; this.Y = Y;
        Assert.Equal(2, members.PrimaryCtorAssignments.Count);
        Assert.Equal("X", members.PrimaryCtorAssignments[0].PropertyName);
        Assert.Equal("X", members.PrimaryCtorAssignments[0].ParameterName);
        Assert.Equal("Y", members.PrimaryCtorAssignments[1].PropertyName);
        Assert.Equal("Y", members.PrimaryCtorAssignments[1].ParameterName);

        // Compiler-generated value-semantics members Pass 6 will emit.
        Assert.True(members.GeneratesEquals);
        Assert.True(members.GeneratesGetHashCode);
        Assert.True(members.GeneratesDeconstruct);
        Assert.True(members.GeneratesToString);
    }

    [Fact]
    public void SimpleRecord_AnnotatesDeclarationNodeWithSameSurface()
    {
        const string source = "namespace N; public record Point(int X, int Y);";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        RecordDeclarationSyntax node = RecordNode(pass1, "Point");

        RecordPositionalCtorAnnotation? annotation =
            unit.GetAnnotation<RecordPositionalCtorAnnotation>(node);
        Assert.NotNull(annotation);
        Assert.Equal("record-positional-ctor", annotation!.Kind);
        Assert.Equal("Point", annotation.Members.RecordName);
        Assert.Equal(2, annotation.Members.Parameters.Count);

        // The node annotation carries the same value as the per-symbol attach.
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Point");
        SynthesizedRecordMembers attached = AttachedMembers(unit, symbol);
        Assert.Equal(attached, annotation.Members);
    }

    // ---------------------------------------------------------------
    // Fixture 2: nested record (record declared inside a class).
    // ---------------------------------------------------------------

    [Fact]
    public void NestedRecord_IsDiscoveredAndSynthesized()
    {
        const string source =
            "namespace N; public class Outer { public record Inner(string Label, long Count); }";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Inner");
        SynthesizedRecordMembers members = AttachedMembers(unit, symbol);

        Assert.Equal("Inner", members.RecordName);
        Assert.False(members.IsRecordStruct);
        Assert.Equal(2, members.Parameters.Count);
        Assert.Equal("Label", members.Parameters[0].Name);
        Assert.Equal("string", members.Parameters[0].TypeDisplay);
        Assert.Equal(0, members.Parameters[0].Index);
        Assert.Equal("Count", members.Parameters[1].Name);
        Assert.Equal("long", members.Parameters[1].TypeDisplay);
        Assert.Equal(1, members.Parameters[1].Index);

        Assert.Equal(
            new[] { "Label", "Count" },
            members.InitProperties.Select(p => p.Name).ToArray());
        Assert.True(members.GeneratesDeconstruct);
    }

    // ---------------------------------------------------------------
    // Fixture 3: generic record.
    // ---------------------------------------------------------------

    [Fact]
    public void GenericRecord_RecordsTypeParameterizedParameters()
    {
        const string source =
            "namespace N; public record Box<T>(T Value, int Tag);";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Box");
        SynthesizedRecordMembers members = AttachedMembers(unit, symbol);

        Assert.Equal("Box", members.RecordName);
        Assert.False(members.IsRecordStruct);
        Assert.Equal(2, members.Parameters.Count);

        // The first parameter's type is the type parameter T.
        Assert.Equal("Value", members.Parameters[0].Name);
        Assert.Equal("T", members.Parameters[0].TypeDisplay);
        Assert.Equal("Tag", members.Parameters[1].Name);
        Assert.Equal("int", members.Parameters[1].TypeDisplay);

        Assert.Equal(
            new[] { "Value", "Tag" },
            members.InitProperties.Select(p => p.Name).ToArray());
        Assert.Equal("T", members.InitProperties[0].TypeDisplay);
        Assert.True(members.GeneratesEquals);
        Assert.True(members.GeneratesDeconstruct);
    }

    // ---------------------------------------------------------------
    // Fixture 4: record struct.
    // ---------------------------------------------------------------

    [Fact]
    public void RecordStruct_FlaggedAsStructAndSynthesized()
    {
        const string source = "namespace N; public record struct Vec(double X, double Y);";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Vec");
        SynthesizedRecordMembers members = AttachedMembers(unit, symbol);

        Assert.Equal("Vec", members.RecordName);
        Assert.True(members.IsRecordStruct);
        Assert.Equal(2, members.Parameters.Count);
        Assert.Equal("X", members.Parameters[0].Name);
        Assert.Equal("double", members.Parameters[0].TypeDisplay);
        Assert.Equal("Y", members.Parameters[1].Name);
        Assert.Equal("double", members.Parameters[1].TypeDisplay);

        Assert.Equal(
            new[] { "X", "Y" },
            members.InitProperties.Select(p => p.Name).ToArray());
        Assert.Equal(2, members.PrimaryCtorAssignments.Count);
        Assert.True(members.GeneratesEquals);
        Assert.True(members.GeneratesGetHashCode);
        Assert.True(members.GeneratesDeconstruct);
        Assert.True(members.GeneratesToString);
    }

    // ---------------------------------------------------------------
    // Fixture 5: positional record with an EXTRA explicit member. The
    // explicit member (a computed property) must NOT appear in the
    // synthesized init-property list -- only the positional parameters do.
    // ---------------------------------------------------------------

    [Fact]
    public void PositionalRecordWithExtraExplicitMember_OnlyPositionalSurfaceIsSynthesized()
    {
        const string source =
            "namespace N; public record Coord(int X, int Y) { public int Sum => X + Y; }";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Coord");
        SynthesizedRecordMembers members = AttachedMembers(unit, symbol);

        // Only the two positional parameters are synthesized; the explicit
        // computed `Sum` property is NOT a synthesized init-property.
        Assert.Equal(2, members.Parameters.Count);
        Assert.Equal(
            new[] { "X", "Y" },
            members.InitProperties.Select(p => p.Name).ToArray());
        Assert.DoesNotContain("Sum", members.InitProperties.Select(p => p.Name));
        Assert.DoesNotContain("Sum", members.PrimaryCtorAssignments.Select(a => a.PropertyName));

        Assert.Equal(2, members.PrimaryCtorAssignments.Count);
        Assert.True(members.GeneratesDeconstruct);
    }

    // ---------------------------------------------------------------
    // Negative: a property-init record (no parameter list) carries no
    // synthesized positional surface, so this normalizer leaves it alone.
    // ---------------------------------------------------------------

    [Fact]
    public void PropertyInitRecord_IsNotAnnotated()
    {
        const string source =
            "namespace N; public record Person { public string Name { get; init; } = \"\"; public int Age { get; init; } }";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);
        INamedTypeSymbol symbol = RecordSymbol(pass1, "Person");
        RecordDeclarationSyntax node = RecordNode(pass1, "Person");

        Assert.Null(unit.GetAttached<SynthesizedRecordMembers>(symbol));
        Assert.Null(unit.GetAnnotation<RecordPositionalCtorAnnotation>(node));
    }

    [Fact]
    public void Run_ProducesNoDiagnostics()
    {
        const string source = "namespace N; public record Point(int X, int Y);";
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unit = Run(pass1);

        Assert.Empty(unit.Diagnostics);
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    private static NormalizedUnit Run(Pass1Result pass1)
        => Pass2Driver.Run(pass1, new INormalizer[] { new RecordPositionalCtorNormalizer() });

    private static RecordDeclarationSyntax RecordNode(Pass1Result pass1, string name)
    {
        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            foreach (RecordDeclarationSyntax node in file.Tree.GetRoot()
                .DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (node.Identifier.ValueText == name)
                {
                    return node;
                }
            }
        }

        throw new KeyNotFoundException($"No record declaration named '{name}'.");
    }

    private static INamedTypeSymbol RecordSymbol(Pass1Result pass1, string name)
    {
        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(file.Tree);
            foreach (RecordDeclarationSyntax node in file.Tree.GetRoot()
                .DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (node.Identifier.ValueText == name
                    && model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol)
                {
                    return symbol;
                }
            }
        }

        throw new KeyNotFoundException($"No bound record symbol named '{name}'.");
    }

    private static SynthesizedRecordMembers AttachedMembers(NormalizedUnit unit, INamedTypeSymbol symbol)
    {
        SynthesizedRecordMembers? members = unit.GetAttached<SynthesizedRecordMembers>(symbol);
        Assert.NotNull(members);
        return members!;
    }
}
