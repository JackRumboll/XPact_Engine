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
/// method's natural signature and NO <c>XResult</c> out-param. When the body
/// roots live managed references the precise-GC shadow stack + stack-map
/// record (XIL2CPP Phase 6.g) wraps it (when it roots none, the shadow-stack
/// apparatus is elided and only the safe-point poll + body emit):
/// <code>
/// extern "C" &lt;ret&gt; &lt;LinkerSymbol&gt;(&lt;self?&gt;, &lt;params&gt;) noexcept {
///     ::XCore::Reflect::XPtr&lt;::XCore::Reflect::XObject&gt; _liveRefs[N] = {};
///     _liveRefs[0] = ...(reinterpret_cast&lt;...&gt;(self));  // self + XObject params
///     XPACT_SAFEPOINT_CHECK();
///     &lt;body&gt;
/// }
/// // FILE SCOPE, after the closing brace: the function is defined by here, so
/// // the registrar's address-of the linker symbol is valid; a file-scope static
/// // initializer runs at static-init time and actually registers (an in-body
/// // function-local static after a returning body is UNREACHABLE).
/// static const ::XCore::Reflect::FStackMapRecord _stackMap... = { ... };
/// [[maybe_unused]] static const bool _stackMapReg... = []() { ...Register(...); return true; }();
/// </code>
/// Tier 2 is <c>noexcept</c> (Constraint Section 2.8: it provably cannot throw).
/// </para>
/// <para>
/// <b>Tier 1 (shim).</b> The ABI-stable <c>XResult</c>-shimmed form. The body
/// (with its shadow stack + stack map keyed by the <c>_Body</c> symbol) lives
/// in a private <c>static</c> <c>_Body</c> helper; the exported
/// <c>extern "C"</c> <c>_Shim</c> wraps the call in a <c>try / catch</c> and
/// reports success / failure through the <c>::XCore::Exception::XResult*</c>
/// out-param (Dev-mode form; the Shipping <c>#ifdef</c> form is Phase 6.j):
/// <code>
/// static &lt;ret&gt; &lt;LinkerSymbol&gt;_Body(&lt;self?&gt;, &lt;params&gt;) {
///     // shadow stack + safe-point + body + stack-map record (as Tier 2 above)
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
    /// <summary>
    /// The 6.g GC-hook TODO marker. The method-body prologue NO LONGER emits
    /// this (it now emits the real precise-GC shadow stack + stack-map record
    /// via <see cref="MethodShadowStackBuilder"/>); the constant is retained
    /// because the virtual-dispatch thunk emitter (which forwards without
    /// rooting and so carries no shadow stack) still emits it as its GC hook.
    /// </summary>
    public const string GcHookComment = "TODO(6.g): FStackMapRecord + shadow-stack";

    /// <summary>The safe-point poll macro every method shell calls (after shadow-stack init) before its body.</summary>
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
    /// free function whose body is the shadow-stack prologue + the safe-point
    /// poll + the lowered statements, then -- at FILE scope after the closing
    /// brace -- the precise-GC stack-map record + registrar. The function's own
    /// linker symbol keys the stack-map registration.
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
        MethodShadowStackBuilder? rootedStack =
            EmitBodyPrologueAndBody(method, writer, bodyEmitter);
        writer.EndBlock();

        // The stack-map record + registrar emit at FILE scope AFTER the closing
        // brace: &<linkerSymbol> is now in scope (the function is fully defined)
        // and a file-scope static initializer runs at static-init time -- so it
        // actually registers, where an in-body function-local static after a
        // returning body would be dead code that never runs.
        EmitFileScopeStackMap(record.LinkerSymbol, rootedStack, writer);
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
        // The _Body helper carries the rooted body, so its symbol keys the
        // precise-GC stack-map registration (NOT the exported _Shim boundary).
        string bodySymbol = record.LinkerSymbol + BodySuffix;
        writer.BeginBlock(
            "static " + returnType + " " + bodySymbol + "(" + RenderParamList(parameters) + ")");
        MethodShadowStackBuilder? rootedStack =
            EmitBodyPrologueAndBody(method, writer, bodyEmitter);
        writer.EndBlock();

        // The stack-map record + registrar emit at FILE scope AFTER the _Body
        // closing brace, keyed off the _Body symbol (the function that carries
        // the rooted body): &<_Body> is now in scope and the file-scope static
        // initializer registers at static-init time. (An in-body function-local
        // static after the _Body's return would be unreachable dead code.)
        EmitFileScopeStackMap(bodySymbol, rootedStack, writer);

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
    /// Emit the shared body prologue + body (the IN-FUNCTION precise-GC
    /// apparatus, XIL2CPP Phase 6.g) via a TWO-PASS lowering so the shadow-stack
    /// array length <c>N</c> is known before the array declaration is written:
    /// <list type="number">
    ///   <item><description>
    ///     Bind a fresh <see cref="MethodShadowStackBuilder"/> onto a SECONDARY
    ///     <see cref="StatementEmitter"/> (sharing the caller's context +
    ///     registry), allocate <c>self</c> at index 0 (instance members) and
    ///     each XObject-derived parameter a slot, THEN lower the body into a
    ///     secondary buffer (so additional rooting allocations a rule makes are
    ///     counted before <c>N</c> is fixed).
    ///   </description></item>
    ///   <item><description>
    ///     Into the real writer, in ORDER: (1) the shadow-stack array
    ///     declaration <c>_liveRefs[N]</c>, (2) the <c>self</c> + parameter slot
    ///     writes, (3) <c>XPACT_SAFEPOINT_CHECK()</c> (after shadow-stack init),
    ///     (4) the flushed body buffer.
    ///   </description></item>
    /// </list>
    /// The <c>FStackMapRecord</c> + <c>XStackMapTable::Register</c> registrar is
    /// NOT emitted here: it is emitted at FILE scope after the function's closing
    /// brace by <see cref="EmitFileScopeStackMap"/> (an in-body function-local
    /// static after a returning body would be unreachable dead code, so the lazy
    /// init would never run and the stack map would never register). This method
    /// returns the populated builder (or null) so the caller can emit that
    /// file-scope record once the function definition is complete.
    /// <para>
    /// When the method roots NO managed references (<c>N == 0</c> -- e.g. a
    /// static leaf with no XObject parameters), the shadow-stack apparatus is
    /// elided entirely (no array, no slot writes) and null is returned (no
    /// file-scope record): a zero-length root array would be ill-formed and a
    /// method with no live refs needs no precise-GC map. The safe-point poll +
    /// body still emit.
    /// </para>
    /// </summary>
    /// <param name="method">The method whose body is lowered.</param>
    /// <param name="writer">The real C++ writer the function body is emitted into.</param>
    /// <param name="bodyEmitter">The caller's statement emitter (its context + registry are reused for the secondary pass).</param>
    /// <returns>
    /// The populated <see cref="MethodShadowStackBuilder"/> when the method roots
    /// at least one managed reference (the caller emits its file-scope stack-map
    /// record), or null when the method roots none.
    /// </returns>
    private static MethodShadowStackBuilder? EmitBodyPrologueAndBody(
        IMethodSymbol method,
        CppWriter writer,
        StatementEmitter bodyEmitter)
    {
        // -------------------------------------------------------------
        // Pass 1: lower the body into a SECONDARY buffer with a bound
        // shadow-stack builder, so N (the live-ref count) is final before the
        // array declaration is written into the real writer.
        // -------------------------------------------------------------
        MethodShadowStackBuilder shadowStack = new();
        List<(int Index, string Expr)> rootedSlotWrites = PreallocateRoots(method, shadowStack);

        // The secondary buffer starts at the real writer's CURRENT indent depth
        // (the function-body depth), so flushing it preserves the same
        // indentation the body would have had if lowered directly into writer.
        CppWriter bodyBuffer = new();
        for (int i = 0; i < writer.Depth; i++)
        {
            bodyBuffer.Indent();
        }

        StatementEmitter secondary = new(bodyEmitter.Context, bodyBuffer, bodyEmitter.Registry)
        {
            ShadowStackBuilder = shadowStack,
        };
        EmitBody(method, bodyBuffer, secondary);

        bool hasRoots = shadowStack.Count > 0;

        // -------------------------------------------------------------
        // Pass 2: emit the IN-FUNCTION parts into the real writer in the LOCKED
        // order. The stack-map record is emitted by the caller at file scope.
        // -------------------------------------------------------------

        // (1) The shadow-stack array declaration (only when there are roots).
        if (hasRoots)
        {
            shadowStack.EmitArrayDecl(writer);

            // (2) self + parameter slot writes (the pre-body root initialisation).
            foreach ((int index, string expr) in rootedSlotWrites)
            {
                shadowStack.EmitSlotWrite(index, expr, writer);
            }
        }

        // (3) The safe-point poll (now AFTER shadow-stack init).
        writer.AppendLine(SafepointCheck);

        // (4) Flush the lowered body buffer verbatim.
        writer.Append(bodyBuffer.Build());

        return hasRoots ? shadowStack : null;
    }

    /// <summary>The file-scope wrapping-namespace prefix each per-function stack-map block is emitted under.</summary>
    public const string StackMapNamespacePrefix = "_xstackmap_";

    /// <summary>
    /// Emit the precise-GC stack-map record + registrar at FILE / NAMESPACE
    /// scope, immediately after the function whose <paramref name="linkerSymbol"/>
    /// keys the registration (and which is therefore fully defined at this point,
    /// so <c>&amp;linkerSymbol</c> is in scope). The record + the
    /// <c>[[maybe_unused]] static const bool</c> registrar are produced by
    /// <see cref="MethodShadowStackBuilder.EmitStackMapRecord"/>; because the
    /// registrar is a FILE-scope static its lazy initializer runs at static-init
    /// time and actually performs the
    /// <c>::XCore::Reflect::XStackMapTable::Register(...)</c> -- unlike an in-body
    /// function-local static after a returning body, which would be unreachable
    /// dead code that never registers.
    /// <para>
    /// <b>Per-function wrapping namespace.</b> <see cref="MethodShadowStackBuilder.EmitStackMapRecord"/>
    /// names the record / registrar with the fixed identifiers <c>_stackMap</c> /
    /// <c>_stackMapReg</c>. At file scope MANY functions in one translation unit
    /// would then collide on those names (a C++ redefinition error). The record
    /// is therefore wrapped in a per-function namespace
    /// <c>namespace _xstackmap_&lt;sanitized-symbol&gt; { ... }</c> so the fixed names
    /// are unique per function. The function whose address is taken still resolves
    /// by unqualified lookup from inside the nested namespace (it lives in an
    /// enclosing scope), so <c>&amp;linkerSymbol</c> stays valid.
    /// </para>
    /// <para>
    /// No-op when <paramref name="rootedStack"/> is null (the method rooted no
    /// managed references, so there is no stack map to register).
    /// </para>
    /// </summary>
    /// <param name="linkerSymbol">The function linker symbol whose address keys the stack-map registration (the Tier-2 direct function, or the Tier-1 <c>_Body</c> helper). Must not be null when <paramref name="rootedStack"/> is non-null.</param>
    /// <param name="rootedStack">The populated shadow-stack builder, or null when the method roots no references.</param>
    /// <param name="writer">The real C++ writer (positioned at file scope after the function's closing brace). Must not be null.</param>
    private static void EmitFileScopeStackMap(
        string linkerSymbol,
        MethodShadowStackBuilder? rootedStack,
        CppWriter writer)
    {
        if (rootedStack is null)
        {
            return;
        }

        // Wrap the fixed-name record + registrar in a per-function namespace so
        // the file-scope identifiers never collide across functions in the TU.
        writer.BeginBlock("namespace " + StackMapNamespaceName(linkerSymbol));
        rootedStack.EmitStackMapRecord(linkerSymbol, writer);
        writer.EndBlock();
    }

    /// <summary>
    /// The deterministic per-function wrapping-namespace name the file-scope
    /// stack-map block is emitted under: the <see cref="StackMapNamespacePrefix"/>
    /// plus <paramref name="linkerSymbol"/> with every character that is not a
    /// C++ identifier character (a letter, a digit, or <c>'_'</c>) replaced by
    /// <c>'_'</c> -- so a constructor symbol carrying a <c>'$'</c> token still
    /// yields a valid, unique identifier. A pure function of the symbol (ordinal
    /// per-character mapping), so it is byte-deterministic.
    /// </summary>
    /// <param name="linkerSymbol">The function linker symbol. Must not be null.</param>
    /// <returns>The sanitized wrapping-namespace identifier.</returns>
    private static string StackMapNamespaceName(string linkerSymbol)
    {
        StringBuilder sb = new(StackMapNamespacePrefix.Length + linkerSymbol.Length);
        sb.Append(StackMapNamespacePrefix);
        foreach (char c in linkerSymbol)
        {
            bool isIdentChar = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9') or '_';
            sb.Append(isIdentChar ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pre-allocate the always-live root slots BEFORE the body is lowered:
    /// <c>self</c> at index 0 for an instance member (architect decision 7) then
    /// each XObject-derived parameter, in declaration order. Returns the ordered
    /// (slot-index, slot-expression) writes the prologue emits after the array
    /// declaration. A value-typed / non-reference parameter is not rooted.
    /// </summary>
    /// <param name="method">The method being emitted.</param>
    /// <param name="shadowStack">The per-method shadow-stack builder to allocate into.</param>
    /// <returns>The ordered slot writes (index + C++ expression) for the rooted self + parameters.</returns>
    private static List<(int Index, string Expr)> PreallocateRoots(
        IMethodSymbol method,
        MethodShadowStackBuilder shadowStack)
    {
        List<(int Index, string Expr)> writes = new();

        // self at index 0 for an instance member (instance method / accessor /
        // operator, or a constructor); a static member has no self.
        if (HasSelfParameter(method))
        {
            int selfIndex = shadowStack.Allocate(SelfSlotKey, SelfParamName);
            writes.Add((selfIndex, SelfParamName));
        }

        // Each XObject-derived (or XObject) reference parameter roots a slot.
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (!IsRootableParameter(parameter))
            {
                continue;
            }

            string name = CppIdentifier(parameter.Name);
            int index = shadowStack.Allocate(ParameterSlotKey(parameter), name);
            writes.Add((index, name));
        }

        return writes;
    }

    /// <summary>
    /// True iff <paramref name="parameter"/> is a by-value managed reference
    /// whose type IS or DERIVES FROM the engine <c>XObject</c> (so it must be
    /// rooted in the shadow stack). A by-ref / out / in parameter is an alias
    /// the caller already roots; a value-typed parameter holds no managed
    /// reference -- neither roots a slot.
    /// </summary>
    private static bool IsRootableParameter(IParameterSymbol parameter)
    {
        if (parameter.RefKind != RefKind.None)
        {
            return false;
        }

        return parameter.Type is INamedTypeSymbol named
            && (Analysis.AnalyzerHelpers.IsXObjectType(named)
                || Analysis.AnalyzerHelpers.IsXObjectDerived(named));
    }

    /// <summary>The stable shadow-stack symbol key the instance <c>self</c> slot is allocated under.</summary>
    private const string SelfSlotKey = "$self";

    /// <summary>
    /// The stable shadow-stack symbol key for a parameter slot: the
    /// <c>$param:</c> prefix + the parameter ordinal + <c>:</c> + the parameter
    /// name, so two distinct parameters never collide and the key is a pure
    /// function of the parameter (deterministic).
    /// </summary>
    private static string ParameterSlotKey(IParameterSymbol parameter)
        => "$param:" + parameter.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + parameter.Name;

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
