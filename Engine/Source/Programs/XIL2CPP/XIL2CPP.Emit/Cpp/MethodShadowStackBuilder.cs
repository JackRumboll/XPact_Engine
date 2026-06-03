// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The per-method precise-GC shadow-stack + stack-map builder (XIL2CPP Phase
/// 6.g, WU-6G-CORE), per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.x
/// (precise rooting) + <c>/Documents/XCoreXObject.html</c> Section 5.2 (the
/// stack-map protocol). One instance is allocated per emitted method body; the
/// <see cref="MethodEmitter"/> binds it onto the body-lowering
/// <see cref="Body.StatementEmitter"/> for the duration of that method's emit.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shadow stack.</b> A method that holds live managed references roots
/// them in a fixed-size on-stack array
/// <c>::XCore::Reflect::XPtr&lt;::XCore::Reflect::XObject&gt; _liveRefs[N]</c>.
/// Slot 0 is the instance <c>self</c> (when present); the remaining slots are
/// the XObject-derived parameters then locals, in allocation order. The GC
/// walks this array via the registered <see cref="EmitStackMapRecord"/> record:
/// each slot's byte offset is <c>index * 8</c> (a tightly-packed array of
/// 8-byte pointers).
/// </para>
/// <para>
/// <b>Idempotent allocation.</b> <see cref="Allocate"/> is keyed on a stable
/// per-method symbol key (ordinal): re-allocating the same key returns the
/// existing index rather than growing the array, so a symbol referenced many
/// times in a body roots exactly one slot.
/// </para>
/// <para>
/// <b>The stack-map record.</b> <see cref="EmitStackMapRecord"/> emits the
/// <c>::XCore::Reflect::FStackMapRecord</c> as a function-local <c>static
/// const</c> in <c>.rodata</c> (one full PC range:
/// <c>pcRangeBegin = 0</c> / <c>pcRangeEnd = 0xFFFFFFFFu</c>), then a
/// <c>[[maybe_unused]]</c> lazy-init object whose constructor calls
/// <c>::XCore::Reflect::XStackMapTable::Register(...)</c> exactly once per
/// process (function-local static initialization is thread-safe per the C++
/// standard) with the function's linker address + the record.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Slot indices are assigned in
/// allocation order; every rendered fragment is a pure function of the
/// allocated set + the linker symbol; offsets format through
/// <see cref="CultureInfo.InvariantCulture"/>; newlines are explicit
/// <c>'\n'</c> via <see cref="CppWriter"/>. No <see cref="DateTime"/> /
/// <see cref="Guid"/> / <see cref="Random"/>. Two builders driven by the
/// identical allocation sequence emit byte-identical C++.
/// </para>
/// </remarks>
public sealed class MethodShadowStackBuilder
{
    /// <summary>The shadow-stack array element type spelling (the templated XObject XPtr per XIL2CPP.html line 3538).</summary>
    public const string SlotElementType = "::XCore::Reflect::XPtr<::XCore::Reflect::XObject>";

    /// <summary>The XObject pointer type a raw slot value is reinterpret-cast to before wrapping in an <see cref="SlotElementType"/>.</summary>
    public const string XObjectPointerType = "::XCore::Reflect::XObject*";

    /// <summary>The shadow-stack array identifier the body roots its live references in.</summary>
    public const string ArrayName = "_liveRefs";

    /// <summary>The stack-map record type spelling.</summary>
    public const string StackMapRecordType = "::XCore::Reflect::FStackMapRecord";

    /// <summary>The per-function stack-map registrar type spelling.</summary>
    public const string StackMapTableType = "::XCore::Reflect::XStackMapTable";

    /// <summary>The sentinel PC-range end (full range) the single-record stack map uses.</summary>
    public const string FullPcRangeEnd = "0xFFFFFFFFu";

    /// <summary>The byte stride between adjacent shadow-stack slots (an array of 8-byte pointers).</summary>
    public const int SlotStrideBytes = 8;

    private readonly List<ShadowStackSlot> _slots = new();
    private readonly Dictionary<string, int> _indexByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// The number of allocated shadow-stack slots (the array length <c>N</c>).
    /// </summary>
    public int Count => _slots.Count;

    /// <summary>
    /// The allocated slots in allocation (index) order. Never null.
    /// </summary>
    public IReadOnlyList<ShadowStackSlot> Slots => _slots;

