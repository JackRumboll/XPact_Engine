// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// The Phase-1 locked specifier vocabulary per
/// <c>/Documents/XHT.html</c> Rev 7 Section 7 + Section 7.4 +
/// <c>/Documents/XToolchainContract.html</c> Section 1.2. This list is the
/// source of truth that <see cref="SpecifierRegistry"/> bulk-registers at
/// construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locked vocabulary.</b> Adding / removing a specifier from this list
/// requires a Toolchain Contract revision per Contract Section 1.2 (the
/// specifier vocabulary is frozen at Stage A). XPact-specific plugins ship
/// additional specifiers via the plugin model (Section 18); those do not
/// appear here.
/// </para>
/// <para>
/// <b>Layout.</b> The list is organised by primary context (Class, Struct,
/// Enum, Function, PropertyMember, PropertyArgument, Param, EnumValue,
/// Delegate). Specifiers legal in more than one context (e.g. <c>Category</c>
/// on both functions and properties; <c>Transient</c> on both classes and
/// property-members) are declared once with the union mask. A specifier name
/// must appear at most once across the entire list -- the conflict-
/// detection rule in <see cref="SpecifierRegistry.Register"/> enforces this
/// at registration time.
/// </para>
/// </remarks>
public static class BuiltInSpecifiers
{
    private static readonly IReadOnlyList<SpecifierDefinition> s_all = BuildVocabulary();

    /// <summary>
    /// The Phase-1 locked specifier set. The order is the declaration order
    /// below, which is also the order
    /// <see cref="RegisterAllInto(ISpecifierRegistry)"/> invokes
    /// <see cref="ISpecifierRegistry.Register(SpecifierDefinition)"/>.
    /// </summary>
    public static IReadOnlyList<SpecifierDefinition> All => s_all;

    /// <summary>
    /// Bulk-register every Phase-1 locked specifier into
    /// <paramref name="registry"/>. Invoked by
    /// <see cref="SpecifierRegistry"/>'s constructor when its
    /// <c>registerBuiltIns</c> flag is true.
    /// </summary>
    /// <param name="registry">The registry to populate. Must not be null.</param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="registry"/> is null.</exception>
    public static void RegisterAllInto(ISpecifierRegistry registry)
    {
        System.ArgumentNullException.ThrowIfNull(registry);

        foreach (SpecifierDefinition def in s_all)
        {
            registry.Register(def);
        }
    }

