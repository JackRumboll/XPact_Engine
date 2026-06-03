// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for the precise-GC LOCAL rooting added to
/// <see cref="LocalDeclarationLoweringRule"/> (XIL2CPP Phase 6.g, WU-6G-LOCAL),
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.x (precise rooting): an
/// XObject-derived local declared inside a method body that carries a shadow
/// stack is rooted -- it allocates a <c>_liveRefs[idx]</c> slot, writes the live
/// reference into the slot after its declaration, and emits a C++ RAII guard
/// that nulls the slot at C++ scope exit. A non-XObject local roots nothing, and
/// (the regression contract) with NO bound shadow stack the rule emits exactly
/// the pre-6.g declaration line.
/// </summary>
public sealed class LocalDeclarationGcTrackingTests
{
    // A locally-declared stand-in for the engine root reference type; the
    // metadata-name + namespace fallback in AnalyzerHelpers.IsXObjectDerived
    // recognises it even though the real curated XObject BCL ref is absent.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    // =================================================================
    // Direct StatementEmitter path: a bound shadow stack, the local rule
    // plus the expression rules so initializers lower to real C++.
    // =================================================================

    [Fact]
    public void XObjectLocal_RootsSlot_WritesSlot_AndEmitsRaiiGuard()
    {
        // A method body holding an XObject-derived local (no initializer keeps
        // the lowered declaration line minimal so the rooting fragments are the
        // focus of the assertions).
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public void Run() { Actor a; }
                }
            }");

        MethodShadowStackBuilder shadowStack = new();
        string cpp = LowerWithShadowStack(ctx, shadowStack);

        // The local rooted exactly one slot (index 0 -- the only allocation in
        // this builder, since the prologue self/param allocation is the
        // MethodEmitter's job, not this rule's).
        Assert.Equal(1, shadowStack.Count);

        // The slot write of the live reference, using MethodShadowStackBuilder's
        // canonical reinterpret_cast<XObject*> form.
        Assert.Contains(
            MethodShadowStackBuilder.ArrayName + "[0] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(a));",
            cpp);

        // The RAII scope-clear guard: a unique-per-index struct holding a
        // reference to the slot, whose destructor nulls it at C++ scope exit.
        Assert.Contains(
            "struct " + LocalDeclarationLoweringRule.GuardTypePrefix + "0 { "
            + MethodShadowStackBuilder.SlotElementType + "& _s; "
            + "~" + LocalDeclarationLoweringRule.GuardTypePrefix + "0() noexcept { _s = nullptr; } } "
            + LocalDeclarationLoweringRule.GuardInstancePrefix + "0{ "
            + MethodShadowStackBuilder.ArrayName + "[0] };",
            cpp);
    }

    [Fact]
    public void NonXObjectLocal_RootsNothing_NoSlotWrite_NoGuard()
    {
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Holder : XObject {
                    public void Run() { int n = 5; }
                }
            }");

        MethodShadowStackBuilder shadowStack = new();
        string cpp = LowerWithShadowStack(ctx, shadowStack);

        // A value-typed local carries no GC obligation: no slot, no write,
        // no guard.
        Assert.Equal(0, shadowStack.Count);
        Assert.DoesNotContain(MethodShadowStackBuilder.ArrayName, cpp);
        Assert.DoesNotContain(LocalDeclarationLoweringRule.GuardTypePrefix, cpp);
        // The plain declaration line is still emitted.
        Assert.Contains("int32_t n = ", cpp);
    }

    [Fact]
    public void MultipleXObjectDeclarators_RootEachIndependently()
    {
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public void Run() { Actor a, b; }
                }
            }");

        MethodShadowStackBuilder shadowStack = new();
        string cpp = LowerWithShadowStack(ctx, shadowStack);

        // Two declarators -> two slots, two writes, two guards, in source order.
        Assert.Equal(2, shadowStack.Count);
        Assert.Contains(MethodShadowStackBuilder.ArrayName + "[0] = ", cpp);
        Assert.Contains("(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(a));", cpp);
        Assert.Contains(MethodShadowStackBuilder.ArrayName + "[1] = ", cpp);
        Assert.Contains("(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(b));", cpp);
        Assert.Contains(LocalDeclarationLoweringRule.GuardInstancePrefix + "0{ ", cpp);
        Assert.Contains(LocalDeclarationLoweringRule.GuardInstancePrefix + "1{ ", cpp);
    }

    [Fact]
    public void NoBoundShadowStack_EmitsOnlyTheDeclarationLine_Unchanged()
    {
        // The regression contract: with NO bound shadow stack (the rule lowered
        // outside a method-body emit) the output is exactly the pre-6.g
        // declaration line -- no slot write, no guard.
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public void Run() { Actor a; }
                }
            }");

        // ShadowStackBuilder left null (the StatementEmitter default).
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, LocalOnlyRegistry());
        emitter.EmitStatement(FindFirstLocal(ctx));
        string cpp = writer.Build();

        Assert.DoesNotContain(MethodShadowStackBuilder.ArrayName, cpp);
        Assert.DoesNotContain(LocalDeclarationLoweringRule.GuardTypePrefix, cpp);
        // Exactly the declaration line, with NO shadow-stack rooting (no bound
        // builder). FIX 4: an explicit-typed XObject-DERIVED (reference) local is
        // now spelled as a POINTER over its `::`-qualified C++ name
        // (`::M::Actor*`), matching how the method / member emitters spell
        // reference params / returns / fields, so the declared type agrees with
        // its pointer-typed initializer / usage. (Previously the bare value name
        // `Actor` was a documented type-lowering gap.)
        Assert.Equal("::M::Actor* a;\n", cpp);
    }

    [Fact]
    public void Rooting_IsByteDeterministic_OnRepeat()
    {
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public void Run() { Actor a; }
                }
            }");

        string first = LowerWithShadowStack(ctx, new MethodShadowStackBuilder());
        string second = LowerWithShadowStack(ctx, new MethodShadowStackBuilder());
        Assert.Equal(first, second);
    }

    // =================================================================
    // End-to-end through MethodEmitter: an XObject local inside a real
    // method body is rooted as part of the method's shadow stack.
    // =================================================================

    [Fact]
    public void MethodEmitter_XObjectLocalInBody_RootsLocalSlotAndGuard()
    {
        // An instance method on an XObject-derived type holding an XObject local.
        // MethodEmitter binds the shadow stack and pre-roots self at slot 0; the
        // local rule then roots the local at the next slot.
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace Game {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Widget : XObject {
                    public void Build() { Actor a; }
                }
            }");

        IMethodSymbol method = FindMethod(ctx, "Build");
        string cpp = EmitMethod(ctx, method);

        // self at slot 0, the XObject local at slot 1 (the array is sized N=2).
        Assert.Contains(
            MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName + "[2] = {};",
            cpp);
        Assert.Contains(
            MethodShadowStackBuilder.ArrayName + "[0] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(self));",
            cpp);
        Assert.Contains(
            MethodShadowStackBuilder.ArrayName + "[1] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(a));",
            cpp);
        // The local's RAII guard (keyed by the local's slot index, 1).
        Assert.Contains(
            LocalDeclarationLoweringRule.GuardInstancePrefix + "1{ "
            + MethodShadowStackBuilder.ArrayName + "[1] };",
            cpp);
    }

    [Fact]
    public void MethodEmitter_StaticMethodWithOnlyValueLocal_RootsNothing()
    {
        // A static method whose only local is value-typed roots no references:
        // the shadow-stack apparatus is elided entirely (MethodEmitter elides it
        // when N == 0), and this rule adds no slot / guard.
        EmitContext ctx = BuildContext(
            XObjectStub,
            @"namespace Game {
                public class Util {
                    public static void Step() { int n = 1; }
                }
            }");

        IMethodSymbol method = FindMethod(ctx, "Step");
        string cpp = EmitMethod(ctx, method);

        Assert.DoesNotContain(MethodShadowStackBuilder.ArrayName, cpp);
        Assert.DoesNotContain(LocalDeclarationLoweringRule.GuardTypePrefix, cpp);
    }

    // =================================================================
    // Helpers.
    // =================================================================

    private static EmitContext BuildContext(params string[] sources)
        => EmitTestHelpers.BuildEmitContext(sources);

    /// <summary>
    /// Lower the first local declaration in the context's source through a
    /// StatementEmitter with <paramref name="shadowStack"/> bound (mirroring how
    /// MethodEmitter binds it during method-body lowering), over a registry
    /// containing the local rule plus the expression rules.
    /// </summary>
    private static string LowerWithShadowStack(EmitContext ctx, MethodShadowStackBuilder shadowStack)
    {
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, LocalOnlyRegistry())
        {
            ShadowStackBuilder = shadowStack,
        };
        emitter.EmitStatement(FindFirstLocal(ctx));
        return writer.Build();
    }

    /// <summary>
    /// A registry with the local-declaration rule plus the identifier /
    /// object-creation rules so any local initializer lowers to real C++ (the
    /// no-initializer locals these tests use never reach them, but a future
    /// initializer-bearing local would).
    /// </summary>
    private static BodyLoweringRuleRegistry LocalOnlyRegistry()
        => new(new IBodyLoweringRule[]
        {
            new LocalDeclarationLoweringRule(),
            new IdentifierLoweringRule(),
        });

    private static LocalDeclarationStatementSyntax FindFirstLocal(EmitContext ctx)
    {
        foreach (ModuleParser.ParsedFile parsed in ctx.Unit.Pass1.ParsedFiles)
        {
            LocalDeclarationStatementSyntax? decl = parsed.Tree.GetRoot()
                .DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault();
            if (decl is not null)
            {
                return decl;
            }
        }

        throw new Xunit.Sdk.XunitException("No local declaration found in the test sources.");
    }

    private static string EmitMethod(EmitContext ctx, IMethodSymbol method)
    {
        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = BodyLoweringRuleRegistry.Discover();
        StatementEmitter bodyEmitter = new(ctx, writer, registry);
        new MethodEmitter().EmitMethod(method, ctx, writer, bodyEmitter);
        return writer.Build();
    }

    private static IMethodSymbol FindMethod(EmitContext ctx, string name)
        => Pass5Driver.EnumerateEmittableFunctions(ctx.Unit)
            .First(m => m.Name == name && m.MethodKind == MethodKind.Ordinary);
}
