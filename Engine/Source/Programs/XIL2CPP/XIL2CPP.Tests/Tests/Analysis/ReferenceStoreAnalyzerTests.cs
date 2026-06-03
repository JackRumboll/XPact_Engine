// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="ReferenceStoreAnalyzer"/> (WU-17): reference-store-site
/// enumeration for the Pass-6 write-barrier emit, and the
/// <c>XIL2CPP063</c> chained-reference-write rejection. Per
/// /Documents/XIL2CPP.html Rev 4 Sections 6.3 + 5.3.
/// </summary>
public sealed class ReferenceStoreAnalyzerTests
{
    // A locally-declared stand-in for the engine root reference type. The
    // metadata-name + namespace fallback in AnalyzerHelpers.IsXObjectDerived
    // recognises it even though the real curated XObject BCL ref is absent.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    private static Pass3Result Analyze(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new ReferenceStoreAnalyzer() });
    }

    private static Pass3Result Analyze(params string[] sources)
        => Analyze(isSimPath: false, sources);

    // -----------------------------------------------------------------
    // Simple XObject field store -- recorded, no diagnostic.
    // -----------------------------------------------------------------

    [Fact]
    public void SimpleXObjectFieldStore_IsRecorded_WithNoDiagnostic()
    {
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor Slot;
                    public void Set(Actor a) { this.Slot = a; }
                }
            }");

        IReadOnlyList<ReferenceStoreSite> sites = result.GetAll<ReferenceStoreSite>();
        ReferenceStoreSite site = Assert.Single(sites);
        Assert.Equal(ReferenceSlotKind.Field, site.SlotKind);
        Assert.False(site.IsCompound);
        Assert.False(site.IsChainedWrite);
        Assert.Contains("Slot", site.SlotSymbolDisplay);
        Assert.NotNull(site.ContainingMethodDisplay);
        Assert.Contains("Set", site.ContainingMethodDisplay!);

        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void SimpleXObjectPropertyStore_IsRecorded_AsProperty()
    {
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor Slot { get; set; }
                    public void Set(Actor a) { Slot = a; }
                }
            }");

        ReferenceStoreSite site = Assert.Single(result.GetAll<ReferenceStoreSite>());
        Assert.Equal(ReferenceSlotKind.Property, site.SlotKind);
        Assert.False(site.IsChainedWrite);
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Compound assignment into an XObject field -- recorded + flagged compound.
    // -----------------------------------------------------------------

    [Fact]
    public void CompoundAssignment_IsRecorded_AsCompound()
    {
        // ??= is the realistic compound form for a reference slot.
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor Slot;
                    public void Set(Actor a) { this.Slot ??= a; }
                }
            }");

        ReferenceStoreSite site = Assert.Single(result.GetAll<ReferenceStoreSite>());
        Assert.True(site.IsCompound);
        Assert.False(site.IsChainedWrite);
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Chained reference write a.b.c = x -- recorded AND XIL2CPP063.
    // -----------------------------------------------------------------

    [Fact]
    public void ChainedReferenceWrite_EmitsXil2Cpp063_AndIsRecordedAsChained()
    {
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Inner : XObject { public Actor Slot; }
                public class Outer : XObject { public Inner Mid; }
                public class Holder : XObject {
                    public Outer Top;
                    public void Set(Actor a) { Top.Mid.Slot = a; }
                }
            }");

        ReferenceStoreSite site = Assert.Single(result.GetAll<ReferenceStoreSite>());
        Assert.True(site.IsChainedWrite);

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ChainedWriteThroughXPtrUndefined, diag.Code);
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void SingleLinkWrite_ThroughLocal_IsNotChained()
    {
        // inner.Slot = a where `inner` is a local: receiver is an identifier,
        // not a member access -- a single link, NOT a chained write.
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Inner : XObject { public Actor Slot; }
                public class Holder : XObject {
                    public void Set(Inner inner, Actor a) { inner.Slot = a; }
                }
            }");

        ReferenceStoreSite site = Assert.Single(result.GetAll<ReferenceStoreSite>());
        Assert.False(site.IsChainedWrite);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ChainThroughValueTypeIntermediate_IsNotChained()
    {
        // outer.MidStruct.Slot = a: the intermediate `MidStruct` is a VALUE
        // type, not an XObject reference link, so this is a single-link store
        // (the value struct is embedded in `outer`), NOT a chained write.
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public struct MidBox { public Actor Slot; }
                public class Holder : XObject {
                    public MidBox MidStruct;
                    public void Set(Actor a) { this.MidStruct.Slot = a; }
                }
            }");

        ReferenceStoreSite site = Assert.Single(result.GetAll<ReferenceStoreSite>());
        Assert.False(site.IsChainedWrite);
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Value-typed field store -- NOT recorded.
    // -----------------------------------------------------------------

    [Fact]
    public void ValueTypedFieldStore_IsNotRecorded()
    {
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Holder : XObject {
                    public int Count;
                    public void Set(int n) { this.Count = n; }
                }
            }");

        Assert.Empty(result.GetAll<ReferenceStoreSite>());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NonXObjectReferenceFieldStore_IsNotRecorded()
    {
        // A plain class field (NOT XObject-derived) is not a reference-store
        // slot for the GC write barrier.
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Plain { }
                public class Holder : XObject {
                    public Plain P;
                    public void Set(Plain p) { this.P = p; }
                }
            }");

        Assert.Empty(result.GetAll<ReferenceStoreSite>());
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Local-variable store -- NOT recorded.
    // -----------------------------------------------------------------

    [Fact]
    public void LocalVariableStore_IsNotRecorded()
    {
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public void Set(Actor a) { Actor local = null; local = a; }
                }
            }");

        Assert.Empty(result.GetAll<ReferenceStoreSite>());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParameterStore_IsNotRecorded()
    {
        Pass3Result result = Analyze(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public void Set(Actor a, Actor b) { a = b; }
                }
            }");

        Assert.Empty(result.GetAll<ReferenceStoreSite>());
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Determinism + multi-site ordering.
    // -----------------------------------------------------------------

    [Fact]
    public void MultipleSites_AreRecordedInDocumentOrder_Deterministically()
    {
        string[] sources =
        {
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor A;
                    public Actor B;
                    public void Set(Actor x, Actor y) { this.A = x; this.B = y; }
                }
            }",
        };

        Pass3Result first = Analyze(sources);
        Pass3Result second = Analyze(sources);

        IReadOnlyList<ReferenceStoreSite> firstSites = first.GetAll<ReferenceStoreSite>();
        IReadOnlyList<ReferenceStoreSite> secondSites = second.GetAll<ReferenceStoreSite>();

        Assert.Equal(2, firstSites.Count);
        // A appears before B (document order).
        Assert.Contains(".A", firstSites[0].SlotSymbolDisplay);
        Assert.Contains(".B", firstSites[1].SlotSymbolDisplay);

        // Deterministic across runs.
        Assert.Equal(
            firstSites.Select(s => s.SlotSymbolDisplay).ToList(),
            secondSites.Select(s => s.SlotSymbolDisplay).ToList());
        Assert.Equal(
            firstSites.Select(s => (s.Span.StartLine, s.Span.StartColumn)).ToList(),
            secondSites.Select(s => (s.Span.StartLine, s.Span.StartColumn)).ToList());
    }
}