    private static IReadOnlyList<SpecifierDefinition> BuildVocabulary()
    {
        List<SpecifierDefinition> list = new(capacity: 96);

        // -----------------------------------------------------------------
        // Class specifiers (XCLASS(...)) per XHT.html Rev 7 Section 7.4 +
        // Contract Section 1.2. Section 7.4 surfaces Intrinsic / MinimalAPI /
        // NoExport / Within / Config as the emit-driving class-flag set.
        // -----------------------------------------------------------------
        const SpecifierContext C = SpecifierContext.Class;

        list.Add(new SpecifierDefinition("Abstract", C, SpecifierValueKind.Flag, false,
            "Class cannot be instantiated directly; subclasses must override."));
        list.Add(new SpecifierDefinition("Blueprintable", C, SpecifierValueKind.Flag, false,
            "Class may be derived from in Blueprint."));
        list.Add(new SpecifierDefinition("BlueprintType",
            SpecifierContext.Class | SpecifierContext.Struct | SpecifierContext.Enum,
            SpecifierValueKind.Flag, false,
            "Type may be used as a variable type in Blueprint graphs."));
        list.Add(new SpecifierDefinition("DefaultToInstanced", C, SpecifierValueKind.Flag, false,
            "Object properties of this class default to Instanced."));
        list.Add(new SpecifierDefinition("Deprecated", C, SpecifierValueKind.Flag, false,
            "Class is deprecated; emits warnings on use."));
        list.Add(new SpecifierDefinition("EditInlineNew", C, SpecifierValueKind.Flag, false,
            "Instances may be created inline in the property editor."));
        list.Add(new SpecifierDefinition("HideCategories", C, SpecifierValueKind.MultipleValues, false,
            "Hide the named property categories from the editor."));
        list.Add(new SpecifierDefinition("Intrinsic", C, SpecifierValueKind.Flag, false,
            "Class is runtime-registered via IMPLEMENT_INTRINSIC_CLASS; no body macros required (XHT115)."));
        list.Add(new SpecifierDefinition("MinimalAPI", C, SpecifierValueKind.Flag, false,
            "Export only the runtime-essential surface; inline functions are not exported (XHT116)."));
        list.Add(new SpecifierDefinition("NoExport", C, SpecifierValueKind.Flag, false,
            "Class emits no body macros; only forward declarations + descriptors (XHT114/XHT117)."));
        list.Add(new SpecifierDefinition("NotBlueprintable", C, SpecifierValueKind.Flag, false,
            "Class may not be derived from in Blueprint."));
        list.Add(new SpecifierDefinition("NotPlaceable", C, SpecifierValueKind.Flag, false,
            "Class may not be placed in a level."));
        list.Add(new SpecifierDefinition("Placeable", C, SpecifierValueKind.Flag, false,
            "Class may be placed in a level."));
        list.Add(new SpecifierDefinition("Within", C, SpecifierValueKind.Reference, false,
            "Declares the required outer type; resolved at StepResolveBases (XHT118/XHT119)."));
        list.Add(new SpecifierDefinition("ClassGroup", C, SpecifierValueKind.SingleValue, false,
            "Editor-side classification group name."));
        list.Add(new SpecifierDefinition("ShowCategories", C, SpecifierValueKind.MultipleValues, false,
            "Show the named property categories in the editor."));

        // -----------------------------------------------------------------
        // Specifiers shared between Class and PropertyMember.
        // 'Config' is a class-level Reference (config name) and a property-
        //  level Flag (member reads from class's config); the names alias
        //  per UHT precedent. We register the class-level form as
        //  SingleValue (the config name string); the member-level form is
        //  bound to PropertyMember with a Flag kind under a different
        //  registry key. UHT collapses both onto the same name and
        //  context-dispatches; XHT keeps the same name per Section 7.4
        //  Config row but the parser/validator distinguishes by context.
        //  We register a single entry whose ApplicableTo covers both, with
        //  the more permissive ValueKind (SingleValue accepts both forms;
        //  the parser fills empty for the member case).
        // -----------------------------------------------------------------
        list.Add(new SpecifierDefinition("Config",
            SpecifierContext.Class | SpecifierContext.PropertyMember,
            SpecifierValueKind.SingleValue, false,
            "Class: read from named .ini / .xconfig file. Property: persisted to class's config (XHT114)."));

        // 'Transient' is legal on classes and property members both.
        list.Add(new SpecifierDefinition("Transient",
            SpecifierContext.Class | SpecifierContext.PropertyMember,
            SpecifierValueKind.Flag, false,
            "Class / member is not persisted across save/load cycles."));

        // -----------------------------------------------------------------
        // Struct specifiers (XSTRUCT(...)) per Contract Section 1.2.
        // -----------------------------------------------------------------
        const SpecifierContext S = SpecifierContext.Struct;
        list.Add(new SpecifierDefinition("HasDefaults", S, SpecifierValueKind.Flag, false,
            "Struct supplies its own default initialisation."));
        list.Add(new SpecifierDefinition("Immutable", S, SpecifierValueKind.Flag, false,
            "Struct fields are read-only after construction."));
        list.Add(new SpecifierDefinition("Atomic", S, SpecifierValueKind.Flag, false,
            "Struct is serialised as a single atomic unit."));

        // -----------------------------------------------------------------
        // Enum specifiers (XENUM(...)).
        // -----------------------------------------------------------------
        list.Add(new SpecifierDefinition("Bitmask", SpecifierContext.Enum, SpecifierValueKind.Flag, false,
            "Enum values are intended for bitwise-OR composition."));

        // -----------------------------------------------------------------
        // Function specifiers (XFUNCTION(...)) per Contract Section 1.2.
        // -----------------------------------------------------------------
        const SpecifierContext F = SpecifierContext.Function;

        list.Add(new SpecifierDefinition("BlueprintCallable", F, SpecifierValueKind.Flag, false,
            "Function may be called from Blueprint."));
        list.Add(new SpecifierDefinition("BlueprintImplementableEvent", F, SpecifierValueKind.Flag, false,
            "Function body is supplied in Blueprint, not native."));
        list.Add(new SpecifierDefinition("BlueprintNativeEvent", F, SpecifierValueKind.Flag, false,
            "Function default body is native; Blueprint may override."));
        list.Add(new SpecifierDefinition("BlueprintPure", F, SpecifierValueKind.Flag, false,
            "Function has no side effects; safe to call from Blueprint pure nodes."));
        list.Add(new SpecifierDefinition("Client", F, SpecifierValueKind.Flag, false,
            "Function is replicated; runs on the owning client."));
        list.Add(new SpecifierDefinition("Exec", F, SpecifierValueKind.Flag, false,
            "Function is callable from the engine console."));
        list.Add(new SpecifierDefinition("Server", F, SpecifierValueKind.Flag, false,
            "Function is replicated; runs on the authoritative server."));
        list.Add(new SpecifierDefinition("NetMulticast", F, SpecifierValueKind.Flag, false,
            "Function is replicated; runs on every connected client."));
        list.Add(new SpecifierDefinition("Reliable", F, SpecifierValueKind.Flag, false,
            "Replicated function is delivered reliably."));
        list.Add(new SpecifierDefinition("Unreliable", F, SpecifierValueKind.Flag, false,
            "Replicated function is delivered best-effort."));
        list.Add(new SpecifierDefinition("WithValidation", F, SpecifierValueKind.Flag, false,
            "Replicated function carries a server-side validator companion."));
        list.Add(new SpecifierDefinition("Static", F, SpecifierValueKind.Flag, false,
            "Function is a static class member."));
        list.Add(new SpecifierDefinition("Const", F, SpecifierValueKind.Flag, false,
            "Function is a const member; does not mutate state."));
        list.Add(new SpecifierDefinition("Virtual", F, SpecifierValueKind.Flag, false,
            "Function is virtual."));
        list.Add(new SpecifierDefinition("Override", F, SpecifierValueKind.Flag, false,
            "Function overrides a base-class virtual."));
        list.Add(new SpecifierDefinition("CallInEditor", F, SpecifierValueKind.Flag, false,
            "Function is callable from the editor's property panel."));

        // Function calling-convention specifiers (Contract Section 1.2,
        // Rev 11 audit finding #2).
        list.Add(new SpecifierDefinition("NoThrow", F, SpecifierValueKind.KeyEqValue, false,
            "Function opts into the direct (non-shimmed) calling convention; XIL2CPP must prove noexcept."));
        list.Add(new SpecifierDefinition("CanThrow", F, SpecifierValueKind.KeyEqValue, false,
            "Function opts into the XResult*-shimmed calling convention."));
        list.Add(new SpecifierDefinition("SimPathStringIndexOK", F, SpecifierValueKind.KeyEqValue, false,
            "Function opts into s[i] error-suppression on sim-path TUs."));

        // -----------------------------------------------------------------
        // Specifiers shared between Function and Property (the 'base'
        // property table per Section 18.1).
        // -----------------------------------------------------------------
        const SpecifierContext FP = SpecifierContext.Function | SpecifierContext.Property
                                  | SpecifierContext.PropertyMember | SpecifierContext.PropertyArgument;

        list.Add(new SpecifierDefinition("Category", FP, SpecifierValueKind.SingleValue, false,
            "Editor-side category grouping for the function / property."));
        list.Add(new SpecifierDefinition("DisplayName",
            FP | SpecifierContext.EnumValue,
            SpecifierValueKind.SingleValue, false,
            "Editor-facing display name."));
        list.Add(new SpecifierDefinition("Meta",
            SpecifierContext.Class | SpecifierContext.Struct | SpecifierContext.Enum
            | SpecifierContext.Interface | SpecifierContext.Function | SpecifierContext.Property
            | SpecifierContext.PropertyMember | SpecifierContext.PropertyArgument
            | SpecifierContext.Delegate | SpecifierContext.EnumValue | SpecifierContext.Param,
            SpecifierValueKind.MultipleValues, true,
            "Free-form key=value metadata bag (Tooltip, EditCondition, etc.)."));

        // -----------------------------------------------------------------
        // PropertyMember specifiers per Contract Section 1.2.
        // -----------------------------------------------------------------
        const SpecifierContext PM = SpecifierContext.PropertyMember;

        list.Add(new SpecifierDefinition("BlueprintReadOnly", PM, SpecifierValueKind.Flag, false,
            "Member is exposed to Blueprint read-only."));
        list.Add(new SpecifierDefinition("BlueprintReadWrite", PM, SpecifierValueKind.Flag, false,
            "Member is exposed to Blueprint read/write."));
        list.Add(new SpecifierDefinition("BlueprintAssignable", PM, SpecifierValueKind.Flag, false,
            "Multicast delegate is bindable from Blueprint."));
        list.Add(new SpecifierDefinition("EditAnywhere", PM, SpecifierValueKind.Flag, false,
            "Member is editable in archetype and instance property editors."));
        list.Add(new SpecifierDefinition("EditDefaultsOnly", PM, SpecifierValueKind.Flag, false,
            "Member is editable only in the archetype editor."));
        list.Add(new SpecifierDefinition("EditInstanceOnly", PM, SpecifierValueKind.Flag, false,
            "Member is editable only on placed instances."));
        list.Add(new SpecifierDefinition("VisibleAnywhere", PM, SpecifierValueKind.Flag, false,
            "Member is visible (read-only) in archetype and instance property editors."));
        list.Add(new SpecifierDefinition("VisibleDefaultsOnly", PM, SpecifierValueKind.Flag, false,
            "Member is visible only in the archetype editor."));
        list.Add(new SpecifierDefinition("VisibleInstanceOnly", PM, SpecifierValueKind.Flag, false,
            "Member is visible only on placed instances."));
        list.Add(new SpecifierDefinition("Replicated", PM, SpecifierValueKind.Flag, false,
            "Member is replicated across the network."));
        list.Add(new SpecifierDefinition("ReplicatedUsing", PM, SpecifierValueKind.SingleValue, false,
            "Member is replicated and fires the named OnRep callback (XHT113)."));
        list.Add(new SpecifierDefinition("RepNotify", PM, SpecifierValueKind.Flag, false,
            "Member fires the OnRep callback on replication."));

        // Replication-condition specifiers per Round-2 audit M2. UHT
        // models each COND_X as its own Flag-form specifier on
        // PropertyMember (rather than a single KeyEqValue specifier
        // whose value is the condition name) so the parser surfaces
        // them uniformly with the other property flags.
        list.Add(new SpecifierDefinition("COND_OwnerOnly", PM, SpecifierValueKind.Flag, false,
            "Replicate only to the actor's owning client (UE COND_OwnerOnly)."));
        list.Add(new SpecifierDefinition("COND_AutonomousOnly", PM, SpecifierValueKind.Flag, false,
            "Replicate only to the autonomous proxy connection (UE COND_AutonomousOnly)."));
        list.Add(new SpecifierDefinition("COND_SimulatedOnly", PM, SpecifierValueKind.Flag, false,
            "Replicate only to simulated proxy connections (UE COND_SimulatedOnly)."));
        list.Add(new SpecifierDefinition("COND_SkipOwner", PM, SpecifierValueKind.Flag, false,
            "Replicate to every connection except the owner (UE COND_SkipOwner)."));
        list.Add(new SpecifierDefinition("COND_InitialOnly", PM, SpecifierValueKind.Flag, false,
            "Replicate only on initial replication (UE COND_InitialOnly)."));
        list.Add(new SpecifierDefinition("COND_ReplayOrOwner", PM, SpecifierValueKind.Flag, false,
            "Replicate on replay or to owner (UE COND_ReplayOrOwner)."));
        list.Add(new SpecifierDefinition("COND_ReplayOnly", PM, SpecifierValueKind.Flag, false,
            "Replicate only on replay capture (UE COND_ReplayOnly)."));
        list.Add(new SpecifierDefinition("COND_SimulatedOrPhysics", PM, SpecifierValueKind.Flag, false,
            "Replicate to simulated proxies OR when role is SimulatedPhysics (UE COND_SimulatedOrPhysics)."));
        list.Add(new SpecifierDefinition("COND_InitialOrOwner", PM, SpecifierValueKind.Flag, false,
            "Replicate on initial replication OR to owner (UE COND_InitialOrOwner)."));
        list.Add(new SpecifierDefinition("COND_Custom", PM, SpecifierValueKind.Flag, false,
            "Replicate per custom condition logic (UE COND_Custom)."));
        list.Add(new SpecifierDefinition("GlobalConfig", PM, SpecifierValueKind.Flag, false,
            "Member is persisted in the global config (not the class's own config)."));
        list.Add(new SpecifierDefinition("Localized", PM, SpecifierValueKind.Flag, false,
            "Member is localised text."));
        list.Add(new SpecifierDefinition("Instanced", PM, SpecifierValueKind.Flag, false,
            "Object-reference member auto-instantiates its target."));
        list.Add(new SpecifierDefinition("SaveGame", PM, SpecifierValueKind.Flag, false,
            "Member is included in SaveGame archives."));
        list.Add(new SpecifierDefinition("Interp", PM, SpecifierValueKind.Flag, false,
            "Member is interpolated by Matinee / Sequencer."));

        // PropertyMember-only specifier sharing the name 'NoExport' with the
        // class-level NoExport. The class-level definition above covers
        // SpecifierContext.Class; this definition covers PropertyMember. The
        // names alias intentionally per UHT precedent + Contract Section 1.2.
        // We collapse onto a single entry whose ApplicableTo covers both.
        // (Class | PropertyMember). Re-registering the same name would
        // collide; the single entry below replaces the earlier class-only
        // definition by being declared with the union mask. Find the prior
        // entry and remove it before adding the merged one.
        list.RemoveAll(d => d.Name == "NoExport");
        list.Add(new SpecifierDefinition("NoExport",
            SpecifierContext.Class | SpecifierContext.Struct | SpecifierContext.PropertyMember,
            SpecifierValueKind.Flag, false,
            "Suppress emission of body macros / property scaffolding (XHT114/XHT117)."));

        // Numeric-range UI specifiers per Contract Section 1.2.
        list.Add(new SpecifierDefinition("ClampMin", PM, SpecifierValueKind.KeyEqValue, false,
            "Minimum value enforced by the editor."));
        list.Add(new SpecifierDefinition("ClampMax", PM, SpecifierValueKind.KeyEqValue, false,
            "Maximum value enforced by the editor."));
        list.Add(new SpecifierDefinition("UIMin", PM, SpecifierValueKind.KeyEqValue, false,
            "Minimum value displayed in editor sliders."));
        list.Add(new SpecifierDefinition("UIMax", PM, SpecifierValueKind.KeyEqValue, false,
            "Maximum value displayed in editor sliders."));
        list.Add(new SpecifierDefinition("ToolTip",
            SpecifierContext.PropertyMember | SpecifierContext.PropertyArgument | SpecifierContext.Function
            | SpecifierContext.Class | SpecifierContext.Struct | SpecifierContext.Enum
            | SpecifierContext.Interface | SpecifierContext.EnumValue,
            SpecifierValueKind.SingleValue, false,
            "Tooltip text displayed in the editor."));

        // -----------------------------------------------------------------
        // PropertyArgument specifiers (function parameter only) per
        // Section 18.1. UHT names: ConstParm, OutParm, ReferenceParm.
        // -----------------------------------------------------------------
        const SpecifierContext PA = SpecifierContext.PropertyArgument;
        list.Add(new SpecifierDefinition("ConstParm", PA, SpecifierValueKind.Flag, false,
            "Parameter is const."));
        list.Add(new SpecifierDefinition("OutParm", PA, SpecifierValueKind.Flag, false,
            "Parameter is an out parameter (pass by mutable reference)."));
        list.Add(new SpecifierDefinition("ReferenceParm", PA, SpecifierValueKind.Flag, false,
            "Parameter is a reference parameter."));
        list.Add(new SpecifierDefinition("ReturnParm", PA, SpecifierValueKind.Flag, false,
            "Parameter slot holds the function's return value."));

        // -----------------------------------------------------------------
        // XPARAM(...) specifiers per Section 7 marker table. Distinct from
        // PropertyArgument (which carries the UHT parameter-shape flag
        // vocabulary); XPARAM specifiers carry the editor-facing parameter
        // metadata.
        // -----------------------------------------------------------------
        const SpecifierContext P = SpecifierContext.Param;
        list.Add(new SpecifierDefinition("Out", P, SpecifierValueKind.Flag, false,
            "XPARAM-side out marker."));
        list.Add(new SpecifierDefinition("Ref", P, SpecifierValueKind.Flag, false,
            "XPARAM-side reference marker."));
        list.Add(new SpecifierDefinition("DefaultValue", P, SpecifierValueKind.SingleValue, false,
            "XPARAM-side default-value literal."));

        // -----------------------------------------------------------------
        // EnumValue specifiers (XMETA(...)) per Section 7 marker table.
        // -----------------------------------------------------------------
        const SpecifierContext EV = SpecifierContext.EnumValue;
        list.Add(new SpecifierDefinition("Hidden", EV, SpecifierValueKind.Flag, false,
            "Enum value is hidden from the editor's enum picker."));

        // -----------------------------------------------------------------
        // Delegate specifiers (XDELEGATE(...)) per Contract Section 1.2.
        // -----------------------------------------------------------------
        const SpecifierContext D = SpecifierContext.Delegate;
        list.Add(new SpecifierDefinition("BlueprintAuthorityOnly", D, SpecifierValueKind.Flag, false,
            "Delegate only fires on the authority."));
        list.Add(new SpecifierDefinition("BlueprintCosmetic", D, SpecifierValueKind.Flag, false,
            "Delegate only fires on cosmetic (non-dedicated-server) clients."));

        return list;
    }
}
