// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Tiering;

/// <summary>
/// The per-function tier verdict XIL2CPP Pass 4 produces, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 / 3.3: a stable id, the
/// human-readable function display, the assigned <see cref="FunctionTier"/>,
/// and (for Tier 1) the demotion reason naming the failing Tier-2 clause.
/// </summary>
/// <remarks>
/// <para>
/// <b>Demotion reason.</b> Empty for a Tier-2 function (it satisfies every
/// clause, so there is nothing to explain). For a Tier-1 function it names
/// the first / most-specific clause that demoted it (e.g. <c>"exported"</c>,
/// <c>"throws in body"</c>, <c>"[XFunction(CanThrow = true)]"</c>, or
/// <c>"calls Tier-1 callee &lt;display&gt;"</c>). The reason is informational
/// (it does not affect the ABI), so the wording is chosen for operator
/// clarity, not as a stable contract field.
/// </para>
/// <para>
/// This is the minimal Phase-6.c classification record the
/// <see cref="TierTable"/> sorts + serializes. The richer per-function schema
/// the doc sketches (<c>exported</c> / <c>canThrowAttributed</c> /
/// <c>throwsInBody</c> / <c>calleesUnverified</c> / <c>manglingV1</c> fields)
/// is a later-pass enrichment; <c>manglingV1</c> in particular is Pass 5's
/// output, which <see cref="StableId"/> stands in for at Pass 4.
/// </para>
/// </remarks>
/// <param name="Id">The deterministic per-function stable id (sort key for the table).</param>
/// <param name="FunctionDisplay">The fully-qualified function display string (human-readable).</param>
/// <param name="Tier">The assigned calling-convention tier.</param>
/// <param name="DemotionReason">For Tier 1, the failing Tier-2 clause; empty for Tier 2.</param>
public sealed record TierClassification(
    StableId Id,
    string FunctionDisplay,
    FunctionTier Tier,
    string DemotionReason);
