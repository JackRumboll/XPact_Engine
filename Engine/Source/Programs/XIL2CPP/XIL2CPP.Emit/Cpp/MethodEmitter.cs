// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The XIL2CPP Pass-6 method emitter: emits the C++ free-function SHELL for one
/// emittable method (or constructor), per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2 (Pass 6) + <c>/Documents/XToolchainContract.html</c>
/// Section 2.3 (the <c>extern "C"</c> free-function form) +
/// Section 2.7 / 2.8 (the Tier-1 shim vs Tier-2 direct calling conventions).
/// The body is lowered through a <see cref="StatementEmitter"/>; in this
/// foundation wave the body-lowering rule set is empty so the body is a run of
/// <c>// TODO(6.e)</c> markers -- the emitter's job here is the function SHELL
/// (signature, tier shape, <c>noexcept</c>, the implicit <c>self</c> first
/// parameter, the <c>$ctor</c> linker symbol, and the safe-point + 6.g GC
/// hook comments).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 2 (direct).</b> A zero-overhead plain free function with the C#
/// method's natural signature and NO <c>XResult</c> out-param:
/// <code>
/// extern "C" &lt;ret&gt; &lt;LinkerSymbol&gt;(&lt;self?&gt;, &lt;params&gt;) noexcept {
///     // TODO(6.g): FStackMapRecord + shadow-stack
///     XPACT_SAFEPOINT_CHECK();
///     &lt;body&gt;
/// }
/// </code>
/// Tier 2 is <c>noexcept</c> (Constraint Section 2.8: it provably cannot throw).
/// </para>
/// <para>
/// <b>Tier 1 (shim).</b> The ABI-stable <c>XResult</c>-shimmed form. The body
/// lives in a private <c>static</c> <c>_Body</c> helper; the exported
/// <c>extern "C"</c> <c>_Shim</c> wraps the call in a <c>try / catch</c> and
/// reports success / failure through the <c>::XCore::Exception::XResult*</c>
/// out-param (Dev-mode form; the Shipping <c>#ifdef</c> form is Phase 6.j):
/// <code>
/// static &lt;ret&gt; &lt;LinkerSymbol&gt;_Body(&lt;self?&gt;, &lt;params&gt;) {
///     // TODO(6.g): FStackMapRecord + shadow-stack
///     XPACT_SAFEPOINT_CHECK();
///     &lt;body&gt;
/// }
/// extern "C" void &lt;LinkerSymbol&gt;_Shim(&lt;self?&gt;, &lt;params&gt;, ::XCore::Exception::XResult* outResult) {
///     try {
///         &lt;ret r = &gt;&lt;LinkerSymbol&gt;_Body(&lt;args&gt;);
///         outResult-&gt;discriminator = ::XCore::Exception::XResult::Success;
///     } catch (const ::XCore::Exception::XCSharpException&amp; ex) {
///         outResult-&gt;discriminator = ::XCore::Exception::XResult::Error;
///         outResult-&gt;exception = ex.GetManagedException();
///     }
/// }
/// </code>
/// The <c>_Shim</c> is NOT <c>noexcept</c> (it is the boundary that converts a
/// C# exception into the <c>XResult</c> discriminator).
/// </para>
/// <para>
/// <b>Self / static / constructor.</b> An instance method takes a typed
/// <c>self</c> pointer as the first parameter (a pointer to the containing
/// type); a static method omits it; a constructor uses the <c>$ctor</c>
/// <see cref="ManglingRecord.LinkerSymbol"/> and (like every instance member)
/// takes the freshly-allocated <c>self</c> as its first parameter.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Every rendered fragment is a
/// pure function of the symbol + the context: ordinal string operations,
/// invariant-culture formatting, explicit <c>\n</c> via <see cref="CppWriter"/>,
/// no <see cref="DateTime"/> / <see cref="Guid"/> / <see cref="Random"/>. Two
/// runs over the same symbol emit byte-identical C++.
/// </para>
/// </remarks>
public sealed class MethodEmitter
{
    /// <summary>The 6.g GC-hook TODO marker emitted as the first body line of every method shell.</summary>
    public const string GcHookComment = "TODO(6.g): FStackMapRecord + shadow-stack";

