// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Tables;

/// <summary>
/// Tests asserting the Phase-1 locked specifier vocabulary per
/// <c>/Documents/XHT.html</c> Rev 5 Section 7 + Section 7.4 +
/// <c>/Documents/XToolchainContract.html</c> Section 1.2.
/// </summary>
public class BuiltInSpecifiersTests
{
    private static SpecifierDefinition? Find(string name)
    {
        return BuiltInSpecifiers.All.FirstOrDefault(s => s.Name == name);
    }

    [Fact]
    public void All_IsNonEmpty_AndExceedsLockedVocabularyFloor()
    {
        // Brief specifies the locked-vocabulary set is non-trivial.
        // 30 is a conservative floor; Phase-1 ships substantially more.
        Assert.True(BuiltInSpecifiers.All.Count >= 30,
            $"Expected at least 30 locked-vocabulary specifiers; got {BuiltInSpecifiers.All.Count}.");
    }

    [Fact]
    public void All_HasNoDuplicateNames()
    {
        // Conflict-detection rule (Section 18.1) requires unique names in
        // the locked set; the BuildVocabulary() author is responsible for
        // collapsing dual-context specifiers (Config, Transient, NoExport)
        // onto a single union-mask entry.
        var dupes = BuiltInSpecifiers.All
            .GroupBy(s => s.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Class_Specifier_AbstractIsRegisteredToClassContext()
    {
        SpecifierDefinition? def = Find("Abstract");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Class) != 0);
        Assert.Equal(SpecifierValueKind.Flag, def.ValueKind);
    }

    [Fact]
    public void Class_Specifier_Section74_IntrinsicMinimalAPINoExportWithinConfig()
    {
        // Per XHT.html Rev 5 Section 7.4, these five specifiers drive
        // emit-time class-flag branching and MUST be registered against
        // SpecifierContext.Class.
        foreach (string name in new[] { "Intrinsic", "MinimalAPI", "NoExport", "Within", "Config" })
        {
            SpecifierDefinition? def = Find(name);
            Assert.NotNull(def);
            Assert.True((def!.ApplicableTo & SpecifierContext.Class) != 0,
                $"Specifier '{name}' must be applicable to Class context per Section 7.4.");
        }
    }

    [Fact]
    public void Class_Specifier_WithinIsReferenceValueKind()
    {
        // Section 7.4 row: Within = [TypeName] -- value is a type reference
        // resolved at StepResolveBases.
        SpecifierDefinition? def = Find("Within");
        Assert.NotNull(def);
        Assert.Equal(SpecifierValueKind.Reference, def!.ValueKind);
    }

    [Fact]
    public void Struct_Specifier_AtomicIsRegisteredToStructContext()
    {
        SpecifierDefinition? def = Find("Atomic");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Struct) != 0);
    }

    [Fact]
    public void Enum_Specifier_BitmaskIsRegisteredToEnumContext()
    {
        SpecifierDefinition? def = Find("Bitmask");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Enum) != 0);
    }

    [Fact]
    public void Function_Specifier_BlueprintCallableIsRegisteredToFunctionContext()
    {
        SpecifierDefinition? def = Find("BlueprintCallable");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Function) != 0);
    }

    [Fact]
    public void PropertyMember_Specifier_EditAnywhereIsMemberOnly()
    {
        // Per Section 18.1, EditAnywhere is the canonical PropertyMember-only
        // specifier. It must NOT apply to PropertyArgument.
        SpecifierDefinition? def = Find("EditAnywhere");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.PropertyMember) != 0);
        Assert.Equal(SpecifierContext.None, def.ApplicableTo & SpecifierContext.PropertyArgument);
    }

    [Fact]
    public void PropertyArgument_Specifier_OutParmIsArgumentOnly()
    {
        // Per Section 18.1, OutParm is PropertyArgument-only.
        SpecifierDefinition? def = Find("OutParm");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.PropertyArgument) != 0);
        Assert.Equal(SpecifierContext.None, def.ApplicableTo & SpecifierContext.PropertyMember);
    }

    [Fact]
    public void Param_Specifier_DefaultValueIsRegisteredToParamContext()
    {
        SpecifierDefinition? def = Find("DefaultValue");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Param) != 0);
        Assert.Equal(SpecifierValueKind.SingleValue, def.ValueKind);
    }

    [Fact]
    public void EnumValue_Specifier_HiddenIsRegisteredToEnumValueContext()
    {
        SpecifierDefinition? def = Find("Hidden");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.EnumValue) != 0);
    }

    [Fact]
    public void Delegate_Specifier_BlueprintAuthorityOnlyIsRegisteredToDelegateContext()
    {
        SpecifierDefinition? def = Find("BlueprintAuthorityOnly");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Delegate) != 0);
    }

    [Fact]
    public void Category_AppliesToFunctionAndProperty()
    {
        // Category is the canonical example of a base-Property-table
        // specifier (Section 18.1) -- legal on Function, PropertyMember,
        // and PropertyArgument.
        SpecifierDefinition? def = Find("Category");
        Assert.NotNull(def);
        Assert.True((def!.ApplicableTo & SpecifierContext.Function) != 0);
        Assert.True((def.ApplicableTo & SpecifierContext.PropertyMember) != 0);
        Assert.Equal(SpecifierValueKind.SingleValue, def.ValueKind);
    }

    [Fact]
    public void RegisterAllInto_PopulatesRegistry()
    {
        SpecifierRegistry r = new(registerBuiltIns: false);
        BuiltInSpecifiers.RegisterAllInto(r);

        Assert.Equal(BuiltInSpecifiers.All.Count, r.AllSpecifiers.Count);
    }

    [Fact]
    public void RegisterAllInto_NullRegistry_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => BuiltInSpecifiers.RegisterAllInto(null!));
    }
}
