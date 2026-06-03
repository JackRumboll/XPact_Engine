// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// The exhaustive corpus of <em>Supported</em> C# 12 language features from
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 4.1 ("The coverage matrix").
/// One fixture per Supported row, authored as a self-contained C# 12
/// compilation unit that parses + binds cleanly against the pinned .NET 8 BCL
/// reference set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a scope boundary.</b> This corpus proves the Pass-1 front-end
/// PARSES and BINDS every Supported feature with zero unexpected Roslyn
/// errors. It does NOT assert any C++ emit or any XIL2CPP-specific
/// diagnostic; emit and the XIL2CPP-diagnostic pass are later sub-phases.
/// </para>
/// <para>
/// <b>XObject rows.</b> Section 4.1's <c>class (XObject-derived)</c> and
/// <c>XObject.New&lt;T&gt;</c> rows reference the <c>XObject</c> base type and
/// the explicit factory, both of which live in the <c>XPact.CSharp.BCL</c>
/// reference assembly that Pass-1 product code consumes but that the tests
/// do not reference (the tests bind against the plain .NET 8 BCL via
/// <c>Basic.Reference.Assemblies.Net80</c>). For Pass-1 parse + bind those
/// rows are syntactically identical to the <c>class (plain reference type)</c>
/// row -- a class declaration with a base type and a static generic factory
/// method -- so they are exercised by a local stand-in <c>XObject</c> base +
/// <c>New&lt;T&gt;</c> factory declared in the same fixture. This keeps the
/// corpus self-contained without leaking the BCL package into the assertion.
/// The XObject-specific lowering (NewObject / XPACT_GC_STORE) and the
/// <c>new XObject()</c> ban (XIL2CPP001) are emit / Pass-3 concerns.
/// </para>
/// </remarks>
internal static class SupportedFeatureCorpus
{
    /// <summary>
    /// Every Supported Section 4.1 fixture, in matrix order (type
    /// declarations, then method-level, statement-level, expressions,
    /// generics, BCL surfaces, nullability).
    /// </summary>
    public static IReadOnlyList<CorpusFeature> Features { get; } = Build();

    private static CorpusFeature S(string name, string source)
        => new(name, source, FeatureStatus.Supported);

