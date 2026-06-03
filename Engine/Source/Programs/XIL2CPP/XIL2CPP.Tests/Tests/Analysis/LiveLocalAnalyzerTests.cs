// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="LiveLocalAnalyzer"/> (WU-6G-STACKMAP) per
/// /Documents/XIL2CPP.html Rev 4 Section 5.x (precise rooting) +
/// /Documents/XCoreXObject.html Section 5.2 (the stack-map protocol). Drives
/// synthetic C# sources through the real Pass 1 -&gt; Pass 2 pipeline, runs JUST
/// the <see cref="LiveLocalAnalyzer"/>, and asserts the recorded
/// <see cref="LiveLocalRecord"/> oracle: one per instance <c>self</c>, one per
/// by-value XObject(-derived) parameter, and one per XObject(-derived) body
/// local -- in the deterministic shadow-stack allocation order (self at index
/// 0, then parameters, then locals).
/// </summary>
public sealed class LiveLocalAnalyzerTests
{
    // A locally-declared stand-in for the engine root reference type; the
    // metadata-name + namespace fallback in AnalyzerHelpers.IsXObjectType /
    // IsXObjectDerived recognises it even without the curated XObject BCL ref.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    private static IReadOnlyList<LiveLocalRecord> Analyze(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result result = Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new LiveLocalAnalyzer() });
        return result.GetAll<LiveLocalRecord>();
    }

    /// <summary>Records for the method whose display contains <paramref name="methodName"/>, in allocation order.</summary>
    private static IReadOnlyList<LiveLocalRecord> ForMethod(
        IReadOnlyList<LiveLocalRecord> all, string methodName)
        => all.Where(r => r.ContainingMethodDisplay.Contains("." + methodName + "("))
            .OrderBy(r => r.DeclarationIndex)
            .ToList();

    // -----------------------------------------------------------------
    // self receiver.
    // -----------------------------------------------------------------

    [Fact]
    public void InstanceMethod_RecordsSelfAtIndexZero()
    {
        IReadOnlyList<LiveLocalRecord> all = Analyze(
            "namespace M; public class A { public void F(int x) { } }");

        LiveLocalRecord self = Assert.Single(ForMethod(all, "F"));
        Assert.Equal(LiveLocalKind.SelfReceiver, self.Kind);
        Assert.Equal(0, self.DeclarationIndex);
        Assert.Equal(LiveLocalAnalyzer.SelfDisplay, self.SymbolDisplay);
    }

    [Fact]
    public void StaticMethod_OmitsSelf()
    {
        IReadOnlyList<LiveLocalRecord> all = Analyze(
            "namespace M; public class A { public static void F(int x) { } }");

        Assert.Empty(ForMethod(all, "F"));
    }

    [Fact]
    public void Constructor_RecordsSelf()
    {
        IReadOnlyList<LiveLocalRecord> all = Analyze(
            "namespace M; public class A { public A(int seed) { } }");

        // The constructor display renders the name as the type name; find by self.
        LiveLocalRecord self = Assert.Single(all.Where(r => r.Kind == LiveLocalKind.SelfReceiver));
        Assert.Equal(0, self.DeclarationIndex);
    }

    [Fact]
    public void StaticConstructor_OmitsSelf()
    {
        IReadOnlyList<LiveLocalRecord> all = Analyze(
            "namespace M; public class A { static A() { } }");

        Assert.Empty(all);
    }

    // -----------------------------------------------------------------
    // Parameters.
    // -----------------------------------------------------------------

    [Fact]
    public void XObjectParameter_RootsAfterSelf()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class Actor : XObject { }
                    public class A : XObject { public void F(Actor a) { } }
                }"),
            "F");

        Assert.Equal(2, records.Count);
        Assert.Equal(LiveLocalKind.SelfReceiver, records[0].Kind);
        Assert.Equal(0, records[0].DeclarationIndex);
        Assert.Equal(LiveLocalKind.Parameter, records[1].Kind);
        Assert.Equal(1, records[1].DeclarationIndex);
        Assert.Equal("a", records[1].SymbolDisplay);
    }

    [Fact]
    public void ValueTypedParameter_IsNotRooted()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class A : XObject { public void F(int n, float f, bool b) { } }
                }"),
            "F");

        // Only self -- no value-typed parameter roots a slot.
        LiveLocalRecord only = Assert.Single(records);
        Assert.Equal(LiveLocalKind.SelfReceiver, only.Kind);
    }

    [Fact]
    public void ByRefXObjectParameter_IsNotRooted()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class Actor : XObject { }
                    public class A : XObject {
                        public void F(ref Actor a, out Actor b, in Actor c) { b = a; }
                    }
                }"),
            "F");

        // A ref/out/in parameter is an alias the caller already roots; only self.
        LiveLocalRecord only = Assert.Single(records);
        Assert.Equal(LiveLocalKind.SelfReceiver, only.Kind);
    }

    [Fact]
    public void MultipleXObjectParameters_RootInDeclarationOrder()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class Actor : XObject { }
                    public class A : XObject {
                        public static void F(Actor first, int mid, Actor last) { }
                    }
                }"),
            "F");

        // Static method: no self. Two XObject params root indices 0 + 1, the
        // value-typed mid is skipped.
        Assert.Equal(2, records.Count);
        Assert.Equal("first", records[0].SymbolDisplay);
        Assert.Equal(0, records[0].DeclarationIndex);
        Assert.Equal("last", records[1].SymbolDisplay);
        Assert.Equal(1, records[1].DeclarationIndex);
    }

    [Fact]
    public void XObjectItself_AsParameter_IsRooted()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class A : XObject { public static void F(XObject o) { } }
                }"),
            "F");

        LiveLocalRecord only = Assert.Single(records);
        Assert.Equal(LiveLocalKind.Parameter, only.Kind);
        Assert.Equal("o", only.SymbolDisplay);
    }

    // -----------------------------------------------------------------
    // Locals.
    // -----------------------------------------------------------------

    [Fact]
    public void XObjectLocal_RootsAfterSelfAndParameters()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class Actor : XObject { }
                    public class A : XObject {
                        public void F(Actor a) { Actor local = a; }
                    }
                }"),
            "F");

        Assert.Equal(3, records.Count);
        Assert.Equal(LiveLocalKind.SelfReceiver, records[0].Kind);
        Assert.Equal(LiveLocalKind.Parameter, records[1].Kind);
        Assert.Equal(LiveLocalKind.Local, records[2].Kind);
        Assert.Equal("local", records[2].SymbolDisplay);
        Assert.Equal(2, records[2].DeclarationIndex);
    }

    [Fact]
    public void ValueTypedLocal_IsNotRooted()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class A : XObject { public void F() { int n = 1; bool b = false; } }
                }"),
            "F");

        // Only self -- value-typed locals are not rooted.
        LiveLocalRecord only = Assert.Single(records);
        Assert.Equal(LiveLocalKind.SelfReceiver, only.Kind);
    }

    [Fact]
    public void NestedLocalFunctionLocals_AreNotRootedInOuterMethod()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class Actor : XObject { }
                    public class A : XObject {
                        public void F() {
                            void Inner() { Actor nested = null; }
                            Inner();
                        }
                    }
                }"),
            "F");

        // The outer method roots only self; the nested local function's local
        // belongs to ITS own emitted method, not F.
        LiveLocalRecord only = Assert.Single(records);
        Assert.Equal(LiveLocalKind.SelfReceiver, only.Kind);
    }

    [Fact]
    public void MultipleLocalsInOneDeclaration_EachRootInOrder()
    {
        IReadOnlyList<LiveLocalRecord> records = ForMethod(
            Analyze(
                XObjectStub,
                @"namespace M {
                    using XPact.CoreXObject;
                    public class Actor : XObject { }
                    public class A : XObject {
                        public static void F() { Actor x = null, y = null; }
                    }
                }"),
            "F");

        Assert.Equal(2, records.Count);
        Assert.Equal("x", records[0].SymbolDisplay);
        Assert.Equal(0, records[0].DeclarationIndex);
        Assert.Equal("y", records[1].SymbolDisplay);
        Assert.Equal(1, records[1].DeclarationIndex);
    }

    // -----------------------------------------------------------------
    // Accessors.
    // -----------------------------------------------------------------

    [Fact]
    public void PropertyAccessors_RecordSelf()
    {
        // A manual property with a get + set body: each accessor is its own
        // emittable method and roots self.
        IReadOnlyList<LiveLocalRecord> all = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class A : XObject {
                    private int _v;
                    public int V { get { return _v; } set { _v = value; } }
                }
            }");

        // Two accessor methods, each rooting exactly self.
        IReadOnlyList<LiveLocalRecord> selves =
            all.Where(r => r.Kind == LiveLocalKind.SelfReceiver).ToList();
        Assert.Equal(2, selves.Count);
        Assert.All(selves, s => Assert.Equal(0, s.DeclarationIndex));
    }

    // -----------------------------------------------------------------
    // Determinism + analyzer identity.
    // -----------------------------------------------------------------

    [Fact]
    public void Name_IsStable()
    {
        Assert.Equal("LiveLocalAnalyzer", new LiveLocalAnalyzer().Name);
    }

    [Fact]
    public void Analyze_IsDeterministic_AcrossRuns()
    {
        const string source = @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class A : XObject {
                    public void F(Actor a) { Actor local = a; }
                    public static int G(Actor x, int n) { return n; }
                }
            }";

        IReadOnlyList<LiveLocalRecord> first = Analyze(XObjectStub, source);
        IReadOnlyList<LiveLocalRecord> second = Analyze(XObjectStub, source);

        Assert.Equal(first, second);
    }
}
