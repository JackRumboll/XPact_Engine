// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="PropertyEmitter"/> (WU-E3): the getter / setter
/// <c>extern "C"</c> accessor shapes that read / write the synthesized
/// <c>__BackingField_&lt;Name&gt;</c> slot, the linker-symbol resolution through
/// the Pass-5 mangling table, and the 6.h <c>XPACT_GC_STORE</c> hook comment
/// that is emitted only for an XObject-derived property setter.
/// </summary>
public sealed class PropertyEmitterTests
{
    // A locally-declared stand-in for the engine root reference type, matching
    // the analysis tests. AnalyzerHelpers.IsXObjectDerived recognises it via
    // the metadata-name + namespace fallback.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    /// <summary>
    /// Build an emit context + resolve the property symbol named
    /// <paramref name="propertyName"/> from the pipeline run over
    /// <paramref name="sources"/>.
    /// </summary>
    private static (EmitContext Context, IPropertySymbol Property) BuildFor(
        string propertyName,
        params string[] sources)
    {
        (NormalizedUnit unit, Pass3Result pass3, TierTable tierTable, ManglingTable manglingTable) =
            EmitTestHelpers.RunPipeline(sources);

        EmitContext context = new(
            unit,
            pass3,
            tierTable,
            manglingTable,
            EmitTestHelpers.ContractVersionTag,
            EmitTestHelpers.GCRootAbi,
            EmitTestHelpers.ExceptionAbi,
            EmitTestHelpers.ManglingSchemeTag);

        IPropertySymbol property = FindProperty(unit, propertyName);
        return (context, property);
    }

    private static IPropertySymbol FindProperty(NormalizedUnit unit, string propertyName)
    {
        foreach (ModuleParser.ParsedFile parsed in unit.Pass1.ParsedFiles)
        {
            SemanticModel model = unit.Pass1.GetSemanticModel(parsed.Tree);
            foreach (PropertyDeclarationSyntax decl in parsed.Tree.GetRoot()
                .DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is IPropertySymbol prop
                    && prop.Name == propertyName)
                {
                    return prop;
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Property '{propertyName}' not found.");
    }

    private static string LinkerSymbolOf(EmitContext context, IMethodSymbol accessor)
    {
        ManglingRecord? record = context.FindMangling(StableId.FromSymbol(accessor));
        Assert.NotNull(record);
        return record!.Value.LinkerSymbol;
    }

    // -----------------------------------------------------------------
    // Getter shape.
    // -----------------------------------------------------------------

    [Fact]
    public void EmitGetter_ValueProperty_ReadsBackingFieldWithExternCNoexcept()
    {
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Health",
            "namespace Game { public class Widget { public int Health { get; set; } } }");

        CppWriter writer = new();
        new PropertyEmitter().EmitGetter(prop, prop.GetMethod!, ctx, writer);

        string getSym = LinkerSymbolOf(ctx, prop.GetMethod!);
        string expected =
            "extern \"C\" int32_t " + getSym + "(::Game::Widget* self) noexcept {\n"
            + "    return self->__BackingField_Health;\n"
            + "}\n";

        Assert.Equal(expected, writer.Build());
    }

    [Fact]
    public void EmitGetter_StringProperty_UsesFStringReturnType()
    {
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Label",
            "namespace Game { public class Widget { public string Label { get; set; } } }");

        CppWriter writer = new();
        new PropertyEmitter().EmitGetter(prop, prop.GetMethod!, ctx, writer);

        Assert.Contains("extern \"C\" ::XCore::Container::FString ", writer.Build());
        Assert.Contains("return self->__BackingField_Label;", writer.Build());
    }

    // -----------------------------------------------------------------
    // Setter shape -- value type (no 6.h hook).
    // -----------------------------------------------------------------

    [Fact]
    public void EmitSetter_ValueProperty_WritesBackingFieldWithoutGcHook()
    {
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Health",
            "namespace Game { public class Widget { public int Health { get; set; } } }");

        CppWriter writer = new();
        new PropertyEmitter().EmitSetter(prop, prop.SetMethod!, ctx, writer);