    private static IReadOnlyList<CorpusFeature> Build()
    {
        List<CorpusFeature> f = new();

        // ===== Type declarations =====

        f.Add(S(
            "class (XObject-derived)",
            """
            namespace N;
            // Local XObject stand-in: Pass-1 binds an XObject-derived class
            // exactly as any class with a base type. The real XObject lives in
            // XPact.CSharp.BCL (not referenced by tests).
            public abstract class XObject { protected XObject() { } }
            public class Pickup : XObject { public int Health = 25; }
            """));

        f.Add(S(
            "class (plain reference type, not XObject-derived)",
            "namespace N; public class Plain { public int Value; public Plain(int v) { Value = v; } }"));

        f.Add(S(
            "struct (value type)",
            "namespace N; public struct Pair { public int A; public int B; }"));

        f.Add(S(
            "record class (positional)",
            "namespace N; public record Point(int X, int Y);"));

        f.Add(S(
            "record class (property-init)",
            "namespace N; public record Person { public string Name { get; init; } = \"\"; public int Age { get; init; } }"));

        f.Add(S(
            "record struct",
            "namespace N; public record struct Vec(double X, double Y);"));

        f.Add(S(
            "interface (with attribute)",
            "namespace N; public interface IShape { double Area(); }"));

        f.Add(S(
            "enum (scoped)",
            "namespace N; public enum Color { Red, Green = 5, Blue }"));

        f.Add(S(
            "partial class/struct/interface",
            """
            namespace N;
            public partial class Widget { public int A() => 1; }
            public partial class Widget { public int B() => 2; }
            public partial struct PV { public int X; }
            public partial struct PV { public int Y; }
            """));

        f.Add(S(
            "nested types",
            "namespace N; public class Outer { public class Inner { public int V; } public struct InnerS { public int W; } }"));

        f.Add(S(
            "static classes",
            "namespace N; public static class Util { public static int Twice(int x) => x * 2; }"));

        f.Add(S(
            "abstract classes",
            "namespace N; public abstract class Shape { public abstract double Area(); }"));

        f.Add(S(
            "sealed classes",
            "namespace N; public sealed class Final { public int V; }"));

        f.Add(S(
            "required members",
            "namespace N; public class Cfg { public required int Port { get; init; } public required string Host { get; set; } }"));

        f.Add(S(
            "primary constructors (class)",
            "namespace N; public class Greeter(string name) { public string Hello() => \"hi \" + name; }"));

        f.Add(S(
            "primary constructors (record)",
            "namespace N; public record Coord(int X, int Y) { public int Sum => X + Y; }"));

        f.Add(S(
            "init-only setters",
            "namespace N; public class Cfg2 { public int Port { get; init; } }"));

        f.Add(S(
            "auto-properties",
            "namespace N; public class Bag { public int Count { get; set; } }"));

        f.Add(S(
            "computed properties (get-only)",
            "namespace N; public class Circle { public double R { get; set; } public double Area => 3.14159 * R * R; }"));

        f.Add(S(
            "indexers (single-index)",
            "namespace N; public class Row { private readonly int[] _d = new int[4]; public int this[int i] { get => _d[i]; set => _d[i] = value; } }"));

        f.Add(S(
            "indexers (multi-index)",
            "namespace N; public class Grid { private readonly int[,] _d = new int[2, 2]; public int this[int a, int b] { get => _d[a, b]; set => _d[a, b] = value; } }"));

        f.Add(S(
            "operator overloads",
            """
            namespace N;
            public readonly struct Money
            {
                public readonly int Cents;
                public Money(int c) { Cents = c; }
                public static Money operator +(Money a, Money b) => new Money(a.Cents + b.Cents);
                public static bool operator ==(Money a, Money b) => a.Cents == b.Cents;
                public static bool operator !=(Money a, Money b) => a.Cents != b.Cents;
                public override bool Equals(object? o) => o is Money m && m.Cents == Cents;
                public override int GetHashCode() => Cents;
            }
            """));

        f.Add(S(
            "user-defined conversions (implicit / explicit)",
            """
            namespace N;
            public readonly struct Celsius
            {
                public readonly double Value;
                public Celsius(double v) { Value = v; }
                public static implicit operator double(Celsius c) => c.Value;
                public static explicit operator Celsius(double d) => new Celsius(d);
            }
            """));

        // ===== Method-level constructs =====

        f.Add(S(
            "instance methods",
            "namespace N; public class M { public int Add(int a, int b) { return a + b; } }"));

        f.Add(S(
            "static methods",
            "namespace N; public class M { public static int Id(int x) { return x; } }"));

        f.Add(S(
            "virtual methods",
            "namespace N; public class Base { public virtual int V() => 1; }"));

        f.Add(S(
            "abstract methods",
            "namespace N; public abstract class Base { public abstract int V(); }"));

        f.Add(S(
            "override methods",
            "namespace N; public class Base { public virtual int V() => 1; } public class Derived : Base { public override int V() => 2; }"));

        f.Add(S(
            "sealed override methods",
            """
            namespace N;
            public class Base { public virtual int V() => 1; }
            public class Mid : Base { public sealed override int V() => 2; }
            """));

        f.Add(S(
            "local functions",
            """
            namespace N;
            public class M
            {
                public int Sum(int a, int b)
                {
                    int Add() => a + b;
                    return Add();
                }
            }
            """));

        f.Add(S(
            "static local functions",
            """
            namespace N;
            public class M
            {
                public int Run(int x)
                {
                    static int Square(int v) => v * v;
                    return Square(x);
                }
            }
            """));

        f.Add(S(
            "lambdas (non-capturing)",
            """
            namespace N;
            using System;
            public class M
            {
                public int Run(int x)
                {
                    Func<int, int> sq = v => v * v;
                    return sq(x);
                }
            }
            """));

        f.Add(S(
            "lambdas (capturing local value-typed variables)",
            """
            namespace N;
            using System;
            public class M
            {
                public int Run(int x)
                {
                    int bias = 10;
                    Func<int, int> add = v => v + bias;
                    return add(x);
                }
            }
            """));

        f.Add(S(
            "lambdas (capturing reference / XObject)",
            """
            namespace N;
            using System;
            public class Node { public int Value; }
            public class M
            {
                public int Run(Node n)
                {
                    Func<int> read = () => n.Value;
                    return read();
                }
            }
            """));

        f.Add(S(
            "local functions (capturing)",
            """
            namespace N;
            public class M
            {
                public int Run(int seed)
                {
                    int Step() => seed + 1;
                    return Step();
                }
            }
            """));

        f.Add(S(
            "extension methods (this T self)",
            """
            namespace N;
            public static class IntExt
            {
                public static int Doubled(this int x) => x * 2;
            }
            public class Use { public int R() => 21.Doubled(); }
            """));

        f.Add(S(
            "explicit interface implementation",
            """
            namespace N;
            public interface IFoo { void Bar(); }
            public class C : IFoo { void IFoo.Bar() { } }
            """));

        f.Add(S(
            "method hiding (new modifier)",
            """
            namespace N;
            public class Base { public int V() => 1; }
            public class Derived : Base { public new int V() => 2; }
            """));

        f.Add(S(
            "ref / out parameters (value types and non-XObject reference types)",
            """
            namespace N;
            public class M
            {
                public void Swap(ref int a, ref int b) { (a, b) = (b, a); }
                public bool TryGet(out int v) { v = 42; return true; }
            }
            """));

        f.Add(S(
            "ref readonly parameters (C# 12)",
            """
            namespace N;
            public class M
            {
                public int Read(ref readonly int v) => v;
            }
            """));

        f.Add(S(
            "params T[] arrays",
            """
            namespace N;
            public class M
            {
                public int Sum(params int[] xs)
                {
                    int total = 0;
                    foreach (int x in xs) total += x;
                    return total;
                }
            }
            """));

        f.Add(S(
            "yield return iterators",
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                public IEnumerable<int> Count(int n)
                {
                    for (int i = 0; i < n; i++) yield return i;
                }
            }
            """));

        // ===== Statement-level constructs =====

        f.Add(S(
            "local variable declarations",
            "namespace N; public class M { public int Run() { int a = 1; int b = 2; return a + b; } }"));

        f.Add(S(
            "var inference",
            "namespace N; public class M { public int Run() { var a = 1; var b = a + 1; return b; } }"));

        f.Add(S(
            "if / else",
            "namespace N; public class M { public int Run(int x) { if (x > 0) return 1; else return -1; } }"));

        f.Add(S(
            "switch statement",
            """
            namespace N;
            public class M
            {
                public string Run(int x)
                {
                    switch (x)
                    {
                        case 0: return "zero";
                        case 1: return "one";
                        default: return "many";
                    }
                }
            }
            """));

        f.Add(S(
            "switch expression",
            """
            namespace N;
            public class M
            {
                public string Run(int x) => x switch { 0 => "zero", 1 => "one", _ => "many" };
            }
            """));

        f.Add(S(
            "pattern matching (is, positional, property, list, type, relational, logical, when)",
            """
            namespace N;
            public record Pt(int X, int Y);
            public class Animal { }
            public class Dog : Animal { public bool Good; }
            public class M
            {
                public string Classify(object o, int[] xs, Pt p) => o switch
                {
                    int n and > 0 and < 10 => "small-int",                 // type + relational + logical
                    int n when n >= 10 => "big-int",                        // when guard
                    Dog { Good: true } => "good-dog",                       // type + property
                    string s => "str:" + s,                                 // type
                    _ when xs is [1, 2, .. var rest] => "list:" + rest.Length, // list pattern
                    _ when p is (0, 0) => "origin",                         // positional pattern
                    _ => "other",
                };
                public bool IsZero(int v) => v is 0;                         // constant 'is' pattern
            }
            """));

        f.Add(S(
            "for",
            "namespace N; public class M { public int Run(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; } }"));

        f.Add(S(
            "foreach over typed container (List<T>)",
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                public int Run(List<int> xs) { int s = 0; foreach (int x in xs) s += x; return s; }
            }
            """));

