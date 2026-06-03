// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The canonical descriptor of one <c>FXObjectLifecycleTable</c> slot per
/// <c>XCoreXObject.html</c> Section 2.4 (the <c>EXObjectLifecycleSlot</c> enum
/// + the slot signatures). Captures the slot index (the
/// <c>EXObjectLifecycleSlot</c> ordinal -- the bit position in the table's
/// <c>Capabilities</c> mask AND the position in the <c>Slots[8]</c> array),
/// the lifecycle hook name (the C# override name that, when declared, makes
/// XIL2CPP emit the slot body), the emitted free-function return type, the
/// trailing C++ parameter list AFTER the leading <c>::XCore::Reflect::XObject* self</c>
/// (empty for the single-arg slots), and whether the slot is <c>noexcept</c>.
/// </summary>
/// <param name="Index">The slot ordinal (0..7) = <c>EXObjectLifecycleSlot</c> value = <c>Capabilities</c> bit position.</param>
/// <param name="Name">The C# lifecycle override name (e.g. <c>PostInitProperties</c>); the <c>Z_&lt;Name&gt;_&lt;Type&gt;</c> body symbol root.</param>
/// <param name="ReturnType">The emitted free-function C++ return type (e.g. <c>void</c> / <c>bool</c>).</param>
/// <param name="ExtraParameters">The C++ parameters after the leading <c>XObject* self</c> (empty when the slot takes only <c>self</c>).</param>
/// <param name="Noexcept">True iff the slot's function pointer signature is <c>noexcept</c> (only <c>Serialize</c>, per FIX-A-HIGH-13).</param>
public readonly record struct LifecycleSlot(
    int Index,
    string Name,
    string ReturnType,
    string ExtraParameters,
    bool Noexcept);

/// <summary>
/// Emits the per-<c>[XClass]</c> <c>FXObjectLifecycleTable</c> slot-function
/// bodies AND the <c>constinit const FXObjectLifecycleTable
/// &lt;Type&gt;_LifecycleTable</c> instance, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.1 (the FIX-A-HIGH-1 split:
/// XIL2CPP owns BOTH the table instance and its slot bodies for C#-declared
/// XClasses; XHT references both via <c>extern</c>). The slot order +
/// signatures mirror <c>XCoreXObject.html</c> Section 2.4's
/// <c>EXObjectLifecycleSlot</c> enum (8 slots; the <c>Serialize</c> slot is
/// <c>noexcept</c> with the <c>FArchiveContext*</c> third arg per
/// FIX-A-HIGH-13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Slot table (locked ABI).</b> The 8 slots, in <c>Slots[8]</c> order, are
/// PostInitProperties (0), BeginDestroy (1), IsReadyForFinishDestroy (2),
/// FinishDestroy (3), Serialize (4), AddReferencedObjects (5), PostLoad (6),
/// PreSave (7). A slot body is emitted ONLY when the C# class declares that
/// lifecycle override; otherwise the table slot is <c>nullptr</c> and the
/// corresponding <c>Capabilities</c> bit is clear.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The slot list is a fixed
/// static table; the <c>Capabilities</c> mask is composed from the declared
/// set in fixed slot order; every numeric token formats through
/// <see cref="CultureInfo.InvariantCulture"/>.
/// </para>
/// </remarks>
public sealed class LifecycleTableEmitter
{
    /// <summary>The C++ namespace the reflection runtime types live in.</summary>
    private const string ReflectNamespace = "::XCore::Reflect::";

    /// <summary>The <c>Z_&lt;Name&gt;_&lt;Type&gt;</c> slot-body symbol prefix.</summary>
    private const string SlotBodyPrefix = "Z_";

    /// <summary>
    /// The 8 lifecycle slots in <c>Slots[8]</c> order per
    /// <c>XCoreXObject.html</c> Section 2.4. The order is the ABI-locked
    /// <c>EXObjectLifecycleSlot</c> ordinal order; do not reorder.
    /// </summary>
    public static readonly IReadOnlyList<LifecycleSlot> Slots = new LifecycleSlot[]
    {
        new(0, "PostInitProperties", "void", string.Empty, Noexcept: false),
        new(1, "BeginDestroy", "void", string.Empty, Noexcept: false),
        new(2, "IsReadyForFinishDestroy", "bool", string.Empty, Noexcept: false),
        new(3, "FinishDestroy", "void", string.Empty, Noexcept: false),
        new(
            4,
            "Serialize",
            "void",
            ReflectNamespace + "FArchive& Ar, const " + ReflectNamespace + "FArchiveContext* Ctx",
            Noexcept: true),
        new(
            5,
            "AddReferencedObjects",
            "void",
            ReflectNamespace + "FXGrayQueue& GrayQueue",
            Noexcept: false),
        new(6, "PostLoad", "void", string.Empty, Noexcept: false),
        new(
            7,
            "PreSave",
            "void",
            "const " + ReflectNamespace + "FObjectPreSaveContext* Ctx",
            Noexcept: false),
    };

