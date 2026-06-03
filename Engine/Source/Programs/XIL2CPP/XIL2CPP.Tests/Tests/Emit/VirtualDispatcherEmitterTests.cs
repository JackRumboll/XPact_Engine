// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="VirtualDispatcherEmitter"/> (XIL2CPP Phase 6.e, WU-E2):
/// the virtual-call trampoline SHELL -- the <c>noexcept</c> <c>extern "C"</c>
/// <c>_Dispatcher</c>, the safe-point + 6.g GC-hook prologue, the
/// <c>self-&gt;GetClass()</c> FClass resolution, the
/// <c>cls-&gt;VirtualMethods[k...VirtualSlotIndex]</c> indirection, the named
/// compilable <c>constexpr</c> slot-index placeholder (with the
/// <c>// TODO(6.e): resolve slot index</c> marker), and byte-determinism.
/// </summary>
public sealed class VirtualDispatcherEmitterTests
{
    [Fact]
    public void IsVirtual_TrueForVirtualAbstractOverride_FalseForPlainAndStatic()
    {
        const string source = """
            namespace Game
            {
                internal abstract class Base
                {
                    internal virtual int V() => 1;
                    internal abstract int A();
                    internal int Plain() => 2;
                    internal static int Stat() => 3;
                }
                internal sealed class Derived : Base
                {
                    internal override int A() => 4;
                    internal override int V() => 5;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);

        Assert.True(VirtualDispatcherEmitter.IsVirtual(FindMethod(ctx, "Base", "V")));
        Assert.True(VirtualDispatcherEmitter.IsVirtual(FindMethod(ctx, "Base", "A")));
        Assert.True(VirtualDispatcherEmitter.IsVirtual(FindMethod(ctx, "Derived", "A")));
        Assert.False(VirtualDispatcherEmitter.IsVirtual(FindMethod(ctx, "Base", "Plain")));
        Assert.False(VirtualDispatcherEmitter.IsVirtual(FindMethod(ctx, "Base", "Stat")));
    }

    [Fact]
    public void Dispatcher_EmitsNoexceptExternCTrampolineWithFClassResolution()
    {
        const string source = """
            namespace Game
            {
                internal class Animal
                {
                    internal virtual int Speak(int volume) => volume;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Animal", "Speak");

        string cpp = EmitDispatcher(ctx, method);
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;

        // extern "C" <ret> <Symbol>_Dispatcher(<self>, <params>) noexcept {
        Assert.Contains(
            "extern \"C\" int32_t " + record.LinkerSymbol + VirtualDispatcherEmitter.DispatcherSuffix + "(",
            cpp);
        Assert.Contains("::Game::Animal* self", cpp);
        Assert.Contains(") noexcept {", cpp);

        // safepoint + 6.g hook prologue.
        Assert.Contains("// " + MethodEmitter.GcHookComment, cpp);
        Assert.Contains(MethodEmitter.SafepointCheck, cpp);

        // FClass resolution via the XObject GetClass() FakeVTable slot.
        Assert.Contains(
            "const " + VirtualDispatcherEmitter.FClassType + "* "
            + VirtualDispatcherEmitter.ClassLocalName + " = self->"
            + VirtualDispatcherEmitter.GetClassCall + ";",
            cpp);
    }

    [Fact]
    public void Dispatcher_EmitsNamedConstexprSlotIndexPlaceholderWithTodo()
    {
        const string source = """
            namespace Game
            {
                internal class Animal
                {
                    internal virtual int Speak(int volume) => volume;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Animal", "Speak");

        string cpp = EmitDispatcher(ctx, method);

        string slotName = VirtualDispatcherEmitter.SlotIndexConstantName(method);
        Assert.Equal("kAnimal_Speak_VirtualSlotIndex", slotName);

        // Compilable placeholder: constexpr int k... = 0; // TODO(6.e): resolve slot index
        Assert.Contains(
            "constexpr int " + slotName + " = 0; // " + VirtualDispatcherEmitter.SlotIndexTodo,
            cpp);

        // The trampoline indexes the v-table by that slot constant.
        Assert.Contains(
            VirtualDispatcherEmitter.ClassLocalName + "->"
            + VirtualDispatcherEmitter.VirtualMethodsMember + "[" + slotName + "]",
            cpp);
    }

    [Fact]
    public void Dispatcher_ForwardsSelfAndArgsThroughTheSlot()
    {
        const string source = """
            namespace Game
            {
                internal class Animal
                {
                    internal virtual int Speak(int volume) => volume;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Animal", "Speak");

        string cpp = EmitDispatcher(ctx, method);

        // Non-void return is forwarded with `return`; self + the declared
        // parameter are the forwarded call arguments.
        Assert.Contains("return reinterpret_cast<", cpp);
        Assert.Contains(")(self, volume);", cpp);
    }

    [Fact]
    public void Dispatcher_VoidReturn_OmitsReturnKeyword()
    {
        const string source = """
            namespace Game
            {
                internal class Animal
                {
                    internal virtual void Touch() { }
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Animal", "Touch");

        string cpp = EmitDispatcher(ctx, method);

        Assert.Contains("reinterpret_cast<", cpp);
        Assert.DoesNotContain("return reinterpret_cast<", cpp);
        Assert.Contains(")(self);", cpp);
    }

    [Fact]
    public void EmitDispatcher_NonVirtualMethod_Throws()
    {
        const string source = """
            namespace Game
            {
                internal class Animal
                {
                    internal int Plain() => 1;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Animal", "Plain");

        CppWriter writer = new();
        Assert.Throws<System.InvalidOperationException>(
            () => new VirtualDispatcherEmitter().EmitDispatcher(method, ctx, writer));
    }

    [Fact]
    public void EmitDispatcher_IsByteDeterministic()
    {
        const string source = """
            namespace Game
            {
                internal class Animal
                {
                    internal virtual int Speak(int volume) => volume;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Animal", "Speak");

        Assert.Equal(EmitDispatcher(ctx, method), EmitDispatcher(ctx, method));
    }

    // -----------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------

    private static string EmitDispatcher(EmitContext ctx, IMethodSymbol method)
    {
        CppWriter writer = new();
        new VirtualDispatcherEmitter().EmitDispatcher(method, ctx, writer);
        return writer.Build();
    }

    private static IMethodSymbol FindMethod(EmitContext ctx, string typeName, string name)
        => Pass5Driver.EnumerateEmittableFunctions(ctx.Unit)
            .First(m => m.Name == name
                && m.ContainingType.Name == typeName
                && m.MethodKind == MethodKind.Ordinary);
}
