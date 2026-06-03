// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// One entry in the C# 12 feature corpus per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 4.1 ("The coverage matrix"):
/// a feature name (surfaced in the xUnit display name for triage), the C# 12
/// source that exercises it, and the matrix status the source was authored
/// for.
/// </summary>
/// <param name="Name">
/// The Section 4.1 row name (e.g. <c>"record class (positional)"</c>).
/// Surfaced in the test display name so a failure points at the exact row.
/// </param>
/// <param name="Source">
/// A self-contained C# 12 compilation unit that exercises the feature. It is
/// parsed + bound against the pinned .NET 8 BCL reference set; no other
/// sources are present, so each fixture must be standalone.
/// </param>
/// <param name="Status">
/// The Section 4.1 status the fixture is authored against
/// (<see cref="FeatureStatus.Supported"/> versus the banned / post-MVP
/// statuses).
/// </param>
internal sealed record CorpusFeature(string Name, string Source, FeatureStatus Status);

/// <summary>
/// The Section 4.1 status columns, collapsed to the two buckets Phase 6.a
/// cares about. Phase 6.a is PARSE + BIND only: it proves Supported features
/// bind with zero unexpected Roslyn errors, and that banned / post-MVP
/// features still parse + bind WITHOUT crashing (so the later XIL2CPP-
/// diagnostic pass can flag them). The XIL2CPP-specific rejection diagnostics
/// (XIL2CPP001 / 040 / 044 / ...) are a Pass-3 sub-phase, not asserted here.
/// </summary>
internal enum FeatureStatus
{
    /// <summary>
    /// Section 4.1 "Supported": must parse, bind, and produce zero
    /// UNEXPECTED Roslyn errors.
    /// </summary>
    Supported,

    /// <summary>
    /// Section 4.1 "BANNED" / "Permanently BANNED" / "post-MVP": must parse +
    /// bind WITHOUT throwing. The Roslyn binder may itself report (legitimate)
    /// errors for some of these (the C# language genuinely rejects them, e.g.
    /// <c>dynamic</c> with no runtime binder reference), but the front-end
    /// must not crash. The XIL2CPP-specific rejection is a later pass.
    /// </summary>
    BannedOrPostMvp,
}
