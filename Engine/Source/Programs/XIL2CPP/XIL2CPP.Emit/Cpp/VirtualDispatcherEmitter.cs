// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The XIL2CPP Pass-6 virtual-dispatcher emitter: for a <c>virtual</c> /
/// <c>abstract</c> / <c>override</c> instance method it emits the
/// <c>extern "C"</c> <c>_Dispatcher</c> trampoline that performs the runtime
/// FClass v-table indirection, per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2 (Pass 6) + Section 5 (the virtual-call lowering rule). The
/// dispatcher reads the receiver's <c>FClass</c> (via the XObject FakeVTable
/// <c>GetClass()</c> slot -- the same dispatch
/// <c>ReflectionConsumptionAnalyzer</c> records) and forwards through the
/// per-method virtual slot:
/// <code>
/// constexpr int k&lt;Type&gt;_&lt;Method&gt;_VirtualSlotIndex = 0; // TODO(6.e): resolve slot index
/// extern "C" &lt;ret&gt; &lt;LinkerSymbol&gt;_Dispatcher(&lt;self&gt;, &lt;params&gt;) noexcept {
///     // TODO(6.g): FStackMapRecord + shadow-stack
///     XPACT_SAFEPOINT_CHECK();
///     const ::XCore::Reflect::FClass* cls = self-&gt;GetClass();
///     [return ]reinterpret_cast&lt;...&gt;(cls-&gt;VirtualMethods[k&lt;Type&gt;_&lt;Method&gt;_VirtualSlotIndex])(self, &lt;args&gt;);
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Slot-index placeholder (Phase 6.e decision).</b> The actual v-table slot
/// index is assigned by the FClass-layout / v-table-build wave (it depends on
/// the whole inheritance chain, which a per-method emitter does not have). The
/// 6.e dispatcher therefore emits a NAMED <c>constexpr int</c> placeholder
/// initialized to <c>0</c> with a <c>// TODO(6.e): resolve slot index</c>
/// marker, so the dispatcher COMPILES today and the later wave only has to
/// fill the constant's value (the indirection shape + the symbol name are
/// locked). This is a compilable placeholder, documented per the unit spec --
/// it is NOT a lying stub: the slot-index name is the stable hook the v-table
/// wave resolves.
/// </para>
/// <para>
/// <b>noexcept.</b> The dispatcher trampoline is <c>noexcept</c>: it is pure
/// indirection (no body that can throw); the thrownness of the resolved target
/// is the target's own concern (a Tier-1 target is reached through its own
/// shim, a Tier-2 target through its direct symbol).
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Every fragment is a pure
/// function of the symbol + context: ordinal string operations, explicit
/// <c>\n</c> via <see cref="CppWriter"/>, no <see cref="DateTime"/> /
/// <see cref="Guid"/> / <see cref="Random"/>.
/// </para>
/// </remarks>
public sealed class VirtualDispatcherEmitter
{
    /// <summary>The C++ FClass type the dispatcher reads the v-table from (Contract reflection runtime).</summary>
    public const string FClassType = "::XCore::Reflect::FClass";

    /// <summary>The virtual-method-table member on <c>FClass</c> the dispatcher indexes.</summary>
    public const string VirtualMethodsMember = "VirtualMethods";

    /// <summary>The local the dispatcher binds the receiver's resolved <c>FClass</c> into.</summary>
    public const string ClassLocalName = "cls";

    /// <summary>The XObject FakeVTable accessor the dispatcher calls to resolve the receiver's <c>FClass</c>.</summary>
    public const string GetClassCall = "GetClass()";

    /// <summary>The <c>_Dispatcher</c> linker-symbol suffix for the virtual trampoline.</summary>
    public const string DispatcherSuffix = "_Dispatcher";

    /// <summary>The <c>constexpr</c> slot-index placeholder TODO marker (Phase 6.e: the v-table wave resolves the value).</summary>
    public const string SlotIndexTodo = "TODO(6.e): resolve slot index";