    /// <summary>
    /// The bare (non-module-prefixed) lifecycle-table instance symbol for a
    /// type: <c>&lt;Type&gt;_LifecycleTable</c> per Section 5.1 (the
    /// referenced-by-extern symbol XHT and XIL2CPP agree on by simple-name).
    /// </summary>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <returns>The lifecycle-table instance symbol.</returns>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    public static string LifecycleTableSymbol(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return typeName + "_LifecycleTable";
    }

    /// <summary>
    /// The <c>Z_&lt;Name&gt;_&lt;Type&gt;</c> slot-body free-function symbol for
    /// a slot on a type.
    /// </summary>
    /// <param name="slot">The lifecycle slot.</param>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <returns>The slot-body symbol.</returns>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    public static string SlotBodySymbol(LifecycleSlot slot, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return SlotBodyPrefix + slot.Name + "_" + typeName;
    }

    /// <summary>
    /// Emit one lifecycle-slot free-function body
    /// (<c>extern "C" &lt;ret&gt; Z_&lt;Name&gt;_&lt;Type&gt;(XObject* self[, extra])[ noexcept] { ... }</c>).
    /// The body downcasts <c>self</c> to <c>&lt;Type&gt;*</c> and lowers the
    /// declared C# override body through <paramref name="bodyEmitter"/>; a
    /// <c>bool</c> slot with no statements emitted falls back to
    /// <c>return true;</c> (the IsReadyForFinishDestroy default).
    /// </summary>
    /// <param name="slot">The lifecycle slot to emit a body for. The caller emits this only when the C# override is declared.</param>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <param name="overrideBody">The declared C# override's body block to lower, or null when the override has no lowerable body.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <param name="bodyEmitter">The statement emitter to lower the override body through. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> or <paramref name="bodyEmitter"/> is null.</exception>
    public void EmitSlotBody(
        LifecycleSlot slot,
        string typeName,
        Microsoft.CodeAnalysis.SyntaxNode? overrideBody,
        CppWriter writer,
        Body.StatementEmitter bodyEmitter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(bodyEmitter);

        string symbol = SlotBodySymbol(slot, typeName);
        string parameterList = ReflectNamespace + "XObject* self";
        if (slot.ExtraParameters.Length > 0)
        {
            parameterList += ", " + slot.ExtraParameters;
        }

        string header = "extern \"C\" " + slot.ReturnType + " " + symbol + "(" + parameterList + ")";
        if (slot.Noexcept)
        {
            header += " noexcept";
        }

        writer.BeginBlock(header);
        writer.AppendLine("auto* obj = static_cast<" + typeName + "*>(self);");
        writer.AppendLine("(void)obj;");
        if (overrideBody is not null)
        {
            bodyEmitter.EmitStatement(overrideBody);
        }
        else if (string.Equals(slot.ReturnType, "bool", StringComparison.Ordinal))
        {
            writer.AppendLine("return true;");
        }
        writer.EndBlock();
    }

    /// <summary>
    /// Emit the <c>constinit const FXObjectLifecycleTable &lt;Type&gt;_LifecycleTable</c>
    /// instance: the <c>Capabilities</c> bitmask (bit N set iff slot N is
    /// declared), a zero <c>_padHeader</c>, then the 8 <c>Slots[8]</c> entries
    /// in ABI order -- each <c>reinterpret_cast&lt;void(*)()&gt;(&amp;Z_&lt;Name&gt;_&lt;Type&gt;)</c>
    /// when the slot is declared, else <c>nullptr</c> (Section 5.1).
    /// </summary>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <param name="declaredSlotIndices">The set of declared slot indices (0..7); a slot not in the set is <c>nullptr</c>. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="declaredSlotIndices"/> or <paramref name="writer"/> is null.</exception>
    public void EmitTableInstance(
        string typeName,
        IReadOnlySet<int> declaredSlotIndices,
        CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(declaredSlotIndices);
        ArgumentNullException.ThrowIfNull(writer);

        string instance = LifecycleTableSymbol(typeName);

        uint capabilities = 0;
        foreach (LifecycleSlot slot in Slots)
        {
            if (declaredSlotIndices.Contains(slot.Index))
            {
                capabilities |= 1u << slot.Index;
            }
        }

        writer.AppendComment("===== " + instance + " (FXObjectLifecycleTable instance) =====");
        writer.AppendComment(
            "XIL2CPP owns the table instance + the slot bodies (Section 5.1, FIX-A-HIGH-1);");
        writer.AppendComment("XHT references both via extern. Capabilities bit N set => slot N non-null.");
        writer.BeginBlock(
            "constinit const " + ReflectNamespace + "FXObjectLifecycleTable " + instance + " =");
        writer.AppendLine(
            "/* Capabilities */ " + capabilities.ToString(CultureInfo.InvariantCulture) + "u,");
        writer.AppendLine("/* _padHeader  */ 0u,");
        writer.BeginBlock("/* Slots[8] */");
        foreach (LifecycleSlot slot in Slots)
        {
            string value = declaredSlotIndices.Contains(slot.Index)
                ? "reinterpret_cast<void(*)()>(&" + SlotBodySymbol(slot, typeName) + ")"
                : "nullptr";
            writer.AppendLine(value + ", // [" + slot.Index.ToString(CultureInfo.InvariantCulture)
                + "] " + slot.Name);
        }
        writer.EndBlock(",");
        writer.EndBlock(";");
    }
}