        string setSym = LinkerSymbolOf(ctx, prop.SetMethod!);
        string expected =
            "extern \"C\" void " + setSym + "(::Game::Widget* self, int32_t value) noexcept {\n"
            + "    self->__BackingField_Health = value;\n"
            + "}\n";

        Assert.Equal(expected, writer.Build());
        Assert.DoesNotContain("TODO(6.h)", writer.Build());
        Assert.DoesNotContain("XPACT_GC_STORE", writer.Build());
    }

    // -----------------------------------------------------------------
    // Setter shape -- XObject reference (emits 6.h hook).
    // -----------------------------------------------------------------

    [Fact]
    public void EmitSetter_XObjectProperty_EmitsGcStoreHookComment()
    {
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Target",
            XObjectStub,
            @"namespace Game {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Widget : XObject { public Actor Target { get; set; } }
            }");

        CppWriter writer = new();
        new PropertyEmitter().EmitSetter(prop, prop.SetMethod!, ctx, writer);

        string output = writer.Build();
        string setSym = LinkerSymbolOf(ctx, prop.SetMethod!);

        // XPtr<Actor> value type for the XObject-derived slot.
        Assert.Contains(
            "extern \"C\" void " + setSym + "(::Game::Widget* self, XPtr<Actor> value) noexcept {",
            output);
        // The 6.h hook comment precedes the field store.
        Assert.Contains(
            "// TODO(6.h): XPACT_GC_STORE(self, &self->__BackingField_Target, value) "
            + "when T is XObject-derived",
            output);
        Assert.Contains("self->__BackingField_Target = value;", output);

        // The hook comment is emitted before the assignment.
        int hookIndex = output.IndexOf("TODO(6.h)", System.StringComparison.Ordinal);
        int storeIndex = output.IndexOf("__BackingField_Target = value", System.StringComparison.Ordinal);
        Assert.True(hookIndex >= 0 && storeIndex > hookIndex);
    }

    // -----------------------------------------------------------------
    // Emit drives both accessors; read-only / write-only properties.
    // -----------------------------------------------------------------

    [Fact]
    public void Emit_ReadWriteProperty_EmitsGetterThenSetter()
    {
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Health",
            "namespace Game { public class Widget { public int Health { get; set; } } }");

        CppWriter writer = new();
        new PropertyEmitter().Emit(prop, ctx, writer);

        string output = writer.Build();
        string getSym = LinkerSymbolOf(ctx, prop.GetMethod!);
        string setSym = LinkerSymbolOf(ctx, prop.SetMethod!);

        int getIndex = output.IndexOf(getSym, System.StringComparison.Ordinal);
        int setIndex = output.IndexOf(setSym, System.StringComparison.Ordinal);
        Assert.True(getIndex >= 0);
        Assert.True(setIndex > getIndex);
    }

    [Fact]
    public void Emit_ReadOnlyAutoProperty_EmitsOnlyGetter()
    {
        // An auto-property with only a getter still synthesizes a backing field;
        // only the getter accessor exists, so only it is emitted.
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Id",
            "namespace Game { public class Widget { public int Id { get; } } }");

        CppWriter writer = new();
        new PropertyEmitter().Emit(prop, ctx, writer);

        string output = writer.Build();
        Assert.Contains("return self->__BackingField_Id;", output);
        Assert.DoesNotContain("= value;", output);
    }

    // -----------------------------------------------------------------
    // Determinism.
    // -----------------------------------------------------------------

    [Fact]
    public void Emit_TwiceForSameProperty_ProducesByteIdenticalOutput()
    {
        (EmitContext ctx, IPropertySymbol prop) = BuildFor(
            "Health",
            "namespace Game { public class Widget { public int Health { get; set; } } }");

        CppWriter a = new();
        CppWriter b = new();
        new PropertyEmitter().Emit(prop, ctx, a);
        new PropertyEmitter().Emit(prop, ctx, b);

        Assert.Equal(a.Build(), b.Build());
    }
}
