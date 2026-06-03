// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The kind of live managed reference a <see cref="LiveLocalRecord"/> describes:
/// the instance <c>self</c> receiver, a method parameter, or a method-body
/// local. The kind matches the role the emitter's shadow stack assigns the
/// slot (XIL2CPP Phase 6.g): <c>self</c> roots index 0, parameters root the
/// next slots (in declaration order), and locals root the remainder (allocated
/// by the body-lowering rules as they encounter the declarations).
/// </summary>
public enum LiveLocalKind
{
    /// <summary>The instance <c>self</c> receiver (always rooted at shadow-stack index 0 for an instance member).</summary>
    SelfReceiver,

    /// <summary>A by-value XObject-derived (or XObject) method parameter.</summary>
    Parameter,

    /// <summary>An XObject-derived (or XObject) method-body local variable.</summary>
    Local,
}

/// <summary>
/// One live managed reference an emittable method roots in the precise-GC
/// shadow stack (XIL2CPP Phase 6.g), per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 5.x (precise rooting) + <c>/Documents/XCoreXObject.html</c>
/// Section 5.2 (the stack-map protocol). The <see cref="LiveLocalAnalyzer"/>
/// records one of these per <c>self</c> (instance members), per by-value
/// XObject-derived parameter, and per XObject-derived body local -- the
/// coverage ORACLE the Phase 6.g stack-map / shadow-stack gates measure the
/// emitter against.
/// </summary>
/// <param name="ContainingMethodDisplay">
/// The fully-qualified display string of the method / accessor / constructor
/// that owns the rooted reference (the join key the coverage gate groups by).
/// </param>
/// <param name="SymbolDisplay">
/// The display string of the rooted symbol: <c>self</c> for the receiver, the
/// parameter / local name otherwise (qualified enough to be unambiguous within
/// the method).
/// </param>
/// <param name="DeclarationIndex">
/// The 0-based shadow-stack allocation index the emitter assigns this slot:
/// <c>self</c> is 0 (instance members), the rootable parameters follow in
/// declaration order, then the body locals follow in document order. The index
/// is <c>* 8</c> the slot's <c>liveRefOffsets</c> byte offset.
/// </param>
/// <param name="Kind">Whether the reference is the <c>self</c> receiver, a parameter, or a local.</param>
public sealed record LiveLocalRecord(
    string ContainingMethodDisplay,
    string SymbolDisplay,
    int DeclarationIndex,
    LiveLocalKind Kind);

/// <summary>
/// Pass-3 live-managed-reference enumeration (WU-6G-STACKMAP) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.x (precise rooting) +
/// <c>/Documents/XCoreXObject.html</c> Section 5.2 (the stack-map protocol).
/// Walks every emittable method / accessor / constructor in the unit (in
/// deterministic source order) and records a <see cref="LiveLocalRecord"/> for
/// each managed reference the method roots in the precise-GC shadow stack:
/// <list type="bullet">
///   <item><description>
///     the instance <c>self</c> receiver, for an instance member (an instance
///     method / accessor / operator, or an instance constructor) -- always
///     rooted at shadow-stack index 0 (it is the receiver the body may store
///     through). A static member has no <c>self</c>.
///   </description></item>
///   <item><description>
///     each by-value parameter whose type IS or DERIVES FROM the engine
///     <c>XObject</c> (a by-ref / out / in parameter is an alias the caller
///     already roots, and a value-typed parameter holds no managed reference --
///     neither roots a slot), in declaration order.
///   </description></item>
///   <item><description>
///     each method-body local whose type IS or DERIVES FROM <c>XObject</c>, in
///     document order (the order the body-lowering rules encounter the
///     declarations + allocate their slots).
///   </description></item>
/// </list>
/// This is the coverage ORACLE the Phase 6.g
/// <c>X-IL2CPP-STACKMAP-COV</c> / <c>X-IL2CPP-SHADOWSTACK-COV</c> gates measure
/// the emitter against: every method that roots at least one reference must emit
/// a file-scope <c>FStackMapRecord</c> whose <c>numLiveRefs</c> equals the
/// <see cref="LiveLocalKind.SelfReceiver"/> + <see cref="LiveLocalKind.Parameter"/>
/// record count for that method, and every rooted reference must have a
/// <c>_liveRefs</c> slot write in the emitted body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrors the emitter exactly.</b> The <c>self</c> + parameter rules are the
/// same predicate the <c>MethodEmitter</c> prologue uses to pre-allocate its
/// always-live root slots, so the oracle's self + parameter count is the
/// method's prologue-rooted slot count (the <c>FStackMapRecord.numLiveRefs</c>
/// for a body with no further local rooting). The local rule reflects which
/// body locals are rootable (a future body-lowering rule roots exactly these).
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Parsed files are visited in
/// their canonical Pass-1 ordinal order; methods in document (pre-order) order;
/// within a method <c>self</c> is recorded first, then parameters in
/// declaration order, then locals in document order. No ambient hash ordering,
/// no <c>DateTime</c> / <c>Guid</c> / <c>Random</c>.
/// </para>
/// </remarks>
public sealed class LiveLocalAnalyzer : ISemanticAnalyzer
{
    /// <summary>The receiver display string recorded for the instance <c>self</c> slot.</summary>
    public const string SelfDisplay = "self";

    /// <summary>
    /// Display format for the containing-method symbol: fully namespace-qualified
    /// containing types plus the member name + parameter types, so the recorded
    /// display string is unambiguous across overloads + types that share a simple
    /// name (the coverage gate keys on it). Deterministic -- no culture / ambient
    /// state.
    /// </summary>
    private static readonly SymbolDisplayFormat s_methodFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <inheritdoc/>
    public string Name => "LiveLocalAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case BaseMethodDeclarationSyntax method:
                        AnalyzeMethod(model, method, builder);
                        break;

