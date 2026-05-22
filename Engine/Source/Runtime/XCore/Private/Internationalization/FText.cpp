// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FText.cpp -- FText body (Section 11.2; Step 14).
// =====================================================================
//
// XCore-4a Rev 3 Section 11.2 implementation. Implements FromLiteral
// + ResolveForCurrentLocale + the source-fallback semantics.
//
// The FromLiteral body stores the three rodata pointers verbatim. No
// allocation happens at construction time -- the cache is filled lazily
// at ResolveForCurrentLocale time.
//
// The ResolveForCurrentLocale body uses FLocalizationManager::Lookup
// for the actual locale dispatch; this TU is intentionally thin and
// defers the locking + table-lookup machinery to the manager.
//
// =====================================================================

#include "Internationalization/FText.h"
#include "Internationalization/FLocalizationManager.h"

#include <cstdio>     // std::fprintf for the dev-warning path

namespace XCore::Loc
{

// ---------------------------------------------------------------------
// FromLiteral -- store three rodata pointers.
//
// The function is the canonical entry point for the LOCTEXT macro
// suite. It is intentionally simple: store the pointers, mark the
// cache as unresolved, and return the value. The cache fill happens
// on first ResolveForCurrentLocale call.
//
// noexcept: trivial pointer assignments + an FString default ctor
// (which is itself noexcept; cf. FString.h).
// ---------------------------------------------------------------------
FText FText::FromLiteral(const char* Namespace,
                         const char* Key,
                         const char* LiteralFallback) noexcept
{
    FText Result;
    Result.m_namespace        = Namespace;
    Result.m_key              = Key;
    Result.m_literalFallback  = LiteralFallback;
    Result.m_resolvedGen      = kUnresolvedGeneration;
    // Result.m_resolved is already the FString default empty value;
    // the first ResolveForCurrentLocale call will refresh it.
    return Result;
}

// ---------------------------------------------------------------------
// ResolveForCurrentLocale -- on-demand lookup + cache.
//
// Sequence:
//   1. Empty-sentinel short-circuit: if m_literalFallback is null,
//      return the cached empty FString.
//   2. Cache hit: if m_resolvedGen matches the manager's current
//      generation, return the cached FString.
//   3. Lookup: ask FLocalizationManager for (m_namespace, m_key);
//      on hit, copy the localised value into m_resolved.
//      On miss, copy the literal fallback into m_resolved AND emit a
//      dev-warning per Section 17.8 H3 (unless this is the invariant
//      sentinel, where a missing-key is expected and silent).
//   4. Update m_resolvedGen to the current manager generation.
//
// The FString copy on cache fill is one heap allocation per FText per
// SetLocale event (not per ResolveForCurrentLocale call); the steady-
// state cost is one comparison + one reference return.
// ---------------------------------------------------------------------
const ::XCore::FString& FText::ResolveForCurrentLocale() const
{
    // Empty-sentinel: default-constructed FText with no literal fallback.
    if (m_literalFallback == nullptr)
    {
        // m_resolved is already the empty FString from the default ctor;
        // mark the cache as always-current so the early-return below
        // takes the fast path on subsequent calls.
        m_resolvedGen = FLocalizationManager::GetCurrentGeneration();
        return m_resolved;
    }

    const ::std::uint32_t CurrentGen = FLocalizationManager::GetCurrentGeneration();

    // Cache hit -- common case after the first resolve per locale.
    // Generation 0 (kUnresolvedGeneration) is the never-resolved
    // sentinel; a manager-reported generation of 0 (PreStaticInit)
    // also takes the slow path (the manager has not yet initialised
    // and Lookup will return nullptr -> fallback).
    if (m_resolvedGen == CurrentGen && CurrentGen != kUnresolvedGeneration)
    {
        return m_resolved;
    }

    // kFormattedGeneration: an already-formatted FText produced by
    // FText::Format. m_resolved holds the formatted output bytes; the
    // namespace+key identify the SOURCE FText but a subsequent
    // re-resolve would replace the formatted output with the
    // unformatted localised value. Short-circuit to the cached bytes.
    if (m_resolvedGen == kFormattedGeneration)
    {
        return m_resolved;
    }

    // Cache miss -- do the lookup.
    const ::XCore::FString* Hit = FLocalizationManager::Lookup(m_namespace, m_key);
    if (Hit != nullptr)
    {
        // Found in the loctable. Copy the localised value into the cache.
        m_resolved = *Hit;
    }
    else
    {
        // Miss: fall back to the literal source text per Section 17.8 H3.
        // Emit a dev-warning UNLESS this is the invariant sentinel
        // (namespace == "__Invariant"), where a "miss" is expected.
        //
        // The literal-fallback FString-construction is one heap or SSO
        // allocation; the cost is paid once per FText per SetLocale.
        m_resolved = ::XCore::FString(m_literalFallback);

#if XPACT_DEBUG || XPACT_DEVELOPMENT
        // Invariant sentinel: silent.
        bool IsInvariantSentinel = false;
        if (m_namespace != nullptr)
        {
            // Compare the namespace against "__Invariant" without
            // pulling <cstring> just for one strcmp; the canonical
            // sentinel string is short enough to inline.
            const char* Sentinel = "__Invariant";
            IsInvariantSentinel = true;
            for (::SIZE_T I = 0; ; ++I)
            {
                if (Sentinel[I] != m_namespace[I])
                {
                    IsInvariantSentinel = false;
                    break;
                }
                if (Sentinel[I] == '\0')
                {
                    break;
                }
            }
        }

        if (!IsInvariantSentinel)
        {
            // Dev-warning to stderr. Phase 1f does not yet have XLog
            // (the engine's structured log channel; lands post-XCore-4a);
            // stderr is the correct landing pad for Phase 1f because
            // XCore-4a's own unit tests read stderr to verify the
            // warning fired.
            //
            // The message names the namespace + key so the developer
            // can find the missing loctable entry quickly.
            std::fprintf(
                stderr,
                "[XCore::Loc] WARN: Missing loctable entry for "
                "namespace='%s' key='%s'; using literal fallback.\n",
                (m_namespace ? m_namespace : "(null)"),
                (m_key       ? m_key       : "(null)"));
        }
#endif
    }

    // Mark the cache as current for the active locale generation.
    m_resolvedGen = CurrentGen;

    return m_resolved;
}

} // namespace XCore::Loc