        f.Add(S(
            "while / do-while",
            """
            namespace N;
            public class M
            {
                public int While(int n) { int s = 0; while (s < n) s++; return s; }
                public int Do(int n) { int s = 0; do { s++; } while (s < n); return s; }
            }
            """));

        f.Add(S(
            "break / continue",
            """
            namespace N;
            public class M
            {
                public int Run(int n)
                {
                    int s = 0;
                    for (int i = 0; i < n; i++) { if (i == 3) continue; if (i == 7) break; s += i; }
                    return s;
                }
            }
            """));

        f.Add(S(
            "return",
            "namespace N; public class M { public void V() { return; } public int I() { return 5; } }"));

        f.Add(S(
            "goto (and goto case / goto default)",
            """
            namespace N;
            public class M
            {
                public int Jump(int x)
                {
                    if (x < 0) goto done;
                    x = -x;
                    done:
                    return x;
                }
                public string Cased(int x)
                {
                    switch (x)
                    {
                        case 0: goto case 1;
                        case 1: return "low";
                        default: goto end;
                        end: return "end";
                    }
                }
            }
            """));

        f.Add(S(
            "throw",
            "namespace N; using System; public class M { public int Run(bool b) { if (b) throw new InvalidOperationException(); return 0; } }"));

        f.Add(S(
            "try / catch / finally",
            """
            namespace N;
            using System;
            public class M
            {
                public int Run()
                {
                    int r = 0;
                    try { r = 1; }
                    catch (InvalidOperationException) { r = 2; }
                    finally { r += 10; }
                    return r;
                }
            }
            """));