                    case AccessorDeclarationSyntax accessor:
                        AnalyzeAccessor(model, accessor, builder);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Record the rooted references for a method / constructor / operator
    /// declaration: <c>self</c> (instance members), the rootable parameters,
    /// then the rootable body locals.
    /// </summary>
    private void AnalyzeMethod(
        SemanticModel model,
        BaseMethodDeclarationSyntax declaration,
        Pass3ResultBuilder builder)
    {
        if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol method)
        {
            return;
        }

        RecordRoots(model, method, declaration, builder);
    }

    /// <summary>
    /// Record the rooted references for a property / indexer / event accessor
    /// declaration. An accessor has the same <c>self</c> + body-local rooting as
    /// any instance member (its synthesized value parameter is not an XObject
    /// by-value managed reference the body roots).
    /// </summary>
    private void AnalyzeAccessor(
        SemanticModel model,
        AccessorDeclarationSyntax declaration,
        Pass3ResultBuilder builder)
    {
        if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol accessor)
        {
            return;
        }

        RecordRoots(model, accessor, declaration, builder);
    }

    /// <summary>
    /// Record the ordered rooted references for <paramref name="method"/>:
    /// <c>self</c> at index 0 (instance members), then each rootable parameter in
    /// declaration order, then each rootable body local in document order. The
    /// allocation index advances across all three so it matches the emitter's
    /// shadow-stack slot index.
    /// </summary>
    private void RecordRoots(
        SemanticModel model,
        IMethodSymbol method,
        SyntaxNode declaration,
        Pass3ResultBuilder builder)
    {
        string methodDisplay = method.ToDisplayString(s_methodFormat);
        int index = 0;

        // self at index 0 for an instance member (instance method / accessor /
        // operator, or an instance constructor); a static member has no self.
        if (HasSelfReceiver(method))
        {
            builder.Add(new LiveLocalRecord(
                methodDisplay, SelfDisplay, index, LiveLocalKind.SelfReceiver));
            index++;
        }

        // Each by-value XObject (or XObject-derived) reference parameter roots a
        // slot, in declaration order.
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (!IsRootableReference(parameter.RefKind, parameter.Type))
            {
                continue;
            }

            builder.Add(new LiveLocalRecord(
                methodDisplay, parameter.Name, index, LiveLocalKind.Parameter));
            index++;
        }

        // Each XObject (or XObject-derived) body local roots a slot, in document
        // order (the order a body-lowering rule encounters + allocates them).
        foreach (LocalDeclarationStatementSyntax local in
            EnumerateBodyLocalDeclarations(declaration))
        {
            foreach (VariableDeclaratorSyntax variable in local.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(variable) is not ILocalSymbol localSymbol)
                {
                    continue;
                }

                if (!IsRootableReference(RefKind.None, localSymbol.Type))
                {
                    continue;
                }

                builder.Add(new LiveLocalRecord(
                    methodDisplay, localSymbol.Name, index, LiveLocalKind.Local));
                index++;
            }
        }
    }

    /// <summary>
    /// Enumerate the <see cref="LocalDeclarationStatementSyntax"/> in
    /// <paramref name="declaration"/>'s body in document order. Returns nothing
    /// for a member with no syntactic body (expression-bodied / abstract /
    /// extern -- none declares a block local). Nested local functions /
    /// lambdas are NOT descended into (their locals belong to their own emitted
    /// method, not this one).
    /// </summary>
    private static IEnumerable<LocalDeclarationStatementSyntax> EnumerateBodyLocalDeclarations(
        SyntaxNode declaration)
    {
        BlockSyntax? body = declaration switch
        {
            BaseMethodDeclarationSyntax m => m.Body,
            AccessorDeclarationSyntax a => a.Body,
            _ => null,
        };

        if (body is null)
        {
            yield break;
        }

        foreach (SyntaxNode node in body.DescendantNodes(descendIntoChildren: ShouldDescend))
        {
            if (node is LocalDeclarationStatementSyntax local)
            {
                yield return local;
            }
        }
    }

    /// <summary>
    /// Do not descend into a nested local function or lambda: its locals are
    /// rooted by ITS own emitted method, not the enclosing one.
    /// </summary>
    private static bool ShouldDescend(SyntaxNode node)
        => node is not (LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax);

    /// <summary>
    /// True iff <paramref name="method"/> takes the implicit instance
    /// <c>self</c> receiver: an instance method / accessor / operator, or an
    /// instance constructor; false for a static method or the static
    /// constructor. Matches <c>MethodEmitter.HasSelfParameter</c>.
    /// </summary>
    private static bool HasSelfReceiver(IMethodSymbol method)
    {
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
    /// True iff a by-value reference of <paramref name="type"/> is rooted in the
    /// shadow stack: <paramref name="refKind"/> is <see cref="RefKind.None"/>
    /// (a by-ref / out / in reference is an alias the caller already roots) AND
    /// the type IS or DERIVES FROM the engine <c>XObject</c>. Matches
    /// <c>MethodEmitter.IsRootableParameter</c>.
    /// </summary>
    private static bool IsRootableReference(RefKind refKind, ITypeSymbol type)
    {
        if (refKind != RefKind.None)
        {
            return false;
        }

        return type is INamedTypeSymbol named
            && (AnalyzerHelpers.IsXObjectType(named) || AnalyzerHelpers.IsXObjectDerived(named));
    }
}