    /// <summary>
    /// Allocate (or look up) the shadow-stack slot for <paramref name="symbolKey"/>.
    /// Idempotent on the key: a repeated allocation of the same key returns the
    /// existing index without growing the array. The first allocation appends a
    /// new slot carrying <paramref name="cppXObjectCastExpr"/> (the C++
    /// expression that resolves the live XObject for the slot, e.g.
    /// <c>self</c> or a parameter name).
    /// </summary>
    /// <param name="symbolKey">The stable per-method symbol key (ordinal). Must not be null / empty.</param>
    /// <param name="cppXObjectCastExpr">The C++ expression resolving the slot's live XObject. Must not be null / empty.</param>
    /// <returns>The slot index (0-based) for the key.</returns>
    /// <exception cref="ArgumentException">If <paramref name="symbolKey"/> or <paramref name="cppXObjectCastExpr"/> is null / empty / whitespace.</exception>
    public int Allocate(string symbolKey, string cppXObjectCastExpr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(cppXObjectCastExpr);

        if (_indexByKey.TryGetValue(symbolKey, out int existing))
        {
            return existing;
        }

        int index = _slots.Count;
        _slots.Add(new ShadowStackSlot(symbolKey, cppXObjectCastExpr));
        _indexByKey.Add(symbolKey, index);
        return index;
    }

    /// <summary>
    /// True iff <paramref name="symbolKey"/> already has an allocated slot,
    /// returning the slot's index in <paramref name="index"/> on success.
    /// </summary>
    /// <param name="symbolKey">The symbol key to test. Must not be null.</param>
    /// <param name="index">On return, the allocated slot index when the key is present; 0 otherwise.</param>
    /// <returns>True iff a slot is allocated for the key.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="symbolKey"/> is null.</exception>
    public bool TryGetIndex(string symbolKey, out int index)
    {
        ArgumentNullException.ThrowIfNull(symbolKey);
        return _indexByKey.TryGetValue(symbolKey, out index);
    }

