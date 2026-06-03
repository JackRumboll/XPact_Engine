// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Pipeline;

/// <summary>
/// Shared source fixtures for the full Pass 1 -&gt; Pass 2 -&gt; Pass 3
/// pipeline integration tests (determinism + cross-analyzer
/// no-double-emission) per /Documents/XIL2CPP.html Rev 4 Section 3.2 + 9.9.
/// </summary>
/// <remarks>
/// The fixtures bind against <c>Basic.Reference.Assemblies.Net80</c> (via
/// <c>NormalizationTestHelpers.BuildPass1</c>) with a locally declared
/// stand-in for the engine <c>XObject</c> root reference type plus its
/// <c>New&lt;T&gt;</c> factory, mirroring the per-analyzer test fixtures
/// (<c>NewExpressionAnalyzerTests</c>). Product code never references the
/// test BCL set; that would break the X-IL2CPP-CSPATH-DET determinism gate.
/// </remarks>
internal static class PipelineTestCorpus
{
    /// <summary>
    /// A locally declared stand-in for the engine root reference type plus
    /// its <c>New</c> / <c>TryNew</c> static factory and the
    /// <c>[XObjectInternalConstructor]</c> suppression attribute. Mirrors the
    /// surface /Documents/XIL2CPP.html Section 2.3 documents; the
    /// <c>where T : XObject</c> constraint is intentionally omitted so the
    /// analyzers (not the binder) surface the XObject-derived rules.
    /// </summary>
    public const string XObjectStub = """
        namespace XPact.CoreXObject
        {
            public sealed class XObjectInternalConstructorAttribute : System.Attribute { }

            public abstract class XObject
            {
                public static T New<T>(XObject outer, string name, int flags) => default!;
                public static T TryNew<T>(XObject outer, string name, int flags) => default!;
            }
        }
        """;

    /// <summary>
    /// A multi-feature NON-sim-path source exercising records, pattern
    /// matching, lambdas, generics, and an XObject-derived <c>new</c> (which
    /// surfaces XIL2CPP001 from <c>NewExpressionAnalyzer</c>). Composed to
    /// drive several normalizers (record positional ctor, pattern match,
    /// lambda/local-function, expression body) and several analyzers
    /// (new-expression, lambda capture, generic instantiation, container)
    /// simultaneously so the determinism lock spans the whole pipeline.
    /// </summary>
    public const string MultiFeatureNonSimPath = """
        using System;
        using System.Collections.Generic;
        using XPact.CoreXObject;

        namespace M
        {
            // Record positional ctor (RecordPositionalCtorNormalizer) +
            // record with-expression (RecordWithExpressionNormalizer).
            public record Point(int X, int Y)
            {
                public Point Shifted() => this with { X = X + 1 };
            }

            // XObject-derived type -> the new-expression below is XIL2CPP001.
            public sealed class Widget : XObject { public int Tag; }

            // Generic type + method (GenericInstantiationAnalyzer surface).
            public sealed class Box<T>
            {
                public T? Value { get; set; }
                public U Map<U>(Func<T, U> f) => f(Value!);
            }

            public class Sample
            {
                // Lambda capture (LambdaCaptureAnalyzer) + local function
                // (LocalFunctionNormalizer).
                public int Compute(int seed)
                {
                    int captured = seed * 2;
                    Func<int, int> adder = x => x + captured;

                    int Local(int v) => v + adder(v);

                    return Local(seed);
                }

                // Pattern matching (PatternMatchNormalizer) over a record.
                public string Describe(object o) => o switch
                {
                    Point { X: 0, Y: 0 } => "origin",
                    Point p => $"({p.X},{p.Y})",
                    int n when n > 0 => "positive",
                    _ => "other",
                };

                // XObject-derived new -> XIL2CPP001 (Locked Commitment 3).
                public Widget MakeWidget() => new Widget { Tag = 7 };

                // Container + generic instantiation surface.
                public Box<int> MakeBox() => new Box<int> { Value = 3 };

                public Dictionary<string, int> Counts() => new() { ["a"] = 1 };
            }
        }
        """;
}