        f.Add(S(
            "throw expression (within ternary)",
            "namespace N; using System; public class M { public int Run(int? x) => x ?? throw new ArgumentNullException(nameof(x)); }"));

        f.Add(S(
            "checked / unchecked blocks",
            """
            namespace N;
            public class M
            {
                public int Ck(int a, int b) { checked { return a + b; } }
                public int Un(int a, int b) { unchecked { return a + b; } }
            }
            """));

        f.Add(S(
            "using block (IDisposable)",
            """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable d) { using (d) { } }
            }
            """));

        f.Add(S(
            "using declaration (using var x = ...)",
            """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable d) { using var x = d; }
            }
            """));

        f.Add(S(
            "fixed statement",
            """
            namespace N;
            public class M
            {
                public unsafe int First(int[] xs) { fixed (int* p = xs) { return *p; } }
            }
            """));

        f.Add(S(
            "unsafe blocks",
            """
            namespace N;
            public class M
            {
                public unsafe int Run() { int v = 5; int* p = &v; return *p; }
            }
            """));

        f.Add(S(
            "pointer arithmetic",
            """
            namespace N;
            public class M
            {
                public unsafe int Second(int[] xs) { fixed (int* p = xs) { return *(p + 1); } }
            }
            """));

        f.Add(S(
            "stackalloc",
            """
            namespace N;
            using System;
            public class M
            {
                public int Run() { Span<int> s = stackalloc int[4]; s[0] = 7; return s[0]; }
            }
            """));

        f.Add(S(
            "Span<T> / ReadOnlySpan<T>",
            """
            namespace N;
            using System;
            public class M
            {
                public int Run(int[] xs)
                {
                    Span<int> s = xs;
                    ReadOnlySpan<int> r = xs;
                    return s.Length + r.Length;
                }
            }
            """));

        f.Add(S(
            "nameof(X)",
            "namespace N; public class M { public string Run() => nameof(M); }"));

        f.Add(S(
            "typeof(T)",
            "namespace N; using System; public class M { public Type Run() => typeof(int); }"));

        f.Add(S(
            "obj.GetType()",
            "namespace N; using System; public class M { public Type Run(object o) => o.GetType(); }"));

        // ===== Expressions =====

        f.Add(S(
            "arithmetic operators",
            "namespace N; public class M { public int Run(int a, int b) => a + b - a * b / (b + 1) % 3; }"));

