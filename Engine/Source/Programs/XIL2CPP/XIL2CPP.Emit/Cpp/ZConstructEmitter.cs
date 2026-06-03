// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// Emits the per-<c>[XClass]</c> FClass singleton-getter
/// (<c>Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;</c>) body, the
/// <c>StaticClass()</c> method body, and the optional
/// <c>constinit const FClass &lt;Type&gt;_Class</c> instance, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.1 (the XClass emit split,
/// FIX-A-HIGH-1). XIL2CPP owns the singleton-getter body + the
/// <c>StaticClass()</c> body unconditionally; it owns the FClass
/// <c>constinit</c> instance ONLY when XHT does NOT (the cross-tool ownership
/// split read from <see cref="EmitContext.XhtCorrelationTable"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Symbol form (Section 5.1 authoritative).</b> The singleton-getter is
/// module-prefixed: <c>Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;</c> (the
/// doc's <c>Z_Construct_FClass_MyModule_XHealthPickup</c> example). The
/// FClass static-descriptor instance + the lifecycle-table instance keep the
/// bare <c>&lt;Type&gt;_Class</c> / <c>&lt;Type&gt;_LifecycleTable</c> form
/// (no module prefix) per the doc, because they are the
/// referenced-by-extern symbols XHT and XIL2CPP agree on by simple-name.
/// </para>
/// <para>
/// <b>XHT ownership.</b> When the XHT manifest declares FClass scaffolding
/// for the type (<c>XhtProducesFClass == true</c>), XHT emits the
/// <c>constinit const FClass &lt;Type&gt;_Class</c> descriptor; XIL2CPP MUST
/// NOT duplicate it (<see cref="EmitFClassInstance"/> is skipped by the
/// driver). When XHT does not (or there is no manifest), XIL2CPP owns the
/// full set and emits the descriptor instance too.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> No ambient state; every
/// token is composed from the C# simple name + the module name + fixed
/// literals through the deterministic <see cref="CppWriter"/>.
/// </para>
/// </remarks>
public sealed class ZConstructEmitter
{
    /// <summary>The C++ namespace the reflection runtime types live in.</summary>
    private const string ReflectNamespace = "::XCore::Reflect::";

    /// <summary>The canonical module-prefixed FClass singleton-getter symbol prefix (Section 5.1 / 10.4).</summary>
    private const string FClassGetterPrefix = "Z_Construct_FClass_";

    /// <summary>
    /// Build the module-prefixed FClass singleton-getter symbol for a type:
    /// <c>Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;</c> per Section 5.1
    /// (authoritative) / Section 10.4. This is the symbol both XIL2CPP and XHT
    /// agree on; the matching <c>XhtCorrelationAnalyzer</c> builds the same
    /// form so cross-tool symbol matching agrees.
    /// </summary>
    /// <param name="moduleName">The emitting module name. Must not be null / empty / whitespace.</param>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <returns>The module-prefixed singleton-getter symbol.</returns>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> or <paramref name="typeName"/> is null / empty / whitespace.</exception>
    public static string ConstructFClassSymbol(string moduleName, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return FClassGetterPrefix + moduleName + "_" + typeName;
    }

    /// <summary>
    /// The bare (non-module-prefixed) FClass static-descriptor instance symbol
    /// for a type: <c>&lt;Type&gt;_Class</c> per Section 5.1 (the
    /// referenced-by-extern symbol XHT and XIL2CPP agree on by simple-name).
    /// </summary>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <returns>The FClass static-descriptor instance symbol.</returns>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    public static string FClassInstanceSymbol(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return typeName + "_Class";
    }

    /// <summary>
    /// Emit the <c>Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;()</c>
    /// singleton-getter body: a cached, first-call-initialized static that
    /// runs a safe-point check, takes the address of the
    /// <c>&lt;Type&gt;_Class</c> descriptor, registers it with the reflection
    /// runtime, and returns the cached pointer (Section 5.1 body shape).
    /// </summary>
    /// <param name="moduleName">The emitting module name. Must not be null / empty / whitespace.</param>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> or <paramref name="typeName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitSingletonGetter(string moduleName, string typeName, CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(writer);

        string symbol = ConstructFClassSymbol(moduleName, typeName);
        string instance = FClassInstanceSymbol(typeName);

        writer.AppendComment("===== " + symbol + " =====");
        writer.AppendComment("XHT emits the matching extern \"C\" declaration; XIL2CPP emits the body.");
        writer.BeginBlock(
            "extern \"C\" const " + ReflectNamespace + "FClass* " + symbol + "() noexcept");
        writer.BeginBlock("static const " + ReflectNamespace + "FClass* sCachedClass = []() noexcept");
        writer.AppendComment("Safe-point boundary so class registration cannot race the GC mark phase.");
        writer.AppendLine("XPACT_SAFEPOINT_CHECK();");
        writer.AppendLine("const auto* cls = &" + instance + ";");
        writer.AppendLine(ReflectNamespace + "XReflectionRuntime::RegisterClass(cls);");
        writer.AppendLine("return cls;");
        writer.EndBlock("();");
        writer.AppendLine("return sCachedClass;");
        writer.EndBlock();
    }

    /// <summary>
    /// Emit the <c>&lt;Type&gt;::StaticClass()</c> method body: a single
    /// <c>return Z_Construct_FClass_&lt;Module&gt;_&lt;Type&gt;();</c>
    /// (Section 5.1). XIL2CPP always owns this body (XHT declares it via the
    /// <c>&lt;Type&gt;.gen.h</c> extern; XIL2CPP defines it).
    /// </summary>
    /// <param name="moduleName">The emitting module name. Must not be null / empty / whitespace.</param>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> or <paramref name="typeName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitStaticClass(string moduleName, string typeName, CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(writer);

        string symbol = ConstructFClassSymbol(moduleName, typeName);

        writer.AppendComment("===== StaticClass() method body =====");
        writer.BeginBlock("const " + ReflectNamespace + "FClass* " + typeName + "::StaticClass()");
        writer.AppendLine("return " + symbol + "();");
        writer.EndBlock();
    }

    /// <summary>
    /// Emit the <c>constinit const FClass &lt;Type&gt;_Class</c> static
    /// descriptor instance. Emitted ONLY when XHT does NOT own the FClass
    /// scaffolding for the type (the driver gates this on
    /// <see cref="EmitContext.XhtCorrelationTable"/>); when XHT owns it this
    /// method is not called (XHT emits the descriptor). The descriptor wires
    /// its <c>LifecycleTable</c> field to <c>&amp;&lt;Type&gt;_LifecycleTable</c>
    /// (the XIL2CPP-emitted table instance) per Section 5.1.
    /// </summary>
    /// <param name="typeName">The C# type's simple name. Must not be null / empty / whitespace.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="typeName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitFClassInstance(string typeName, CppWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(writer);

        string instance = FClassInstanceSymbol(typeName);
        string lifecycleTable = LifecycleTableEmitter.LifecycleTableSymbol(typeName);

        writer.AppendComment("===== " + instance + " (FClass static descriptor) =====");
        writer.AppendComment("XIL2CPP owns this descriptor: the XHT .gen.manifest declares no FClass");
        writer.AppendComment("scaffolding for this type, so XIL2CPP emits the full set per Section 5.1.");
        writer.BeginBlock("constinit const " + ReflectNamespace + "FClass " + instance + " =");
        writer.AppendComment("LifecycleTable wires to the XIL2CPP-emitted table instance.");
        writer.AppendLine(".LifecycleTable = &" + lifecycleTable + ",");
        writer.EndBlock(";");
    }
}