    /// <summary>The safe-point poll macro every method shell calls before its body.</summary>
    public const string SafepointCheck = "XPACT_SAFEPOINT_CHECK();";

    /// <summary>The runtime exception-result type the Tier-1 shim out-param points at (Contract Section 2.7).</summary>
    public const string XResultType = "::XCore::Exception::XResult";

    /// <summary>The runtime C# exception wrapper the Tier-1 shim catches (Contract Section 2.7).</summary>
    public const string XCSharpExceptionType = "::XCore::Exception::XCSharpException";

    /// <summary>The <c>XResult</c> discriminator value the shim sets on a clean return.</summary>
    public const string SuccessDiscriminator = "::XCore::Exception::XResult::Success";

    /// <summary>The <c>XResult</c> discriminator value the shim sets when a C# exception escaped the body.</summary>
    public const string ErrorDiscriminator = "::XCore::Exception::XResult::Error";

    /// <summary>The <c>_Body</c> linker-symbol suffix for the Tier-1 private body helper.</summary>
    public const string BodySuffix = "_Body";

    /// <summary>The <c>_Shim</c> linker-symbol suffix for the Tier-1 exported boundary function.</summary>
    public const string ShimSuffix = "_Shim";

    /// <summary>The out-param name the Tier-1 shim writes its <c>XResult</c> discriminator into.</summary>
    public const string OutResultName = "outResult";

    /// <summary>The implicit instance-method first-parameter name (Contract Section 2.3 free-function <c>self</c>).</summary>
    public const string SelfParamName = "self";

    /// <summary>
    /// Emit the C++ free-function shell for <paramref name="method"/> into
    /// <paramref name="writer"/>, choosing the Tier-1 shim or Tier-2 direct
    /// shape from <paramref name="context"/>'s Pass-4 tier verdict. The body is
    /// lowered through <paramref name="bodyEmitter"/> (a
    /// <see cref="StatementEmitter"/> bound to the same context + writer); with
    /// no discovered rules the body renders as <c>// TODO(6.e)</c> markers.
    /// </summary>
    /// <param name="method">The method / constructor symbol to emit. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <param name="bodyEmitter">The statement emitter used to lower the method body. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    /// <exception cref="InvalidOperationException">If the method has no Pass-5 mangling record in the context.</exception>
    public void EmitMethod(
        IMethodSymbol method,
        EmitContext context,
        CppWriter writer,
        StatementEmitter bodyEmitter)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(bodyEmitter);

        StableId id = StableId.FromSymbol(method);
        ManglingRecord record = context.FindMangling(id)
            ?? throw new InvalidOperationException(
                $"No Pass-5 mangling record for '{id.Value}'; cannot emit its method shell.");

        FunctionTier tier = context.FindTier(id);
        string returnType = CppTypeMapper.MapReturn(method);
        IReadOnlyList<CppParam> parameters = BuildParameters(method);

