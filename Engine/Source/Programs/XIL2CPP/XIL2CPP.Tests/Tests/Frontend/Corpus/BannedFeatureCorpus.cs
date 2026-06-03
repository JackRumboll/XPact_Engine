// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// The corpus of <em>BANNED</em> / <em>post-MVP</em> C# 12 language features
/// from <c>/Documents/XIL2CPP.html</c> Rev 4 Section 4.1. One fixture per
/// banned / post-MVP row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a scope boundary.</b> Phase 6.a is PARSE + BIND only. For these
/// features the front-end's contract is that it must parse + bind them
/// WITHOUT throwing / crashing, so the later XIL2CPP-diagnostic pass (Pass 3)
/// can walk the bound tree and emit the per-feature rejection (XIL2CPP001 /
/// 040 / 044 / 048 / 061 / ...). This corpus therefore does NOT assert any
/// XIL2CPP diagnostic and does NOT assert a clean bind: it only asserts the
/// front-end survives the input.
/// </para>
/// <para>
/// <b>Roslyn errors are acceptable here.</b> Several of these features are
/// genuinely rejected by the C# language / the bound reference set rather
/// than (only) by XIL2CPP -- e.g. <c>dynamic</c> needs the
/// <c>Microsoft.CSharp</c> runtime binder which the test BCL does not
/// reference, and <c>params Span&lt;T&gt;</c> uses C# 13 syntax under the
/// C# 12 pin. The assertion is "the front-end did not crash", never "the bind
/// is clean". The supported-feature corpus is where clean-bind is asserted.
/// </para>
/// </remarks>
internal static class BannedFeatureCorpus
{
    /// <summary>Every banned / post-MVP Section 4.1 fixture, in matrix order.</summary>
    public static IReadOnlyList<CorpusFeature> Features { get; } = Build();

    private static CorpusFeature B(string name, string source)
        => new(name, source, FeatureStatus.BannedOrPostMvp);

    private static IReadOnlyList<CorpusFeature> Build()
    {
        List<CorpusFeature> f = new();

        // ===== Type / member declarations (post-MVP) =====

        f.Add(B(
            "delegate declarations (post-MVP)",
            "namespace N; public delegate int BinaryOp(int a, int b);"));

        f.Add(B(
            "events (declarative event Action E;) (post-MVP)",
            "namespace N; using System; public class M { public event Action? Tick; public void Fire() => Tick?.Invoke(); }"));

        f.Add(B(
            "static abstract interface members (C# 11) (post-MVP)",
            """
            namespace N;
            using System.Numerics;
            public interface IAddable<T> where T : IAddable<T>
            {
                static abstract T operator +(T a, T b);
                static abstract T Zero { get; }
            }
            """));

        // ===== Method-level (banned / post-MVP) =====

        f.Add(B(
            "ref / out XObject reference parameters (BANNED XIL2CPP005)",
            """
            namespace N;
            public abstract class XObject { }
            public class Actor : XObject { }
            public class M
            {
                public void Take(ref Actor a) { }
                public void Make(out Actor a) { a = null!; }
            }
            """));

        f.Add(B(
            "params ReadOnlySpan<T> (C# 12) (post-MVP XIL2CPP015)",
            """
            namespace N;
            using System;
            public class M
            {
                public int Sum(params ReadOnlySpan<int> xs)
                {
                    int t = 0;
                    foreach (int x in xs) t += x;
                    return t;
                }
            }
            """));

        f.Add(B(
            "params Span<T> (permanently BANNED)",
            """
            namespace N;
            using System;
            public class M
            {
                public void Fill(params Span<int> xs) { }
            }
            """));

        f.Add(B(
            "async / await (BANNED on sim-path XIL2CPP044)",
            """
            namespace N;
            using System.Threading.Tasks;
            public class M
            {
                public async Task<int> Run()
                {
                    await Task.Yield();
                    return 1;
                }
            }
            """));

        f.Add(B(
            "Task<T> / ValueTask<T> / Task.Result / Task.Wait() (BANNED on sim-path XIL2CPP048)",
            """
            namespace N;
            using System.Threading.Tasks;
            public class M
            {
                public int Run(Task<int> t) { t.Wait(); return t.Result; }
                public ValueTask<int> V() => new ValueTask<int>(0);
            }
            """));

        f.Add(B(
            "IAsyncEnumerable<T> / await foreach (BANNED on sim-path)",
            """
            namespace N;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            public class M
            {
                public async Task<int> Run(IAsyncEnumerable<int> src)
                {
                    int t = 0;
                    await foreach (int x in src) t += x;
                    return t;
                }
            }
            """));

        // ===== Statement-level (banned / post-MVP) =====

        f.Add(B(
            "foreach over IEnumerable<T> interface (BANNED on sim-path XIL2CPP064)",
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                public int Run(IEnumerable<int> xs) { int s = 0; foreach (int x in xs) s += x; return s; }
            }
            """));

        f.Add(B(
            "foreach over HashSet<T>/Dictionary<K,V> on sim-path (ERROR XIL2CPP041)",
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                public int Run(HashSet<int> h, Dictionary<string, int> d)
                {
                    int s = 0;
                    foreach (int x in h) s += x;
                    foreach (var kv in d) s += kv.Value;
                    return s;
                }
            }
            """));