        f.Add(S(
            "comparison operators",
            "namespace N; public class M { public bool Run(int a, int b) => a < b && a <= b && a > 0 && a >= 0 && a == 0 || a != b; }"));

        f.Add(S(
            "logical operators",
            "namespace N; public class M { public bool Run(bool a, bool b) => (a && b) || !a; }"));

        f.Add(S(
            "bitwise operators",
            "namespace N; public class M { public int Run(int a, int b) => (a & b) | (a ^ b) | (~a) | (a << 1) | (b >> 1); }"));

        f.Add(S(
            "assignment operators",
            """
            namespace N;
            public class M
            {
                public int Run(int x)
                {
                    int a = x; a += 1; a -= 1; a *= 2; a /= 2; a %= 3; a &= 7; a |= 1; a ^= 2; a <<= 1; a >>= 1;
                    return a;
                }
            }
            """));

        f.Add(S(
            "conditional operator (? :)",
            "namespace N; public class M { public int Run(bool b) => b ? 1 : 0; }"));

        f.Add(S(
            "null-coalescing (?? , ??=)",
            """
            namespace N;
            public class M
            {
                public string Run(string? a)
                {
                    string r = a ?? "default";
                    a ??= "fallback";
                    return r + a;
                }
            }
            """));

        f.Add(S(
            "null-conditional (?. , ?[])",
            """
            namespace N;
            public class Node { public Node? Next; public int[] Data = new int[1]; }
            public class M
            {
                public int? Run(Node? n) => n?.Next?.Data?[0];
            }
            """));

        f.Add(S(
            "tuple literals",
            "namespace N; public class M { public (int, string) Run() => (1, \"a\"); }"));

        f.Add(S(
            "tuple deconstruction",
            """
            namespace N;
            public class M
            {
                public int Run()
                {
                    (int a, int b) = (3, 4);
                    var (c, d) = (5, 6);
                    return a + b + c + d;
                }
            }
            """));

        f.Add(S(
            "range / index (x..y, ^n)",
            """
            namespace N;
            public class M
            {
                public int[] Run(int[] xs)
                {
                    int last = xs[^1];
                    int[] mid = xs[1..3];
                    mid[0] = last;
                    return mid;
                }
            }
            """));

        f.Add(S(
            "string interpolation",
            "namespace N; public class M { public string Run(string n, int c) => $\"{n} has {c} items\"; }"));

        f.Add(S(
            "collection expressions",
            "namespace N; using System.Collections.Generic; public class M { public List<int> Run() => [1, 2, 3]; public int[] Arr() => [4, 5]; }"));

        f.Add(S(
            "raw string literals",
            "namespace N; public class M { public string Run() => \"\"\"\n  line one\n  line two\n  \"\"\"; }"));

        f.Add(S(
            "lambda expressions",
            "namespace N; using System; public class M { public Func<int, int, int> Run() => (a, b) => a + b; }"));

        f.Add(S(
            "object initializers (new Foo { X = 1 })",
            "namespace N; public class Foo { public int X; public int Y; } public class M { public Foo Run() => new Foo { X = 1, Y = 2 }; }"));

        f.Add(S(
            "collection initializers (new List<T> { ... })",
            "namespace N; using System.Collections.Generic; public class M { public List<int> Run() => new List<int> { 1, 2, 3 }; }"));

        f.Add(S(
            "object creation (new Foo()) for non-XObject",
            "namespace N; public class Foo { } public class M { public Foo Run() => new Foo(); }"));

        f.Add(S(
            "XObject.New<T>(...) factory",
            """
            namespace N;
            // Local stand-in for the XPact.CSharp.BCL XObject + New<T> factory.
            // Pass-1 binds the factory call shape; the NewObject lowering is emit.
            public abstract class XObject
            {
                public static T New<T>(XObject? outer, string name) where T : XObject => null!;
            }
            public class Actor : XObject { }
            public class M { public Actor Spawn() => XObject.New<Actor>(null, "A"); }
            """));

        // ===== Generics =====

