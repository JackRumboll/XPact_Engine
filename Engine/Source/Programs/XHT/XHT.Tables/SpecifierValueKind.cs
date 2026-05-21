// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Identifies the value-shape a specifier expects when it appears in source,
/// per <c>/Documents/XHT.html</c> Rev 7 Section 7.2 (specifier parsing).
/// </summary>
/// <remarks>
/// <para>
/// XHT mirrors UHT's <c>UhtSpecifierValueType</c> enum at
/// <c>EpicGames.UHT/Tables/UhtSpecifierTable.cs</c> but collapses the long
/// UHT vocabulary (<c>None</c>, <c>String</c>, <c>OptionalString</c>,
/// <c>SingleString</c>, <c>StringList</c>, <c>NonEmptyStringList</c>,
/// <c>KeyValuePairList</c>, <c>OptionalEqualsKeyValuePairList</c>) into the
/// five forms XHT actually needs at Phase 1. The collapse is a deliberate
/// reduction; any plugin needing the finer UHT vocabulary can resurrect it
/// by registering its own narrower validator on top of the base kind.
/// </para>
/// <para>
/// The parser uses this enum to decide how to consume tokens after the
/// specifier name:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="Flag"/> -- do not consume an <c>=</c> or value list;
///     the specifier is the entire token (<c>EditAnywhere</c>,
///     <c>BlueprintReadOnly</c>).
///   </description></item>
///   <item><description>
///     <see cref="SingleValue"/> -- consume <c>=</c> + one quoted string
///     or identifier (<c>Category="Combat"</c>).
///   </description></item>
///   <item><description>
///     <see cref="MultipleValues"/> -- consume <c>=</c> + a
///     parenthesised pipe- or comma-separated list
///     (<c>HideCategories=("Foo","Bar")</c>).
///   </description></item>
///   <item><description>
///     <see cref="KeyEqValue"/> -- consume <c>=</c> + one numeric or
///     symbolic value to be parsed against a domain constraint
///     (<c>ClampMin="0"</c>).
///   </description></item>
///   <item><description>
///     <see cref="Reference"/> -- consume <c>=</c> + an identifier that
///     must resolve to a reflected type at <c>StepResolveBases</c>
///     (<c>Within=AActor</c>).
///   </description></item>
/// </list>
/// </remarks>
public enum SpecifierValueKind
{
    /// <summary>
    /// Flag-only specifier with no value. Source form: <c>EditAnywhere</c>,
    /// <c>BlueprintReadOnly</c>.
    /// </summary>
    Flag,

    /// <summary>
    /// Single string or identifier value. Source form:
    /// <c>Category="Combat"</c>, <c>DisplayName="Open Fraction"</c>.
    /// </summary>
    SingleValue,

    /// <summary>
    /// Multiple values in a parenthesised list. Source form:
    /// <c>HideCategories=("Foo","Bar")</c>,
    /// <c>BlueprintAuthorityOnly|BlueprintCallable</c>.
    /// </summary>
    MultipleValues,

    /// <summary>
    /// Single value parsed against a domain constraint at validation time.
    /// Source form: <c>ClampMin="0"</c>, <c>UIMax="100"</c>.
    /// </summary>
    KeyEqValue,

    /// <summary>
    /// Identifier value that must resolve to a reflected type during the
    /// resolver's <c>StepResolveBases</c> phase. Source form:
    /// <c>Within=AActor</c>.
    /// </summary>
    Reference,
}
