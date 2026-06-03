// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The category a Pass-3 <see cref="StackAllocSite"/> was classified into per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.13 (Q10 resolution +
/// FIX-B-MEDIUM-35 / FIX-D-MED-05).
/// </summary>
public enum StackAllocCategory
{
    /// <summary>
    /// A compile-time-sized <c>stackalloc T[N]</c> whose static byte size
    /// (<c>N * sizeof(T)</c>) is known and within the
    /// <c>XPACT_MAX_STACKALLOC_BYTES</c> (64&#160;KB) limit. No diagnostic;
    /// emits as a fixed-size stack array (no runtime check).
    /// </summary>
    Ok = 0,

    /// <summary>
    /// A dynamically-sized <c>stackalloc T[n]</c> (the count is not a
    /// compile-time constant). No build-time diagnostic; emit inserts a
    /// runtime size check against <c>XPACT_MAX_STACKALLOC_BYTES</c> before the
    /// <c>alloca</c> (Section 5.13). Recorded so emit knows to insert the
    /// guard.
    /// </summary>
    RuntimeChecked = 1,

    /// <summary>
    /// A compile-time-sized <c>stackalloc T[N]</c> whose static byte size
    /// exceeds the 64&#160;KB limit. Reported as <c>XIL2CPP086</c>.
    /// </summary>
    OversizeError = 2,

    /// <summary>
    /// A <c>stackalloc T[...]</c> whose element type <c>T</c> is managed
    /// (a reference type, or a value type that transitively contains a managed
    /// / XObject reference), which cannot be stack-allocated. Reported as
    /// <c>XIL2CPP081</c>.
    /// </summary>
    ManagedElementError = 3,
}

/// <summary>
/// A recorded <c>stackalloc</c> site discovered by
/// <see cref="StackAllocAnalyzer"/>. Pass-3 emit consumes these to choose the
/// fixed-array vs. <c>alloca</c>-with-runtime-check lowering per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.13.
/// </summary>
/// <param name="Span">The 1-based source span of the <c>stackalloc</c> expression.</param>
/// <param name="ElementType">
/// The fully-qualified display name of the element type <c>T</c>, or
/// <c>"&lt;unresolved&gt;"</c> when it could not be bound.
/// </param>
/// <param name="ElementByteSize">
/// The size in bytes of one element when it is a known unmanaged primitive /
/// enum / pointer type, or null when the element size is not statically known
/// (a non-primitive unmanaged struct, or a managed / unresolved element).
/// </param>
/// <param name="ConstantCount">
/// The compile-time-constant element count, or null when the count is
/// dynamic (not a constant) or absent (an implicit / initializer-sized form
/// whose count is the initializer length).
/// </param>
/// <param name="TotalByteSize">
/// The total static byte size (<c>ConstantCount * ElementByteSize</c>) when
/// both are known, or null otherwise.
/// </param>
/// <param name="Category">The classification of this site.</param>
public sealed record StackAllocSite(
    SourceSpan Span,
    string ElementType,
    int? ElementByteSize,
    long? ConstantCount,
    long? TotalByteSize,
    StackAllocCategory Category);