        f.Add(B(
            "lock(obj) (BANNED on sim-path)",
            """
            namespace N;
            public class M
            {
                private readonly object _gate = new object();
                public void Run() { lock (_gate) { } }
            }
            """));

        f.Add(B(
            "Type.GetMethods() / MethodInfo.Invoke() (post-MVP + BANNED on sim-path)",
            """
            namespace N;
            using System;
            using System.Reflection;
            public class M
            {
                public object? Run(object o)
                {
                    MethodInfo[] ms = o.GetType().GetMethods();
                    return ms[0].Invoke(o, null);
                }
            }
            """));

        f.Add(B(
            "System.Reflection.Emit (permanently BANNED)",
            """
            namespace N;
            using System;
            using System.Reflection;
            using System.Reflection.Emit;
            public class M
            {
                public void Run()
                {
                    var an = new AssemblyName("Dyn");
                    AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
                    ModuleBuilder mb = ab.DefineDynamicModule("M");
                }
            }
            """));

        // ===== Expressions / BCL surfaces (banned / post-MVP) =====

        f.Add(B(
            "anonymous types (new { ... }) (BANNED on sim-path XIL2CPP061)",
            """
            namespace N;
            public class M
            {
                public object Run() => new { Actor = "hero", Count = 5 };
            }
            """));

        f.Add(B(
            "implicit boxing of value type to object (BANNED XIL2CPP062)",
            "namespace N; public class M { public object Run() { object o = 5; return o; } }"));

        f.Add(B(
            "List<object> storing value types (BANNED XIL2CPP073)",
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                public object Run() { var l = new List<object> { 1, 2, 3 }; return l[0]; }
            }
            """));

        f.Add(B(
            "pattern match on object with value-type pattern (BANNED XIL2CPP060)",
            """
            namespace N;
            public class M
            {
                public string Run(object o) => o switch { int n => "int:" + n, _ => "other" };
            }
            """));

        f.Add(B(
            "[ThreadStatic] static fields (BANNED on sim-path XIL2CPP057)",
            """
            namespace N;
            using System;
            public class M
            {
                [ThreadStatic] private static int _slot;
                public void Set(int v) { _slot = v; }
                public int Get() => _slot;
            }
            """));

        f.Add(B(
            "System.Linq.* (BANNED on sim-path)",
            """
            namespace N;
            using System.Collections.Generic;
            using System.Linq;
            public class M
            {
                public List<int> Run(List<int> xs) => xs.Where(x => x > 0).Select(x => x * 2).ToList();
            }
            """));

        f.Add(B(
            "System.Activator.CreateInstance (BANNED on sim-path XIL2CPP051)",
            """
            namespace N;
            using System;
            public class Foo { }
            public class M
            {
                public object? Run() => Activator.CreateInstance(typeof(Foo));
            }
            """));

        f.Add(B(
            "dynamic keyword (permanently BANNED)",
            "namespace N; public class M { public object Run(dynamic d) { return d.Anything(); } }"));

        f.Add(B(
            "System.Runtime.InteropServices.DllImport (permanently BANNED)",
            """
            namespace N;
            using System.Runtime.InteropServices;
            public static class Native
            {
                [DllImport("kernel32.dll")]
                public static extern uint GetCurrentThreadId();
            }
            """));

        f.Add(B(
            "DateTime.Now / Random.Shared / Environment.TickCount (BANNED on sim-path XIL2CPP040)",
            """
            namespace N;
            using System;
            public class M
            {
                public long Run() => DateTime.Now.Ticks + Random.Shared.Next() + Environment.TickCount;
            }
            """));

        f.Add(B(
            "System.Threading.Thread.* (BANNED on sim-path)",
            """
            namespace N;
            using System.Threading;
            public class M
            {
                public void Run() { Thread.Sleep(0); var t = new Thread(() => { }); t.Start(); }
            }
            """));

        f.Add(B(
            "System.Threading.Interlocked.* (BANNED on sim-path XIL2CPP055)",
            """
            namespace N;
            using System.Threading;
            public class M
            {
                private int _counter;
                public int Run() { Interlocked.Increment(ref _counter); return Interlocked.CompareExchange(ref _counter, 1, 0); }
            }
            """));

        return f;
    }
}
