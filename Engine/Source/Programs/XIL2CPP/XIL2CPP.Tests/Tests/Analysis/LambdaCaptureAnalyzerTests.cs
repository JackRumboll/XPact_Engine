// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="LambdaCaptureAnalyzer"/> (WU-19, /Documents/XIL2CPP.html
/// Rev 4 Section 5.9): per-lambda capture classification (XObject captures
/// needing XGCRootSpan, value captures, implicit this-capture, non-capturing)
/// plus the XIL2CPP080 (Span/ref-struct-over-XObject captured by a closure) and
/// XIL2CPP096 ([XValueClass]-tagged type captured by reference) diagnostics.
/// </summary>
public sealed class LambdaCaptureAnalyzerTests
{
    // A minimal XObject stand-in + [XValueClass] attribute the analyzer's
    // metadata-name fallback recognizes (the real engine BCL is not on the
    // test reference set). Prepended to every fixture.
    private const string Prelude = @"
namespace XPact.CoreXObject { public abstract class XObject { } }
namespace XPact { public sealed class XValueClassAttribute : System.Attribute { } }
";

    private static Pass3Result Analyze(string source)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(Prelude + source);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new LambdaCaptureAnalyzer() });
    }

    private static ClassifiedCaptureSet Single(Pass3Result result)
        => Assert.Single(result.GetAll<ClassifiedCaptureSet>());

    // -----------------------------------------------------------------
    // Capture classification.
    // -----------------------------------------------------------------

    [Fact]
    public void NonCapturingLambda_RecordedAsNonCapturing_NoDiagnostics()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    public class A {
        public void Run() { Func<int,int> f = x => x * x; var y = f(3); }
    }
}");

        ClassifiedCaptureSet set = Single(result);
        Assert.True(set.IsNonCapturing);
        Assert.False(set.CapturesThis);
        Assert.Empty(set.XObjectCaptures);
        Assert.Empty(set.ValueCaptures);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ValueCapture_ClassifiedAsValue_NotXObject_NoDiagnostics()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    public class A {
        public void Run() { int n = 5; Func<int,int> f = x => x + n; var y = f(1); }
    }
}");

        ClassifiedCaptureSet set = Single(result);
        Assert.False(set.IsNonCapturing);
        Assert.False(set.CapturesThis);
        Assert.Empty(set.XObjectCaptures);
        ClassifiedCapture cap = Assert.Single(set.ValueCaptures);
        Assert.Equal("n", cap.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void XObjectCapture_ClassifiedAsXObject_NeedsRootSpan_NoDiagnostics()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    using XPact.CoreXObject;
    public class Actor : XObject { public void Click() { } }
    public class A {
        public void Run() { Actor a = null!; Action f = () => a.Click(); f(); }
    }
}");

        ClassifiedCaptureSet set = Single(result);
        Assert.False(set.IsNonCapturing);
        Assert.False(set.CapturesThis);
        Assert.Empty(set.ValueCaptures);
        ClassifiedCapture cap = Assert.Single(set.XObjectCaptures);
        Assert.Equal("a", cap.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ImplicitThisCapture_RecordedAsCapturesThis_NoDiagnostics()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    using XPact.CoreXObject;
    public class Host : XObject {
        private int field;
        public Action Make() { return () => { field = 1; }; }
    }
}");

        ClassifiedCaptureSet set = Single(result);
        Assert.True(set.CapturesThis);
        Assert.False(set.IsNonCapturing);
        // `this` is the only capture; it is recorded via the flag, NOT as a
        // captured variable in either bucket.
        Assert.Empty(set.XObjectCaptures);
        Assert.Empty(set.ValueCaptures);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MixedCaptures_BucketedSeparately()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    using XPact.CoreXObject;
    public class Actor : XObject { public int Id; }
    public class A {
        public void Run() {
            int n = 5;
            Actor a = null!;
            Func<int> f = () => n + a.Id;
            var y = f();
        }
    }
}");

        ClassifiedCaptureSet set = Single(result);
        Assert.Equal("a", Assert.Single(set.XObjectCaptures).Name);
        Assert.Equal("n", Assert.Single(set.ValueCaptures).Name);
        Assert.False(set.CapturesThis);
        Assert.False(set.IsNonCapturing);
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Anonymous methods (delegate { ... }) are covered too.
    // -----------------------------------------------------------------

    [Fact]
    public void AnonymousMethod_ValueCapture_IsClassified()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    public class A {
        public void Run() { int n = 3; Action f = delegate() { var z = n; }; f(); }
    }
}");

        ClassifiedCaptureSet set = Single(result);
        Assert.Equal("n", Assert.Single(set.ValueCaptures).Name);
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // XIL2CPP080: Span/ref-struct over XObject captured by a closure.
    // -----------------------------------------------------------------

    [Fact]
    public void SpanOverXObject_CapturedByLambda_EmitsXIL2CPP080()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    using XPact.CoreXObject;
    public class Actor : XObject { }
    public class A {
        public void Run() {
            Span<Actor> s = default;
            Action f = () => { var z = s.Length; };
            f();
        }
    }
}");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.SpanOverXObjectUnsupported, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);
        Assert.Contains("s", diag.Message);

        // The Span capture is NOT bucketed as a value or XObject capture.
        ClassifiedCaptureSet set = Single(result);
        Assert.Empty(set.XObjectCaptures);
        Assert.Empty(set.ValueCaptures);
    }

    [Fact]
    public void SpanOverPrimitive_CapturedByLambda_DoesNotEmit080()
    {
        // Span<int> carries no XObject reference, so capturing it is not the
        // XIL2CPP080 case (the CS8175 ref-local-capture error is Roslyn's, not
        // this analyzer's concern). It is bucketed as a value capture.
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    public class A {
        public void Run() {
            Span<int> s = default;
            Action f = () => { var z = s.Length; };
            f();
        }
    }
}");

        Assert.Empty(result.Diagnostics);
        ClassifiedCaptureSet set = Single(result);
        Assert.Equal("s", Assert.Single(set.ValueCaptures).Name);
    }

    // -----------------------------------------------------------------
    // XIL2CPP096: [XValueClass]-tagged type captured by reference.
    // -----------------------------------------------------------------

    [Fact]
    public void XValueClassType_CapturedByLambda_EmitsXIL2CPP096()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    using XPact;
    [XValueClass] public class FPoint { public int X; }
    public class A {
        public void Run() {
            FPoint p = new FPoint();
            Action f = () => { var z = p.X; };
            f();
        }
    }
}");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.XValueClassObservedWithReferenceIdentity, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("XValueClass", diag.Message);

        // The value-class capture is still recorded (as a value capture) so
        // Pass 6 has its metadata.
        ClassifiedCaptureSet set = Single(result);
        Assert.Equal("p", Assert.Single(set.ValueCaptures).Name);
    }

    [Fact]
    public void PlainReferenceCapture_NoXValueClass_DoesNotEmit096()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    public class Plain { public int X; }
    public class A {
        public void Run() {
            Plain p = new Plain();
            Action f = () => { var z = p.X; };
            f();
        }
    }
}");

        Assert.Empty(result.Diagnostics);
        ClassifiedCaptureSet set = Single(result);
        Assert.Equal("p", Assert.Single(set.ValueCaptures).Name);
    }

    // -----------------------------------------------------------------
    // Keying + multiplicity + determinism.
    // -----------------------------------------------------------------

    [Fact]
    public void MultipleLambdas_EachRecordedKeyedByOwnSymbol()
    {
        Pass3Result result = Analyze(@"
namespace M {
    using System;
    public class A {
        public void Run() {
            Func<int,int> f = x => x * x;
            int n = 2;
            Func<int,int> g = x => x + n;
        }
    }
}");

        IReadOnlyList<ClassifiedCaptureSet> sets = result.GetAll<ClassifiedCaptureSet>();
        Assert.Equal(2, sets.Count);
        // Distinct lambda method symbols.
        Assert.NotSame(sets[0].LambdaSymbol, sets[1].LambdaSymbol);
        // Source order: the non-capturing square lambda first, the
        // value-capturing add lambda second.
        Assert.True(sets[0].IsNonCapturing);
        Assert.False(sets[1].IsNonCapturing);
        Assert.Equal("n", Assert.Single(sets[1].ValueCaptures).Name);
    }

    [Fact]
    public void Run_IsDeterministic_AcrossTwoRuns()
    {
        const string source = @"
namespace M {
    using System;
    using XPact.CoreXObject;
    public class Actor : XObject { public int Id; }
    public class A {
        public void Run() {
            int n = 5;
            Actor a = null!;
            Actor b = null!;
            Func<int> f = () => n + a.Id + b.Id;
        }
    }
}";

        Pass3Result first = Analyze(source);
        Pass3Result second = Analyze(source);

        ClassifiedCaptureSet s1 = Single(first);
        ClassifiedCaptureSet s2 = Single(second);

        Assert.Equal(
            s1.XObjectCaptures.Select(c => c.Name).ToList(),
            s2.XObjectCaptures.Select(c => c.Name).ToList());
        Assert.Equal(
            s1.ValueCaptures.Select(c => c.Name).ToList(),
            s2.ValueCaptures.Select(c => c.Name).ToList());
        // a, b sorted deterministically.
        Assert.Equal(new[] { "a", "b" }, s1.XObjectCaptures.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NoLambdas_RecordsNothing_NoDiagnostics()
    {
        Pass3Result result = Analyze(@"
namespace M {
    public class A { public int F() => 1; }
}");

        Assert.Empty(result.GetAll<ClassifiedCaptureSet>());
        Assert.Empty(result.Diagnostics);
    }
}
