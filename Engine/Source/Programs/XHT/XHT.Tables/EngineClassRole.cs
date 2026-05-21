// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Role of an engine-anchor type in the reflection hierarchy per
/// <c>/Documents/XHT.html</c> Rev 7 Section 2 (the XHT.Tables module
/// description -- <c>XhtEngineClassTable</c>: "core engine roles --
/// XObject, XClass, XStruct, XInterface -- mapped to their reflected
/// representations").
/// </summary>
/// <remarks>
/// <para>
/// The resolver consults <see cref="XhtEngineClassTable"/> at
/// <c>StepBindSuperAndBases</c> and <c>StepResolveBases</c> to recognise
/// whether a parsed type is one of the engine anchor types (and therefore
/// has special-case handling for inheritance pairing, descriptor emission,
/// etc.). XHT mirrors UHT's <c>UhtEngineClassTable</c> pattern at
/// <c>EpicGames.UHT/Tables/UhtEngineClassTable.cs</c>.
/// </para>
/// </remarks>
public enum EngineClassRole
{
    /// <summary>Root of the reflection hierarchy (paired with UE's <c>UObject</c>).</summary>
    XObject,

    /// <summary>Class metadata anchor (paired with UE's <c>UClass</c>).</summary>
    XClass,

    /// <summary>Struct metadata anchor (paired with UE's <c>UScriptStruct</c>).</summary>
    XStruct,

    /// <summary>Interface metadata anchor (paired with UE's <c>UInterface</c>).</summary>
    XInterface,

    /// <summary>Enum metadata anchor (paired with UE's <c>UEnum</c>).</summary>
    XEnum,

    /// <summary>Function metadata anchor (paired with UE's <c>UFunction</c>).</summary>
    XFunction,

    /// <summary>Property metadata anchor (paired with UE's <c>FProperty</c>).</summary>
    XProperty,
}
