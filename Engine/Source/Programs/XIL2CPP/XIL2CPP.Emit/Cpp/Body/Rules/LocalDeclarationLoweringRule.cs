// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// Body-lowering rule (WU-D1) for a C# local-variable declaration statement,
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3:
/// <c>var x = expr;</c> lowers to <c>auto x = &lt;expr&gt;;</c> and
/// <c>int x = 5;</c> lowers to <c>int32_t x = 5;</c> (the explicit-type form
/// emits the resolved C++ type spelling). One C# declarator per emitted C++
/// line; the initializer (when present) is lowered by recursing into the parent
/// expression emitter so this rule never re-implements expression lowering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Type spelling.</b> A <c>var</c> declaration emits <c>auto</c> (Roslyn /
/// the doc Section 5.3 example). An explicit-type declaration emits the C++
/// spelling resolved from the declarator's bound type: the C# primitive types
/// map to their fixed-width <c>&lt;cstdint&gt;</c> forms
/// (<c>int</c> to <c>int32_t</c>, <c>char</c> to <c>char16_t</c>, ... per the
/// doc's cross-arch-bit-exact mapping), and any other resolved type falls back
/// to its bound name. The richer reference / container / struct type spellings
/// (<c>XObject*</c>, <c>XPtr&lt;T&gt;</c>, value structs) are the province of the
/// later class / member emitters; this rule emits the primitive mapping
/// precisely and the resolved name otherwise.
/// </para>
/// <para>
/// <b>Precise-GC local rooting (XIL2CPP Phase 6.g, WU-6G-LOCAL).</b> When the
/// rule is lowering a method body that carries a shadow stack (the parent
/// <see cref="StatementEmitter.ShadowStackBuilder"/> is non-null) AND a
/// declared local's bound type derives from the engine <c>XObject</c>
/// (<see cref="AnalyzerHelpers.IsXObjectDerived"/>), the local is rooted: it is
/// allocated a shadow-stack slot, the slot is written with the live reference
/// immediately after the declaration line, and a C++ RAII guard nulls the slot
/// at C++ scope exit so the shadow stack never reports a stale root after the
/// local leaves scope. A non-XObject local roots nothing; outside a method-body
/// emit (the builder is null) the rule behaves exactly as it did before 6.g
/// (declaration line only). Each declarator in a multi-declarator statement is
/// rooted independently, in source order.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Declarators are emitted in
/// the source's syntactic order; type spelling is a pure switch over the bound
/// <see cref="SpecialType"/>; the rooting slot index, slot-write expression, and
/// guard name are pure functions of the allocation order; slot keys are ordinal
/// (the local name); the guard index formats through
/// <see cref="CultureInfo.InvariantCulture"/>; no ambient state,
/// <see cref="DateTime"/>, <see cref="Guid"/>, or culture-sensitive formatting.
/// </para>
/// </remarks>
public sealed class LocalDeclarationLoweringRule : IBodyLoweringRule
{
    /// <summary>
    /// The C++ RAII scope-clear guard type-name prefix. One unique guard type
    /// (and instance) is emitted per rooted local, named
    /// <c>_XilGcGuard_&lt;idx&gt;</c> / <c>_xilGcGuard_&lt;idx&gt;</c> by the
    /// slot index so two rooted locals in one C++ scope never collide. The
    /// guard's destructor nulls its shadow-stack slot at C++ scope exit.
    /// </summary>
    public const string GuardTypePrefix = "_XilGcGuard_";

    /// <summary>The C++ RAII scope-clear guard instance-name prefix (see <see cref="GuardTypePrefix"/>).</summary>
    public const string GuardInstancePrefix = "_xilGcGuard_";

    /// <summary>
    /// The stable shadow-stack symbol-key prefix for a rooted local: the
    /// <c>$local:</c> prefix + the local's C# name. A C# method may not declare
    /// two locals with the same name in overlapping scopes, so the name is a
    /// stable, collision-free per-method key (and the allocation is idempotent
    /// on it, so a local re-encountered roots exactly one slot).
    /// </summary>
    private const string LocalSlotKeyPrefix = "$local:";

    /// <inheritdoc/>
    public string Name => "LocalDeclaration";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
        => node is LocalDeclarationStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var declarationStatement = (LocalDeclarationStatementSyntax)node;
        VariableDeclarationSyntax declaration = declarationStatement.Declaration;

        SemanticModel model = context.GetSemanticModel(node.SyntaxTree);
        string typeSpelling = ResolveTypeSpelling(declaration, model);

        // The bound declaration type drives the precise-GC rooting decision: a
        // local whose type derives from the engine XObject must be rooted in the
        // shadow stack so a GC during its lifetime sees it as a live reference.
        // The whole declaration shares one type (a single `var` / explicit type
        // governs every declarator), so it is resolved once.
        bool rootLocals = parent.ShadowStackBuilder is not null
            && IsXObjectDerivedLocal(declaration, model);

        foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
        {
            // Lead-in: "<type> <name>[ = <init>]" with no trailing terminator,
            // then close the declaration statement with ";\n".
            EmitDeclarator(typeSpelling, declarator, parent, writer);
            writer.Append(";");
            writer.AppendLine();

            // Precise-GC rooting (Phase 6.g): root the XObject local in the
            // shadow stack AFTER its declaration line, then emit the RAII guard
            // that nulls the slot at C++ scope exit. Handled per declarator so a
            // multi-declarator statement roots each local independently.
            if (rootLocals)
            {
                RootLocal(parent.ShadowStackBuilder!, declarator.Identifier.ValueText, writer);
            }
        }
    }

    /// <summary>
    /// Emit a single declarator's lead-in &#8211; <c>&lt;type&gt; &lt;name&gt;</c>
    /// plus <c>= &lt;init&gt;</c> when the declarator has an initializer
    /// &#8211; with NO trailing terminator and NO newline (the caller closes the
    /// statement / for-init clause). The initializer is lowered through the
    /// parent expression emitter so it never re-implements expression lowering.
    /// </summary>
    /// <param name="typeSpelling">The resolved C++ type spelling shared by every declarator.</param>
    /// <param name="declarator">The single declarator to render.</param>
    /// <param name="parent">The dispatching statement emitter (for initializer recursion).</param>
    /// <param name="writer">The C++ writer the lead-in is emitted into.</param>
    private static void EmitDeclarator(
        string typeSpelling,
        VariableDeclaratorSyntax declarator,
        StatementEmitter parent,
        CppWriter writer)
    {
        writer.Append(typeSpelling);
        writer.Append(" ");
        writer.Append(declarator.Identifier.ValueText);

        if (declarator.Initializer is { } initializer)
        {
            writer.Append(" = ");
            parent.Expressions.EmitExpression(initializer.Value);
        }
    }

    /// <summary>
    /// Render a C# <see cref="VariableDeclarationSyntax"/> INLINE as a C++
    /// declaration with NO trailing terminator / newline &#8211; the form a
    /// <c>for</c>-init clause needs (<c>for (int i = 0; ...)</c>). The type
    /// spelling + initializer logic is identical to the statement-position emit
    /// (<see cref="Emit"/>): the same fixed-width primitive mapping, the same
    /// XObject-derived pointer spelling (FIX 4), and the same expression-emitter
    /// recursion for each initializer. Multiple declarators are comma-separated
    /// (<c>int i = 0, j = 1</c>). Precise-GC rooting is intentionally NOT applied
    /// here (a for-init local is rooted by the enclosing method-body emit when
    /// appropriate; the loop header is not a statement scope that owns a guard).
    /// </summary>
    /// <param name="declaration">The for-init variable declaration. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer the inline declaration is emitted into. Must not be null.</param>
    /// <param name="parent">The dispatching statement emitter (for initializer recursion). Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static void EmitInlineDeclaration(
        VariableDeclarationSyntax declaration,
        EmitContext context,
        CppWriter writer,
        StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        SemanticModel model = context.GetSemanticModel(declaration.SyntaxTree);
        string typeSpelling = ResolveTypeSpelling(declaration, model);

        for (int i = 0; i < declaration.Variables.Count; i++)
        {
            VariableDeclaratorSyntax declarator = declaration.Variables[i];
            if (i == 0)
            {
                // First declarator carries the shared type prefix.
                EmitDeclarator(typeSpelling, declarator, parent, writer);
                continue;
            }

            // Subsequent declarators in a multi-declarator declaration share the
            // single leading type (C++ `int32_t i = 0, k = 1`), so the type
            // prefix is emitted only once: render just the name + initializer.
            writer.Append(", ");
            writer.Append(declarator.Identifier.ValueText);
            if (declarator.Initializer is { } initializer)
            {
                writer.Append(" = ");
                parent.Expressions.EmitExpression(initializer.Value);
            }
        }
    }

    /// <summary>
    /// Root an XObject-derived local in the shadow stack: allocate (or look up)
    /// its slot keyed by the local name, write the live reference into the slot,
    /// then emit a C++ RAII scope-clear guard whose destructor nulls the slot at
    /// C++ scope exit (so the shadow stack never reports a stale root once the
    /// local leaves scope).
    /// </summary>
    /// <param name="shadowStack">The bound per-method shadow-stack builder.</param>
    /// <param name="localName">The local's C# / C++ identifier (the slot value + the slot key suffix).</param>
    /// <param name="writer">The C++ writer the slot write + guard are emitted into.</param>
    private static void RootLocal(
        MethodShadowStackBuilder shadowStack,
        string localName,
        CppWriter writer)
    {
        int index = shadowStack.Allocate(LocalSlotKeyPrefix + localName, localName);
        shadowStack.EmitSlotWrite(index, localName, writer);

        // The RAII scope-clear guard: a unique-per-index local struct holding a
        // reference to the slot, whose destructor nulls it at C++ scope exit.
        // `_liveRefs[idx]` matches MethodShadowStackBuilder's slot element + null
        // assignment (XPtr<XObject>& bound to the slot, `= nullptr` in the dtor).
        string idx = index.ToString(CultureInfo.InvariantCulture);
        string slotRef = MethodShadowStackBuilder.ArrayName + "[" + idx + "]";
        writer.AppendLine(
            "struct " + GuardTypePrefix + idx + " { "
            + MethodShadowStackBuilder.SlotElementType + "& _s; "
            + "~" + GuardTypePrefix + idx + "() noexcept { _s = nullptr; } } "
            + GuardInstancePrefix + idx + "{ " + slotRef + " };");
    }

    /// <summary>
    /// True iff <paramref name="declaration"/>'s bound type derives from the
    /// engine root reference type <c>XObject</c> (via
    /// <see cref="AnalyzerHelpers.IsXObjectDerived"/>), resolved through
    /// <paramref name="model"/>. Works for a <c>var</c> declaration too (the
    /// model resolves the inferred type). A value type, a non-named type, or an
    /// unresolved type is not XObject-derived.
    /// </summary>
    private static bool IsXObjectDerivedLocal(VariableDeclarationSyntax declaration, SemanticModel model)
        => model.GetTypeInfo(declaration.Type).Type is INamedTypeSymbol named
            && AnalyzerHelpers.IsXObjectDerived(named);

    /// <summary>
    /// Resolve the C++ type spelling for a declaration: <c>auto</c> for a
    /// <c>var</c> declaration, else the C++ spelling of the declarator's bound
    /// type. A C# primitive maps to its fixed-width <c>&lt;cstdint&gt;</c> form;
    /// an XObject-DERIVED (engine reference) type maps to a POINTER over its
    /// <c>::</c>-qualified C++ name (<c>::Game::XActor*</c>) &#8211; matching how
    /// <see cref="MethodEmitter"/> / <see cref="PropertyEmitter"/> spell
    /// reference params / returns / fields, so an explicit-typed reference local
    /// (<c>XActor chosen = fallback;</c>) lowers to a pointer that agrees with
    /// its pointer-typed initializer / usage (FIX 4). A value struct stays
    /// by-value; an unresolved type falls back to the syntactic type text.
    /// </summary>
    private static string ResolveTypeSpelling(VariableDeclarationSyntax declaration, SemanticModel model)
    {
        if (declaration.Type.IsVar)
        {
            return "auto";
        }

        ITypeSymbol? type = model.GetTypeInfo(declaration.Type).Type;

        // FIX 4: an XObject-derived (engine reference) local is represented as a
        // raw pointer in the C++ object model, so it must be spelled as a pointer
        // (::Ns::T*) to agree with its pointer-typed initializer / usage. The
        // shared CppTypeName.RenderReference renderer (used by the cast / object-
        // creation rules) produces the same ::Ns::T* spelling the method / member
        // emitters use; a primitive / value type renders by value.
        if (CppTypeName.IsXObjectReference(type))
        {
            return CppTypeName.RenderReference(type);
        }

        return SpellCppType(type) ?? declaration.Type.ToString();
    }

    /// <summary>
    /// Map a bound C# type to its emitted C++ spelling, or null when the type
    /// is unresolved (the caller falls back to the syntactic type text). The
    /// C# primitive types map to their fixed-width <c>&lt;cstdint&gt;</c> forms
    /// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + the <c>char</c>
    /// mapping note (Section 5.3, FIX-B-HIGH-23): <c>char</c> maps to
    /// <c>char16_t</c> (a 16-bit UTF-16 code unit, NOT the platform-variable
    /// <c>wchar_t</c>).
    /// </summary>
    /// <param name="type">The bound type symbol (may be null).</param>
    /// <returns>The C++ type spelling, or null when unresolved.</returns>
    internal static string? SpellCppType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        return type.SpecialType switch
        {
            SpecialType.System_Void => "void",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Char => "char16_t",
            SpecialType.System_SByte => "int8_t",
            SpecialType.System_Byte => "uint8_t",
            SpecialType.System_Int16 => "int16_t",
            SpecialType.System_UInt16 => "uint16_t",
            SpecialType.System_Int32 => "int32_t",
            SpecialType.System_UInt32 => "uint32_t",
            SpecialType.System_Int64 => "int64_t",
            SpecialType.System_UInt64 => "uint64_t",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            _ => type.Name,
        };
    }
}