/// <summary>
/// Pass-3 semantic analyzer for <c>stackalloc</c> expressions per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.13 (Q10 resolution;
/// FIX-B-MEDIUM-35 runtime size check; FIX-D-MED-05 static size check).
/// </summary>
/// <remarks>
/// <para>
/// For each <c>stackalloc</c> expression (both the explicit
/// <c>stackalloc T[n]</c> / <c>stackalloc T[] { ... }</c> form and the
/// implicit <c>stackalloc[] { ... }</c> form) the analyzer classifies the
/// site and records a <see cref="StackAllocSite"/>:
/// <list type="bullet">
///   <item><description>
///     <b>Managed / ref-containing element</b> -- the element type <c>T</c> is
///     not an unmanaged type (a reference type, or a value type that
///     transitively contains a managed / XObject reference): emit
///     <c>XIL2CPP081</c> (<see cref="StackAllocCategory.ManagedElementError"/>).
///     This takes precedence over the size check.
///   </description></item>
///   <item><description>
///     <b>Compile-time-sized, oversize</b> -- the element count is a constant
///     and <c>count * sizeof(T)</c> exceeds the 64&#160;KB limit: emit
///     <c>XIL2CPP086</c> (<see cref="StackAllocCategory.OversizeError"/>).
///   </description></item>
///   <item><description>
///     <b>Compile-time-sized, within bound</b> -- recorded as
///     <see cref="StackAllocCategory.Ok"/>, no diagnostic.
///   </description></item>
///   <item><description>
///     <b>Dynamically-sized</b> -- the count is not a constant: recorded as
///     <see cref="StackAllocCategory.RuntimeChecked"/> (emit inserts the
///     runtime guard), no build-time diagnostic.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Code ownership.</b> This analyzer owns <c>XIL2CPP081</c> and
/// <c>XIL2CPP086</c> only. The <c>Span&lt;T&gt;</c>-over-XObject ban
/// (<c>XIL2CPP080</c>) and the Span-escape rule (<c>XIL2CPP082</c>) belong to
/// other analyzers and are not emitted here.
/// </para>
/// <para>
/// <b>Determinism.</b> Parsed files are visited in their canonical Pass-1
/// order and nodes in document (span) order via
/// <c>SyntaxNode.DescendantNodes</c> (a stable pre-order walk); the
/// element-size table is a fixed lookup. No ambient state, <c>DateTime</c>, or
/// <c>Random</c> (gate X-IL2CPP-CSPATH-DET, Section 9.9). The analyzer never
/// throws on well-formed input; an element type that cannot be bound is
/// recorded with an <c>"&lt;unresolved&gt;"</c> element name and no diagnostic.
/// </para>
/// </remarks>
public sealed class StackAllocAnalyzer : ISemanticAnalyzer
{
    /// <summary>
    /// The default <c>XPACT_MAX_STACKALLOC_BYTES</c> stack-allocation limit
    /// (64&#160;KB) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.13. The
    /// limit is configurable per-module, but the default applies at this pass.
    /// </summary>
    public const long MaxStackAllocBytes = 64L * 1024L;

    private const string UnresolvedElementName = "<unresolved>";

    /// <inheritdoc />
    public string Name => "StackAllocAnalyzer";

    /// <inheritdoc />
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        // Visit parsed files in their canonical Pass-1 order, nodes in
        // document (span) order, for a deterministic walk.
        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxNode root = file.Tree.GetRoot();

            List<ExpressionSyntax> stackAllocs = root
                .DescendantNodes()
                .Where(static n => n is StackAllocArrayCreationExpressionSyntax
                                   or ImplicitStackAllocArrayCreationExpressionSyntax)
                .Cast<ExpressionSyntax>()
                .ToList();
            if (stackAllocs.Count == 0)
            {
                continue;
            }

            SemanticModel model = pass1.GetSemanticModel(file.Tree);

