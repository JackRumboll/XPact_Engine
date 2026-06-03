// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The per-<c>[XClass]</c> type-level emitter per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.1: for a C# class marked
/// <c>[XPact.CoreXObject.XClass]</c> it emits (into a <see cref="CppWriter"/>)
/// the lifecycle-slot bodies for the overrides the class declares, the
/// <c>Z_ClassConstructor_&lt;Type&gt;</c> placement-new body, the
/// <c>Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;()</c> singleton-getter,
/// the <c>&lt;Type&gt;::StaticClass()</c> body, the
/// <c>constinit const FXObjectLifecycleTable &lt;Type&gt;_LifecycleTable</c>
/// instance, and -- ONLY when XHT does not own the FClass scaffolding for the
/// type -- the <c>constinit const FClass &lt;Type&gt;_Class</c> descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>XHT coordination (FIX-A-HIGH-1, Section 5.1 / 10.4).</b> The emitter
/// reads <see cref="EmitContext.XhtCorrelationTable"/>. When the XHT manifest
/// declares FClass scaffolding for the type
/// (<c>XhtProducesFClass == true</c>), XHT owns the
/// <c>constinit const FClass &lt;Type&gt;_Class</c> descriptor and XIL2CPP
/// MUST NOT re-emit it -- the emitter writes only the singleton-getter +
/// lifecycle bodies + lifecycle-table instance + ClassConstructor +
/// StaticClass. When XHT does NOT (or there is no manifest / the type is not
/// in it), XIL2CPP owns the full set and additionally emits the FClass
/// descriptor instance.
/// </para>
/// <para>
/// <b>Type token (Section 5.1 / cross-tool agreement).</b> The
/// <c>&lt;Type&gt;</c> token in every emitted symbol is the C# type's simple
/// name -- the exact token <c>XhtCorrelationAnalyzer</c> uses when it builds
/// the canonical <c>Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;</c> symbol,
/// so cross-tool symbol matching agrees. (A future C#-to-C++ type-name
/// mapper is a separate concern; using the C# simple name keeps this unit
/// self-contained and the symbol agreement exact.)
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Lifecycle overrides are
/// detected in the ABI-locked slot order (never source order); the qualified
/// metadata name join key is composed deterministically; no ambient state
/// participates.
/// </para>
/// </remarks>
public sealed class ClassEmitter
{
    /// <summary>The metadata name of the reflection-emit class attribute (Section 10.5).</summary>
    private const string XClassAttributeMetadataName = "XClassAttribute";

    /// <summary>The namespace of the XPact reflection attributes (Section 10.5).</summary>
    private const string XPactAttributeNamespace = "XPact.CoreXObject";

    /// <summary>The C++ namespace the reflection runtime types live in.</summary>
    private const string ReflectNamespace = "::XCore::Reflect::";

    private readonly LifecycleTableEmitter _lifecycle = new();
    private readonly ZConstructEmitter _zconstruct = new();

    /// <summary>
    /// True iff <paramref name="symbol"/> is a reflection-emit class, i.e. it
    /// carries the <c>[XPact.CoreXObject.XClass]</c> attribute (matched by
    /// metadata name + namespace, the same recognition
    /// <c>XhtCorrelationAnalyzer</c> uses, so the BCL-ref-absent test path
    /// works).
    /// </summary>
    /// <param name="symbol">The candidate type symbol. May be null (returns false).</param>
    /// <returns>True iff the type is an <c>[XClass]</c>.</returns>
    public static bool IsXClass(INamedTypeSymbol? symbol)
    {
        if (symbol is null)
        {
            return false;
        }
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            INamedTypeSymbol? attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }
            if (string.Equals(attrClass.MetadataName, XClassAttributeMetadataName, StringComparison.Ordinal)
                && attrClass.ContainingNamespace is { IsGlobalNamespace: false } ns
                && string.Equals(ns.ToDisplayString(), XPactAttributeNamespace, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The fully-qualified metadata join key (<c>Namespace.Type</c>) for a
    /// type, matching <c>XhtCorrelationAnalyzer</c>'s
    /// <c>XhtCorrelatedType.CSharpTypeName</c> convention.
    /// </summary>
    private static string QualifiedMetadataName(INamedTypeSymbol symbol)
    {
        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            return ns.ToDisplayString() + "." + symbol.Name;
        }
        return symbol.Name;
    }

    /// <summary>
    /// True iff the XHT manifest (read from
    /// <see cref="EmitContext.XhtCorrelationTable"/>) declares FClass
    /// scaffolding for <paramref name="symbol"/> -- i.e. XHT owns the
    /// <c>constinit const FClass &lt;Type&gt;_Class</c> descriptor and XIL2CPP
    /// must skip it. False when there is no table, the type is absent from it,
    /// or its entry's <c>XhtProducesFClass</c> is false.
    /// </summary>
    /// <param name="symbol">The type symbol. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <returns>True iff XHT owns the FClass descriptor for the type.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="symbol"/> or <paramref name="context"/> is null.</exception>
    public static bool XhtOwnsFClass(INamedTypeSymbol symbol, EmitContext context)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(context);

        XhtCorrelationTable? table = context.XhtCorrelationTable;
        if (table is null)
        {
            return false;
        }

        string key = QualifiedMetadataName(symbol);
        foreach (XhtCorrelatedType correlated in table.CorrelatedTypes)
        {
            if (string.Equals(correlated.CSharpTypeName, key, StringComparison.Ordinal))
            {
                return correlated.XhtProducesFClass;
            }
        }
        return false;
    }