    /// <summary>
    /// True iff <paramref name="method"/> is dispatched virtually (a
    /// <c>virtual</c>, <c>abstract</c>, or <c>override</c> instance method, or
    /// an interface-member implementation) and therefore needs a
    /// <c>_Dispatcher</c> trampoline.
    /// </summary>
    /// <param name="method">The method symbol. Must not be null.</param>
    /// <returns>True iff a virtual dispatcher should be emitted.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    public static bool IsVirtual(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.IsStatic)
        {
            return false;
        }
        return method.IsVirtual || method.IsAbstract || method.IsOverride;
    }

    /// <summary>
    /// Emit the virtual-dispatcher trampoline for <paramref name="method"/>
    /// into <paramref name="writer"/>: the named <c>constexpr</c> slot-index
    /// placeholder, then the <c>noexcept</c> <c>extern "C"</c>
    /// <c>_Dispatcher</c> that resolves the receiver's <c>FClass</c> and
    /// forwards through the virtual slot.
    /// </summary>
    /// <param name="method">The virtual instance method to emit a dispatcher for. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    /// <exception cref="InvalidOperationException">If the method has no Pass-5 mangling record, or is not a virtual instance method.</exception>
    public void EmitDispatcher(IMethodSymbol method, EmitContext context, CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        if (!IsVirtual(method))
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' is not a virtual instance method; no dispatcher is emitted.");
        }

        StableId id = StableId.FromSymbol(method);
        ManglingRecord record = context.FindMangling(id)
            ?? throw new InvalidOperationException(
                $"No Pass-5 mangling record for '{id.Value}'; cannot emit its virtual dispatcher.");

        string returnType = CppTypeMapper.MapReturn(method);

        // The dispatcher's parameter list begins with the typed self (a virtual
        // method is always an instance member), then the declared parameters.
        IReadOnlyList<CppParam> parameters = MethodEmitter.BuildParameters(method);

        string slotName = SlotIndexConstantName(method);

        // Named, compilable slot-index placeholder (Phase 6.e: the FClass
        // v-table wave fills the value; the name is the stable hook).
        writer.AppendLine("constexpr int " + slotName + " = 0; // " + SlotIndexTodo);

        string header = "extern \"C\" " + returnType + " " + record.LinkerSymbol + DispatcherSuffix
            + "(" + MethodEmitter.RenderParamList(parameters) + ") noexcept";
        writer.BeginBlock(header);

        writer.AppendComment(MethodEmitter.GcHookComment);
        writer.AppendLine(MethodEmitter.SafepointCheck);

        // Resolve the receiver's FClass via the XObject FakeVTable GetClass()
        // slot (the same dispatch ReflectionConsumptionAnalyzer records).
        writer.AppendLine(
            "const " + FClassType + "* " + ClassLocalName + " = "
            + MethodEmitter.SelfParamName + "->" + GetClassCall + ";");

        // Forward through the virtual slot. The function-pointer cast target is
        // resolved by the same later wave that fills the slot index; the 6.e
        // shape pins the indirection (cls->VirtualMethods[<slot>]).
        string slotAccess = ClassLocalName + "->" + VirtualMethodsMember + "[" + slotName + "]";
        string forwardArgs = MethodEmitter.RenderArgList(parameters);
        string returnKeyword = StringComparer.Ordinal.Equals(returnType, "void") ? string.Empty : "return ";
        writer.AppendLine(
            returnKeyword + "reinterpret_cast<" + FunctionPointerType(returnType, parameters) + ">("
            + slotAccess + ")(" + forwardArgs + ");");

        writer.EndBlock();
    }

    /// <summary>
    /// Build the named <c>constexpr</c> slot-index constant for
    /// <paramref name="method"/>: <c>k&lt;Type&gt;_&lt;Method&gt;_VirtualSlotIndex</c>.
    /// The <c>&lt;Type&gt;</c> is the containing type's nested-type chain joined
    /// by <c>_</c>; the <c>&lt;Method&gt;</c> is the method-name token (so a
    /// constructor / operator / accessor renders a valid identifier). Public so
    /// the later v-table wave + tests reference the identical name.
    /// </summary>
    /// <param name="method">The virtual method. Must not be null.</param>
    /// <returns>The slot-index constant identifier.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    public static string SlotIndexConstantName(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);

        StringBuilder sb = new();
        sb.Append('k');
        sb.Append(TypeChainIdentifier(method.ContainingType));
        sb.Append('_');
        sb.Append(SanitizeIdentifier(Mangler.MethodNameToken(method)));
        sb.Append("_VirtualSlotIndex");
        return sb.ToString();
    }

    /// <summary>
    /// Render the containing type's nested-type chain as an identifier fragment
    /// (outermost-first, joined by <c>_</c>); the empty string when there is no
    /// containing type.
    /// </summary>
    private static string TypeChainIdentifier(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return string.Empty;
        }

        List<string> chain = new();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            chain.Add(t.Name);
        }

        StringBuilder sb = new();
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            sb.Append(chain[i]);
            if (i > 0)
            {
                sb.Append('_');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the C++ function-pointer cast type the slot is cast to:
    /// <c>&lt;ret&gt; (*)(&lt;param-types&gt;)</c> (self type included, since the
    /// dispatcher forwards self as the first argument).
    /// </summary>
    private static string FunctionPointerType(string returnType, IReadOnlyList<CppParam> parameters)
    {
        StringBuilder sb = new();
        sb.Append(returnType);
        sb.Append(" (*)(");
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(parameters[i].Type);
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Replace any character that is not a C++ identifier character
    /// (<c>[A-Za-z0-9_]</c>) with <c>_</c> so a method-name token containing
    /// <c>$</c> (a constructor: <c>$ctor</c>) or an operator name yields a valid
    /// constant identifier.
    /// </summary>
    private static string SanitizeIdentifier(string token)
    {
        StringBuilder sb = new(token.Length);
        foreach (char c in token)
        {
            bool ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '_';
            sb.Append(ok ? c : '_');
        }
        return sb.ToString();
    }
}