            foreach (ExpressionSyntax stackAlloc in stackAllocs)
            {
                AnalyzeSite(stackAlloc, model, pass1.ModuleName, builder);
            }
        }
    }

    private static void AnalyzeSite(
        ExpressionSyntax stackAlloc,
        SemanticModel model,
        string moduleName,
        Pass3ResultBuilder builder)
    {
        ITypeSymbol? elementType = ResolveElementType(stackAlloc, model);
        string elementName = elementType is null
            ? UnresolvedElementName
            : elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        SourceSpan span = ToSpan(stackAlloc);

        // (1) Managed / ref-containing element -> XIL2CPP081. This takes
        //     precedence over the size check: a managed element cannot be
        //     stack-allocated at all, so the size is moot.
        if (elementType is not null && IsManagedElement(elementType))
        {
            StackAllocSite managedSite = new(
                span,
                elementName,
                ElementByteSize: null,
                ConstantCount: null,
                TotalByteSize: null,
                StackAllocCategory.ManagedElementError);
            builder.Add(managedSite);
            builder.AddDiagnostic(span.ToDiagnostic(
                Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity.Error,
                DiagnosticCodes.StackallocOverXObjectUnsupported,
                $"stackalloc T[n] where T ('{elementName}') is a managed or reference-containing type is not supported; "
                    + "the element must be an unmanaged value type.",
                moduleName));
            return;
        }

        // Element byte size for a known unmanaged primitive / enum / pointer
        // element, or null when the element size is not statically known here.
        int? elementByteSize = elementType is null ? null : TryGetElementByteSize(elementType);

        // (2) Determine the count. A constant count enables the static size
        //     check; a non-constant count is the dynamic (runtime-checked)
        //     case.
        long? constantCount = TryGetConstantCount(stackAlloc, model);

        if (constantCount is null)
        {
            // Dynamically-sized stackalloc: emit inserts a runtime guard.
            // Recorded, no build-time diagnostic.
            builder.Add(new StackAllocSite(
                span,
                elementName,
                elementByteSize,
                ConstantCount: null,
                TotalByteSize: null,
                StackAllocCategory.RuntimeChecked));
            return;
        }

        // (3) Compile-time-sized. When the element size is known, compute the
        //     total and apply the static bound; otherwise record an Ok site
        //     (we cannot prove oversize for an unmanaged struct of unknown
        //     C++ layout size, so we do not false-positive XIL2CPP086).
        long? totalByteSize = elementByteSize is int size
            ? checked(constantCount.Value * size)
            : (long?)null;

        if (totalByteSize is long total && total > MaxStackAllocBytes)
        {
            builder.Add(new StackAllocSite(
                span,
                elementName,
                elementByteSize,
                constantCount,
                totalByteSize,
                StackAllocCategory.OversizeError));
            builder.AddDiagnostic(span.ToDiagnostic(
                Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity.Error,
                DiagnosticCodes.StackallocExceedsLimit,
                $"stackalloc {elementName}[{constantCount.Value}] requires {total} bytes, which exceeds the "
                    + $"{MaxStackAllocBytes}-byte (64 KB) stack-allocation limit at static size; "
                    + "reduce the count or use a heap-allocated, RAII-managed array (TArray<T>).",
                moduleName));
            return;
        }

        // Within bound (or unknown element size): a fixed-size stack array, no
        // runtime check, no diagnostic.
        builder.Add(new StackAllocSite(
            span,
            elementName,
            elementByteSize,
            constantCount,
            totalByteSize,
            StackAllocCategory.Ok));
    }

    /// <summary>
    /// Resolve the element type <c>T</c> of a <c>stackalloc</c> expression.
    /// For the explicit form the element type comes from the array-type
    /// syntax; for both forms we fall back to unwrapping the expression's
    /// resolved <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c> / <c>T*</c>
    /// type so the element is recovered even on the implicit
    /// (<c>stackalloc[] { ... }</c>) form.
    /// </summary>
    private static ITypeSymbol? ResolveElementType(ExpressionSyntax stackAlloc, SemanticModel model)
    {
        // Explicit form: stackalloc T[n] / stackalloc T[] { ... }. The
        // declared element type binds even when CS0208 fires on a managed T
        // (that error is about taking the element's address, not resolving the
        // type name), so this is the authoritative source for XIL2CPP081.
        if (stackAlloc is StackAllocArrayCreationExpressionSyntax explicitForm
            && explicitForm.Type is ArrayTypeSyntax arrayType)
        {
            ITypeSymbol? declared = model.GetTypeInfo(arrayType.ElementType).Type;
            if (declared is not null && declared.TypeKind != TypeKind.Error)
            {
                return declared;
            }
        }

        // Fallback (covers the implicit form and any explicit form whose
        // element did not bind above): unwrap the resolved expression type.
        return UnwrapSpanElement(model.GetTypeInfo(stackAlloc).Type
            ?? model.GetTypeInfo(stackAlloc).ConvertedType);
    }

    /// <summary>
    /// Unwrap the element type from a resolved <c>stackalloc</c> expression
    /// type: <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c> -> <c>T</c>,
    /// or a pointer <c>T*</c> -> <c>T</c>. Returns null otherwise.
    /// </summary>
    private static ITypeSymbol? UnwrapSpanElement(ITypeSymbol? exprType)
    {
        switch (exprType)
        {
            case IPointerTypeSymbol pointer:
                return pointer.PointedAtType;

            case INamedTypeSymbol named when named.TypeArguments.Length == 1:
                string original = named.OriginalDefinition.ToDisplayString();
                if (original is "System.Span<T>" or "System.ReadOnlySpan<T>")
                {
                    return named.TypeArguments[0];
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// True iff the element type is managed: a reference type, or a value type
    /// that is not unmanaged (transitively contains a managed / XObject
    /// reference). <see cref="ITypeSymbol.IsUnmanagedType"/> is Roslyn's
    /// authoritative recursive unmanaged-ness test; the negation is exactly
    /// "managed or ref-containing".
    /// </summary>
    private static bool IsManagedElement(ITypeSymbol elementType)
    {
        // A type parameter's managed-ness is not statically known here (no
        // unmanaged constraint resolution at this pass); treat it as managed
        // is too aggressive, so only flag concrete non-unmanaged types.
        if (elementType.TypeKind == TypeKind.TypeParameter)
        {
            return false;
        }

        return !elementType.IsUnmanagedType;
    }

    /// <summary>
    /// The compile-time-constant element count of a <c>stackalloc</c>, or null
    /// when the count is dynamic (not a constant) or absent (implicit /
    /// initializer-sized forms, whose count is recorded as runtime-checked
    /// here). Only the explicit <c>stackalloc T[n]</c> rank-expression form
    /// carries a statically-checkable count.
    /// </summary>
    private static long? TryGetConstantCount(ExpressionSyntax stackAlloc, SemanticModel model)
    {
        if (stackAlloc is not StackAllocArrayCreationExpressionSyntax explicitForm
            || explicitForm.Type is not ArrayTypeSyntax arrayType
            || arrayType.RankSpecifiers.Count != 1)
        {
            return null;
        }

        ArrayRankSpecifierSyntax rank = arrayType.RankSpecifiers[0];
        if (rank.Sizes.Count != 1)
        {
            return null;
        }

        ExpressionSyntax sizeExpr = rank.Sizes[0];
        if (sizeExpr is OmittedArraySizeExpressionSyntax)
        {
            // stackalloc T[] { ... } -- no explicit size expression; the count
            // is the initializer length. Treated as runtime-checked (not a
            // statically-bounded count) rather than synthesised here.
            return null;
        }

        Optional<object?> constant = model.GetConstantValue(sizeExpr);
        if (!constant.HasValue || constant.Value is null)
        {
            return null;
        }

        try
        {
            long count = Convert.ToInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return count < 0 ? null : count;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The size in bytes of one element when <paramref name="elementType"/> is
    /// a known, platform-independent unmanaged primitive, an enum (sized by
    /// its underlying type), or a pointer (64-bit target), or null when the
    /// element size is not statically known here (a non-primitive unmanaged
    /// struct whose C++ layout size this pass does not compute).
    /// </summary>
    private static int? TryGetElementByteSize(ITypeSymbol elementType)
    {
        if (elementType is IPointerTypeSymbol)
        {
            // x86_64 target (manifest Architecture "x86_64"): 8-byte pointers.
            return 8;
        }

        if (elementType.TypeKind == TypeKind.Enum
            && elementType is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return PrimitiveByteSize(underlying.SpecialType);
        }

        return PrimitiveByteSize(elementType.SpecialType);
    }

    /// <summary>
    /// The byte size of a C# primitive <see cref="SpecialType"/>, or null when
    /// the special type is not a fixed-size primitive whose size is
    /// platform-independent.
    /// </summary>
    private static int? PrimitiveByteSize(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => 1,
        SpecialType.System_Byte => 1,
        SpecialType.System_SByte => 1,
        SpecialType.System_Char => 2,
        SpecialType.System_Int16 => 2,
        SpecialType.System_UInt16 => 2,
        SpecialType.System_Int32 => 4,
        SpecialType.System_UInt32 => 4,
        SpecialType.System_Single => 4,
        SpecialType.System_Int64 => 8,
        SpecialType.System_UInt64 => 8,
        SpecialType.System_Double => 8,
        SpecialType.System_Decimal => 16,
        // nint / nuint / IntPtr / UIntPtr: pointer-width on the x86_64 target.
        SpecialType.System_IntPtr => 8,
        SpecialType.System_UIntPtr => 8,
        _ => null,
    };

    /// <summary>
    /// Build a 1-based <see cref="SourceSpan"/> covering
    /// <paramref name="node"/> from its Roslyn line span.
    /// </summary>
    private static SourceSpan ToSpan(SyntaxNode node)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        Microsoft.CodeAnalysis.Text.LinePosition start = lineSpan.StartLinePosition;
        Microsoft.CodeAnalysis.Text.LinePosition end = lineSpan.EndLinePosition;
        return new SourceSpan(
            lineSpan.Path,
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            validate: true);
    }
}