    /// <summary>
    /// Emit the full Section 5.1 type-level body set for an <c>[XClass]</c>
    /// into <paramref name="writer"/>: the declared lifecycle-slot bodies, the
    /// ClassConstructor body, the singleton-getter, the StaticClass body, the
    /// FClass descriptor instance (only when XIL2CPP owns it), and the
    /// lifecycle-table instance. Lifecycle override bodies are lowered through
    /// a fresh <see cref="StatementEmitter"/> built over
    /// <paramref name="registry"/> so the body-lowering rule seam is threaded.
    /// </summary>
    /// <param name="symbol">The <c>[XClass]</c> type symbol. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <param name="registry">The body-lowering rule registry the lifecycle-body statement emitter dispatches through. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public void EmitClass(
        INamedTypeSymbol symbol,
        EmitContext context,
        CppWriter writer,
        BodyLoweringRuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(registry);

        string typeName = symbol.Name;
        string moduleName = context.ModuleName;

        // Detect the declared lifecycle overrides (ABI slot order), pairing
        // each declared slot with the C# override body to lower.
        Dictionary<int, SyntaxNode?> declaredBodies = CollectDeclaredLifecycleBodies(symbol);
        HashSet<int> declaredSlots = new(declaredBodies.Keys);

        StatementEmitter bodyEmitter = new(context, writer, registry);

        // ----- Lifecycle slot bodies (only the declared overrides). -----
        writer.AppendComment("===== Lifecycle slot bodies (Section 5.1; XCoreXObject Section 2.4) =====");
        foreach (LifecycleSlot slot in LifecycleTableEmitter.Slots)
        {
            if (declaredBodies.TryGetValue(slot.Index, out SyntaxNode? overrideBody))
            {
                _lifecycle.EmitSlotBody(slot, typeName, overrideBody, writer, bodyEmitter);
            }
        }

        // ----- ClassConstructor body (placement-new). -----
        EmitClassConstructor(typeName, writer);

        // ----- Singleton-getter + StaticClass. -----
        _zconstruct.EmitSingletonGetter(moduleName, typeName, writer);
        _zconstruct.EmitStaticClass(moduleName, typeName, writer);

        // ----- FClass descriptor instance (only when XIL2CPP owns it). -----
        if (!XhtOwnsFClass(symbol, context))
        {
            _zconstruct.EmitFClassInstance(typeName, writer);
        }

        // ----- Lifecycle-table instance (always XIL2CPP-owned). -----
        _lifecycle.EmitTableInstance(typeName, declaredSlots, writer);
    }

    /// <summary>
    /// Emit the <c>Z_ClassConstructor_&lt;Type&gt;</c> body: the placement-new
    /// into the allocator-provided memory the FClass's <c>ClassConstructorFn</c>
    /// slot invokes (Section 5.1).
    /// </summary>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitClassConstructor(string typeName, CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(writer);

        writer.AppendComment("===== Z_ClassConstructor_" + typeName + " (placement-new) =====");
        writer.AppendComment("Invoked by NewObject<" + typeName + "> via FClass::ClassConstructorFn slot.");
        writer.BeginBlock(
            "extern \"C\" " + ReflectNamespace + "XObject* Z_ClassConstructor_" + typeName + "("
            + ReflectNamespace + "XObject* memory, " + ReflectNamespace + "FXObjectInitializer& init)");
        writer.AppendLine("(void)init;");
        writer.AppendLine("auto* obj = new (memory) " + typeName + "();");
        writer.AppendLine("return obj;");
        writer.EndBlock();
    }

    /// <summary>
    /// Collect the C# lifecycle overrides the type declares, keyed by slot
    /// index. A slot is "declared" when the type declares a method member
    /// whose name equals the slot's lifecycle hook name (e.g.
    /// <c>PostInitProperties</c>). The value is the override's body
    /// (<see cref="BlockSyntax"/> or <see cref="ArrowExpressionClauseSyntax"/>),
    /// or null when the declaration carries no lowerable body. Detection is in
    /// the ABI-locked slot order so the result is deterministic regardless of
    /// source member order.
    /// </summary>
    private static Dictionary<int, SyntaxNode?> CollectDeclaredLifecycleBodies(INamedTypeSymbol symbol)
    {
        // Index the type's declared methods by name (last declaration wins for
        // a given name; lifecycle hooks are not overloaded on signature here).
        Dictionary<string, IMethodSymbol> byName = new(StringComparer.Ordinal);
        foreach (ISymbol member in symbol.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method
                && !method.IsImplicitlyDeclared)
            {
                byName[method.Name] = method;
            }
        }

        Dictionary<int, SyntaxNode?> declared = new();
        foreach (LifecycleSlot slot in LifecycleTableEmitter.Slots)
        {
            if (byName.TryGetValue(slot.Name, out IMethodSymbol? method))
            {
                declared[slot.Index] = FindOverrideBody(method);
            }
        }
        return declared;
    }

    /// <summary>
    /// Resolve the lowerable body node of a declared lifecycle override: the
    /// method declaration's <see cref="BlockSyntax"/> body, or its
    /// <see cref="ArrowExpressionClauseSyntax"/> expression body, or null when
    /// it has neither (abstract / partial-without-impl).
    /// </summary>
    private static SyntaxNode? FindOverrideBody(IMethodSymbol method)
    {
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is MethodDeclarationSyntax decl)
            {
                if (decl.Body is { } block)
                {
                    return block;
                }
                if (decl.ExpressionBody is { } arrow)
                {
                    return arrow;
                }
            }
        }
        return null;
    }
}