    /// <summary>
    /// Emit the shadow-stack array declaration into <paramref name="writer"/>:
    /// <c>::XCore::Reflect::XPtr&lt;::XCore::Reflect::XObject&gt; _liveRefs[N] = {};</c>
    /// (zero-initialized so every slot reads as a null root before its first
    /// write). <c>N</c> is the current <see cref="Count"/>.
    /// </summary>
    /// <param name="writer">The target C++ writer. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitArrayDecl(CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string n = _slots.Count.ToString(CultureInfo.InvariantCulture);
        writer.AppendLine(SlotElementType + " " + ArrayName + "[" + n + "] = {};");
    }

    /// <summary>
    /// Emit a write into shadow-stack slot <paramref name="index"/> of the live
    /// XObject computed by <paramref name="cppExpr"/>:
    /// <c>_liveRefs[index] = ::XCore::Reflect::XPtr&lt;::XCore::Reflect::XObject&gt;(reinterpret_cast&lt;::XCore::Reflect::XObject*&gt;(expr));</c>.
    /// </summary>
    /// <param name="index">The slot index (0-based; must be in range).</param>
    /// <param name="cppExpr">The C++ expression resolving the live XObject. Must not be null / empty.</param>
    /// <param name="writer">The target C++ writer. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="cppExpr"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="index"/> is out of range.</exception>
    public void EmitSlotWrite(int index, string cppExpr, CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cppExpr);
        ArgumentNullException.ThrowIfNull(writer);
        ValidateIndex(index);

        string i = index.ToString(CultureInfo.InvariantCulture);
        writer.AppendLine(
            ArrayName + "[" + i + "] = " + SlotElementType
            + "(reinterpret_cast<" + XObjectPointerType + ">(" + cppExpr + "));");
    }

    /// <summary>
    /// Emit a clear of shadow-stack slot <paramref name="index"/>:
    /// <c>_liveRefs[index] = nullptr;</c> (drops the slot's root before the
    /// referent leaves scope, so a later GC does not over-retain it).
    /// </summary>
    /// <param name="index">The slot index (0-based; must be in range).</param>
    /// <param name="writer">The target C++ writer. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="index"/> is out of range.</exception>
    public void EmitSlotClear(int index, CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValidateIndex(index);

        string i = index.ToString(CultureInfo.InvariantCulture);
        writer.AppendLine(ArrayName + "[" + i + "] = nullptr;");
    }

    /// <summary>
    /// Emit the function's precise-GC stack map into <paramref name="writer"/>:
    /// a function-local <c>static const ::XCore::Reflect::FStackMapRecord</c> in
    /// <c>.rodata</c> describing the <see cref="Count"/> live-ref slots (one full
    /// PC range; <c>liveRefOffsets</c> = <c>{ 0, 8, 16, ... }</c>, i.e.
    /// <c>index * 8</c>), then a <c>[[maybe_unused]]</c> function-local static
    /// registrar object whose constructor calls
    /// <c>::XCore::Reflect::XStackMapTable::Register(reinterpret_cast&lt;unsigned long long&gt;(&amp;linkerSymbol), 0, &amp;record)</c>
    /// exactly once (thread-safe function-local static init). The
    /// <paramref name="linkerSymbol"/> is the C++ function whose address keys
    /// the registration (the Tier-2 direct function, or the Tier-1 <c>_Body</c>
    /// helper that carries the rooted body).
    /// </summary>
    /// <param name="linkerSymbol">The function linker symbol whose address keys the stack-map registration. Must not be null / empty / whitespace.</param>
    /// <param name="writer">The target C++ writer. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="linkerSymbol"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitStackMapRecord(string linkerSymbol, CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkerSymbol);
        ArgumentNullException.ThrowIfNull(writer);

        string n = _slots.Count.ToString(CultureInfo.InvariantCulture);
        string recordName = "_stackMap";
        string registrarName = "_stackMapReg";

        // The static const FStackMapRecord in .rodata (designated-init shape so
        // the field set is order-robust + self-documenting). One full PC range:
        // pcRangeBegin = 0, pcRangeEnd = 0xFFFFFFFFu. liveRefOffsets packs the N
        // slot byte offsets (index * 8).
        writer.AppendLine(
            "static const " + StackMapRecordType + " " + recordName + " = {");
        writer.Indent();
        writer.AppendLine(".pcRangeBegin = 0,");
        writer.AppendLine(".pcRangeEnd = " + FullPcRangeEnd + ",");
        writer.AppendLine(".numLiveRefs = " + n + ",");
        writer.AppendLine("._pad = 0,");
        writer.AppendLine(".liveRefOffsets = { " + RenderOffsets() + " },");
        writer.Unindent();
        writer.AppendLine("};");

        // The [[maybe_unused]] lazy-init registrar: a function-local static
        // whose constructor registers the record exactly once. funcSize is the
        // 0 sentinel (the runtime resolves the real extent at registration).
        writer.AppendLine(
            "[[maybe_unused]] static const bool " + registrarName + " = []() {");
        writer.Indent();
        writer.AppendLine(
            StackMapTableType + "::Register(reinterpret_cast<unsigned long long>(&"
            + linkerSymbol + "), 0 /*funcSize sentinel*/, &" + recordName + ");");
        writer.AppendLine("return true;");
        writer.Unindent();
        writer.AppendLine("}();");
    }

    /// <summary>
    /// Render the comma-separated <c>liveRefOffsets</c> body for the
    /// <see cref="Count"/> slots: each slot's byte offset is its
    /// <c>index * 8</c> (<c>"0, 8, 16"</c> for three slots; the empty string for
    /// zero slots).
    /// </summary>
    private string RenderOffsets()
    {
        StringBuilder sb = new();
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append((i * SlotStrideBytes).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private void ValidateIndex(int index)
    {
        if (index < 0 || index >= _slots.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "Shadow-stack slot index is outside the allocated range [0, " + _slots.Count + ").");
        }
    }
}

/// <summary>
/// One allocated shadow-stack slot: its stable per-method symbol key and the
/// C++ expression that resolves the live XObject the slot roots.
/// </summary>
/// <param name="SymbolKey">The stable per-method symbol key (ordinal) the slot was allocated under.</param>
/// <param name="CppXObjectCastExpr">The C++ expression resolving the slot's live XObject (e.g. <c>self</c> or a parameter name).</param>
public readonly record struct ShadowStackSlot(string SymbolKey, string CppXObjectCastExpr);