        f.Add(S(
            "generic class / struct / interface declarations",
            """
            namespace N;
            public class Box<T> { public T? Value; }
            public struct Cell<T> { public T Item; }
            public interface IContainer<T> { T Get(); }
            """));

        f.Add(S(
            "generic methods",
            "namespace N; public class M { public T Echo<T>(T value) => value; public (T, U) Pair<T, U>(T a, U b) => (a, b); }"));

        f.Add(S(
            "generic constraints (where T : ..., struct, unmanaged, new(), etc.)",
            """
            namespace N;
            using System;
            public class C1<T> where T : class { public T? V; }
            public class C2<T> where T : struct { public T V; }
            public class C3<T> where T : unmanaged { public T V; }
            public class C4<T> where T : new() { public T Make() => new T(); }
            public class C5<T> where T : IComparable<T> { public int Cmp(T a, T b) => a.CompareTo(b); }
            """));

        f.Add(S(
            "variance (in T, out T)",
            """
            namespace N;
            public interface IProducer<out T> { T Get(); }
            public interface IConsumer<in T> { void Put(T value); }
            """));

        // ===== BCL surfaces =====

        f.Add(S(
            "System.String",
            "namespace N; public class M { public int Len(string s) => s.Length; public string Up(string s) => s.ToUpperInvariant(); }"));

        f.Add(S(
            "System.Collections.Generic.List<T>",
            """
            namespace N;
            using System.Collections.Generic;
            public class M { public int Run() { var l = new List<int>(); l.Add(1); l.Add(2); return l.Count; } }
            """));

        f.Add(S(
            "System.Collections.Generic.Dictionary<K,V>",
            """
            namespace N;
            using System.Collections.Generic;
            public class M { public int Run() { var d = new Dictionary<string, int>(); d["a"] = 1; return d["a"]; } }
            """));

        f.Add(S(
            "System.Collections.Generic.HashSet<T>",
            """
            namespace N;
            using System.Collections.Generic;
            public class M { public int Run() { var s = new HashSet<int>(); s.Add(1); s.Add(1); return s.Count; } }
            """));

        f.Add(S(
            "System.Numerics.Vector*, Quaternion",
            """
            namespace N;
            using System.Numerics;
            public class M
            {
                public Vector3 V() => new Vector3(1, 2, 3);
                public Quaternion Q() => Quaternion.Identity;
            }
            """));

        f.Add(S(
            "volatile field keyword",
            "namespace N; public class M { private volatile int _flag; public void Set() { _flag = 1; } public int Get() => _flag; }"));

        f.Add(S(
            "C# char (16-bit UTF-16 code unit)",
            "namespace N; public class M { public char First(string s) => s[0]; public bool IsA(char c) => c == 'a'; }"));

        f.Add(S(
            "[Conditional(\"DEBUG\")] attribute",
            """
            namespace N;
            using System.Diagnostics;
            public class M
            {
                [Conditional("DEBUG")]
                public void Trace(string msg) { }
                public void Run() { Trace("x"); }
            }
            """));

        f.Add(S(
            "[ModuleInitializer] attribute (C# 9+)",
            """
            namespace N;
            using System.Runtime.CompilerServices;
            internal static class Init
            {
                [ModuleInitializer]
                internal static void Run() { }
            }
            """));

        f.Add(S(
            "C# 12 using X = SomeType<int>; alias-any-type",
            """
            namespace N;
            using System.Collections.Generic;
            using IntList = System.Collections.Generic.List<int>;
            using Coord = (int X, int Y);
            public class M
            {
                public IntList L() => new IntList { 1, 2 };
                public Coord C() => (3, 4);
            }
            """));

        // ===== Nullability =====

        f.Add(S(
            "T? (nullable reference, #nullable enable)",
            "namespace N; public class M { public string? Maybe(bool b) => b ? \"x\" : null; }"));

        f.Add(S(
            "int? / FVector? (nullable value)",
            """
            namespace N;
            using System.Numerics;
            public class M
            {
                public int? MaybeInt(bool b) => b ? 5 : null;
                public Vector3? MaybeVec(bool b) => b ? new Vector3(1, 1, 1) : null;
            }
            """));

        return f;
    }
}