        if (tier == FunctionTier.Tier2)
        {
            EmitTier2Direct(record, returnType, parameters, method, context, writer, bodyEmitter);
        }
        else
        {
            EmitTier1Shim(record, returnType, parameters, method, context, writer, bodyEmitter);
        }
    }

    /// <summary>
    /// Emit the Tier-2 direct form: a single <c>noexcept</c> <c>extern "C"</c>
    /// free function whose body is the safe-point poll + the 6.g GC-hook
    /// comment + the lowered statements.
    /// </summary>
    private static void EmitTier2Direct(
        ManglingRecord record,
        string returnType,
        IReadOnlyList<CppParam> parameters,
        IMethodSymbol method,
        EmitContext context,
        CppWriter writer,
        StatementEmitter bodyEmitter)
    {
        string header = "extern \"C\" " + returnType + " " + record.LinkerSymbol
            + "(" + RenderParamList(parameters) + ") noexcept";
        writer.BeginBlock(header);
        EmitBodyPrologueAndBody(method, writer, bodyEmitter);
        writer.EndBlock();
    }

    /// <summary>
    /// Emit the Tier-1 shim form (Dev-mode): a private <c>static</c>
    /// <c>_Body</c> helper carrying the real body, then the exported
    /// <c>extern "C"</c> <c>_Shim</c> that calls it inside a <c>try / catch</c>
    /// and reports through the <c>XResult*</c> out-param. The Shipping
    /// <c>#ifdef</c> form is Phase 6.j.
    /// </summary>
    private static void EmitTier1Shim(
        ManglingRecord record,
        string returnType,
        IReadOnlyList<CppParam> parameters,
        IMethodSymbol method,
        EmitContext context,
        CppWriter writer,
        StatementEmitter bodyEmitter)
    {
        // Private body helper: static <ret> <Symbol>_Body(<self?>, <params>).
        string bodySymbol = record.LinkerSymbol + BodySuffix;
        writer.BeginBlock(
            "static " + returnType + " " + bodySymbol + "(" + RenderParamList(parameters) + ")");
        EmitBodyPrologueAndBody(method, writer, bodyEmitter);
        writer.EndBlock();

        // Exported shim boundary: appends the XResult* out-param after the
        // method's own parameters. NOT noexcept (it converts a C# exception
        // into the XResult discriminator).
        string shimSymbol = record.LinkerSymbol + ShimSuffix;
        string shimParams = AppendOutResult(RenderParamList(parameters));
        writer.BeginBlock("extern \"C\" void " + shimSymbol + "(" + shimParams + ")");
        writer.BeginBlock("try");

        string callArgs = RenderArgList(parameters);
        bool isVoid = StringComparer.Ordinal.Equals(returnType, "void");
        if (isVoid)
        {
            writer.AppendLine(bodySymbol + "(" + callArgs + ");");
        }
        else
        {
            writer.AppendLine(returnType + " result = " + bodySymbol + "(" + callArgs + ");");
            writer.AppendLine("(void)result;");
        }
        writer.AppendLine(OutResultName + "->discriminator = " + SuccessDiscriminator + ";");

        writer.Unindent();
        writer.AppendLine("} catch (const " + XCSharpExceptionType + "& ex) {");
        writer.Indent();
        writer.AppendLine(OutResultName + "->discriminator = " + ErrorDiscriminator + ";");
        writer.AppendLine(OutResultName + "->exception = ex.GetManagedException();");
        writer.EndBlock();

        writer.EndBlock();
    }

    /// <summary>
    /// Emit the shared body prologue (the 6.g GC-hook comment + the safe-point
    /// poll) followed by the lowered method body.
    /// </summary>
    private static void EmitBodyPrologueAndBody(
        IMethodSymbol method,
        CppWriter writer,
        StatementEmitter bodyEmitter)
    {
        writer.AppendComment(GcHookComment);
        writer.AppendLine(SafepointCheck);
        EmitBody(method, writer, bodyEmitter);
    }

    /// <summary>
    /// Lower the method's syntactic body through <paramref name="bodyEmitter"/>.
    /// A block body has each statement lowered in source order; an
    /// expression-bodied member has its arrow expression lowered; a method with
    /// no syntactic body (abstract / extern / partial-without-impl) emits a
    /// single explanatory comment. With zero discovered rules every lowered node
    /// renders as a <c>// TODO(6.e)</c> marker (the expected foundation state).
    /// </summary>
    private static void EmitBody(IMethodSymbol method, CppWriter writer, StatementEmitter bodyEmitter)
    {
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            SyntaxNode node = reference.GetSyntax();
            switch (node)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax m:
                    if (m.Body is not null)
                    {
                        foreach (SyntaxNode statement in m.Body.Statements)
                        {
                            bodyEmitter.EmitStatement(statement);
                        }
                        return;
                    }
                    if (m.ExpressionBody is not null)
                    {
                        bodyEmitter.Expressions.EmitExpression(m.ExpressionBody.Expression);
                        return;
                    }
                    break;

                case Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax a:
                    if (a.Body is not null)
                    {
                        foreach (SyntaxNode statement in a.Body.Statements)
                        {
                            bodyEmitter.EmitStatement(statement);
                        }
                        return;
                    }
                    if (a.ExpressionBody is not null)
                    {
                        bodyEmitter.Expressions.EmitExpression(a.ExpressionBody.Expression);
                        return;
                    }
                    break;

                case Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax lf:
                    if (lf.Body is not null)
                    {
                        foreach (SyntaxNode statement in lf.Body.Statements)
                        {
                            bodyEmitter.EmitStatement(statement);
                        }
                        return;
                    }
                    if (lf.ExpressionBody is not null)
                    {
                        bodyEmitter.Expressions.EmitExpression(lf.ExpressionBody.Expression);
                        return;
                    }
                    break;

                case Microsoft.CodeAnalysis.CSharp.Syntax.ArrowExpressionClauseSyntax arrow:
                    bodyEmitter.Expressions.EmitExpression(arrow.Expression);
                    return;
            }
        }

        // No syntactic body to lower (abstract / extern / runtime-supplied).
        writer.AppendComment("TODO(6.e): no syntactic body to lower");
    }

    /// <summary>
    /// Build the ordered C++ parameter list for <paramref name="method"/>: the
    /// implicit typed <c>self</c> first (for an instance member -- including a
    /// constructor; a static method omits it), then each declared parameter
    /// mapped to its C++ type. Public so the
    /// <see cref="VirtualDispatcherEmitter"/> reuses the identical list.
    /// </summary>
    /// <param name="method">The method symbol. Must not be null.</param>
    /// <returns>The ordered C++ parameters (self first when present).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    public static IReadOnlyList<CppParam> BuildParameters(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);

        List<CppParam> parameters = new();

        // Instance members (plain methods, accessors, operators that are
        // instance, AND constructors -- the freshly-allocated object) take a
        // typed self first. Static methods + the static constructor do not.
        if (HasSelfParameter(method))
        {
            parameters.Add(new CppParam(
                CppTypeMapper.SelfPointerType(method.ContainingType), SelfParamName));
        }

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            parameters.Add(new CppParam(
                CppTypeMapper.MapParameter(parameter), CppIdentifier(parameter.Name)));
        }

        return parameters;
    }

    /// <summary>
    /// True iff <paramref name="method"/> takes the implicit instance
    /// <c>self</c> first parameter: an instance method / accessor / operator,
    /// or an instance constructor; false for a static method or the static
    /// constructor.
    /// </summary>
    /// <param name="method">The method symbol. Must not be null.</param>
    /// <returns>True iff a typed <c>self</c> first parameter is emitted.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    public static bool HasSelfParameter(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (method.MethodKind == MethodKind.StaticConstructor)
        {
            return false;
        }
        if (method.MethodKind == MethodKind.Constructor)
        {
            return true;
        }
        return !method.IsStatic && method.ContainingType is not null;
    }

    /// <summary>
    /// Render an ordered parameter list as its C++ declaration text
    /// (<c>"&lt;type&gt; &lt;name&gt;, ..."</c>); the empty string for no
    /// parameters.
    /// </summary>
    /// <param name="parameters">The parameters to render. Must not be null.</param>
    /// <returns>The parenthesized-body parameter list (without the parentheses).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="parameters"/> is null.</exception>
    public static string RenderParamList(IReadOnlyList<CppParam> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        StringBuilder sb = new();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(parameters[i].Type);
            sb.Append(' ');
            sb.Append(parameters[i].Name);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render an ordered parameter list as its C++ call-argument text
    /// (the parameter names, comma-separated); the empty string for none.
    /// </summary>
    /// <param name="parameters">The parameters to render. Must not be null.</param>
    /// <returns>The comma-separated argument names.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="parameters"/> is null.</exception>
    public static string RenderArgList(IReadOnlyList<CppParam> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        StringBuilder sb = new();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(parameters[i].Name);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Append the Tier-1 shim's <c>XResult*</c> out-param to a rendered
    /// parameter list (after a separating <c>", "</c> when the list is
    /// non-empty).
    /// </summary>
    private static string AppendOutResult(string renderedParams)
    {
        string outParam = XResultType + "* " + OutResultName;
        if (renderedParams.Length == 0)
        {
            return outParam;
        }
        return renderedParams + ", " + outParam;
    }

    /// <summary>
    /// Sanitize a C# identifier to a safe C++ identifier: a null / empty name
    /// becomes a positional placeholder is the caller's concern; here a valid
    /// C# name is emitted verbatim (C# and C++ identifier grammars agree for
    /// the names XIL2CPP accepts). Kept as a seam so a future reserved-word
    /// escape lands in one place.
    /// </summary>
    private static string CppIdentifier(string name)
        => string.IsNullOrEmpty(name) ? "_" : name;
}

/// <summary>
/// One rendered C++ function parameter: its C++ type spelling and its C++
/// identifier. A small immutable carrier the
/// <see cref="MethodEmitter"/> / <see cref="VirtualDispatcherEmitter"/> thread
/// through their signature / argument rendering.
/// </summary>
/// <param name="Type">The C++ type spelling (e.g. <c>int32_t</c>, <c>::Game::Widget*</c>).</param>
/// <param name="Name">The C++ parameter identifier (e.g. <c>self</c>, <c>x</c>).</param>
public readonly record struct CppParam(string Type, string Name);

/// <summary>
/// The deterministic C# -&gt; C++ type mapping the Pass-6 emitters use for
/// function signatures, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5
/// (the type-lowering rules: C# reference types lower to a typed
/// <c>XObject</c>-rooted pointer; C# primitives lower to the fixed-width
/// <c>&lt;cstdint&gt;</c> spellings). It carries NO ambient state, so the
/// mapping is byte-deterministic.
/// </summary>
/// <remarks>
/// <para>
/// This is the SHELL-level mapping for the method/dispatcher signatures (the
/// 6.e wave). It is deliberately conservative: a reference type maps to a
/// pointer to its <c>::</c>-qualified C++ type name; a primitive maps to its
/// fixed-width spelling; anything not yet modelled falls back to a clearly
/// marked <c>XObject*</c> (reference) or <c>/*?*/ ...</c> placeholder that a
/// later type-lowering wave refines. The mapping never throws, so a method
/// shell always emits.
/// </para>
/// </remarks>
internal static class CppTypeMapper
{
    /// <summary>The runtime base reference-pointer type a not-yet-modelled reference type falls back to.</summary>
    public const string GenericObjectPointer = "::XCore::XObject*";

    /// <summary>
    /// Map a method's return type to its C++ spelling (<c>void</c> stays
    /// <c>void</c>).
    /// </summary>
    /// <param name="method">The method symbol. Must not be null.</param>
    /// <returns>The C++ return-type spelling.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    public static string MapReturn(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);

        // A constructor's natural C++ return is void (the freshly-allocated
        // self is the implicit first parameter, not the return value).
        if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
        {
            return "void";
        }
        return MapType(method.ReturnType);
    }

    /// <summary>
    /// Map a parameter to its C++ type spelling. A by-ref / out / in / ref
    /// readonly parameter maps to a reference (<c>&amp;</c>) over the mapped
    /// element type; a by-value parameter maps to the mapped type directly.
    /// </summary>
    /// <param name="parameter">The parameter symbol. Must not be null.</param>
    /// <returns>The C++ parameter-type spelling.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="parameter"/> is null.</exception>
    public static string MapParameter(IParameterSymbol parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        string mapped = MapType(parameter.Type);
        return parameter.RefKind switch
        {
            RefKind.Ref => mapped + "&",
            RefKind.Out => mapped + "&",
            RefKind.In => "const " + mapped + "&",
            RefKind.RefReadOnlyParameter => "const " + mapped + "&",
            _ => mapped,
        };
    }

    /// <summary>
    /// The typed <c>self</c> pointer for an instance member: a pointer to the
    /// containing type's C++ name.
    /// </summary>
    /// <param name="containingType">The member's containing type. Must not be null.</param>
    /// <returns>The C++ self-pointer spelling (e.g. <c>::Game::Widget*</c>).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="containingType"/> is null.</exception>
    public static string SelfPointerType(INamedTypeSymbol containingType)
    {
        ArgumentNullException.ThrowIfNull(containingType);
        return QualifiedTypeName(containingType) + "*";
    }

    /// <summary>
    /// Map an arbitrary C# type to its C++ spelling: a special-type keyword
    /// where one exists, else a reference type to a pointer over its
    /// <c>::</c>-qualified name, else (value struct) the qualified name by
    /// value.
    /// </summary>
    private static string MapType(ITypeSymbol type)
    {
        string? primitive = PrimitiveSpelling(type.SpecialType);
        if (primitive is not null)
        {
            return primitive;
        }

        if (type is IArrayTypeSymbol)
        {
            // Arrays are a managed reference type rooted in the GC; the element
            // mapping is a later type-lowering concern, so the shell uses the
            // generic object pointer.
            return GenericObjectPointer;
        }

        if (type.IsReferenceType)
        {
            return QualifiedTypeName(type) + "*";
        }

        if (type.TypeKind == TypeKind.TypeParameter)
        {
            // Open type parameters are resolved by the generic-instantiation
            // wave; the shell roots them as the generic object pointer (the
            // common XObject case) which is conservatively correct.
            return type.IsValueType ? type.Name : GenericObjectPointer;
        }

        // A value type / struct passed by value.
        return QualifiedTypeName(type);
    }

    /// <summary>
    /// Render a type's <c>::</c>-qualified C++ name (leading <c>::</c> +
    /// namespace path + nested-type chain). The global namespace contributes
    /// nothing; nested types join with <c>::</c>.
    /// </summary>
    private static string QualifiedTypeName(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return "::" + type.Name;
        }

        StringBuilder sb = new();
        sb.Append("::");

        // Namespace path (outermost-first).
        List<string> nsParts = new();
        for (INamespaceSymbol? ns = named.ContainingNamespace;
            ns is not null && !ns.IsGlobalNamespace;
            ns = ns.ContainingNamespace)
        {
            nsParts.Add(ns.Name);
        }
        for (int i = nsParts.Count - 1; i >= 0; i--)
        {
            sb.Append(nsParts[i]);
            sb.Append("::");
        }

        // Nested-type chain (outermost-first).
        List<string> typeChain = new();
        for (INamedTypeSymbol? t = named; t is not null; t = t.ContainingType)
        {
            typeChain.Add(t.Name);
        }
        for (int i = typeChain.Count - 1; i >= 0; i--)
        {
            sb.Append(typeChain[i]);
            if (i > 0)
            {
                sb.Append("::");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The fixed-width / keyword C++ spelling for a C# special type, or null
    /// when the type is not a mapped primitive. <c>int</c> -&gt; <c>int32_t</c>,
    /// <c>bool</c> -&gt; <c>bool</c>, <c>float</c> -&gt; <c>float</c>, etc.
    /// (Contract / spec Section 5 primitive lowering.)
    /// </summary>
    private static string? PrimitiveSpelling(SpecialType specialType) => specialType switch
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
        SpecialType.System_IntPtr => "intptr_t",
        SpecialType.System_UIntPtr => "uintptr_t",
        _ => null,
    };
}
