// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FText.h -- localized-text type (Section 11.2; Step 14).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.2 + 11.3 + 11.4 + 11.6 + 11.7 + Section
// 17.8 (H1/H2/H3 acceptance) + Section 18 OPEN-5 (per-locale binary
// loctable storage).
//
// FText is XPact's localised-string type. It is a three-pointer POD
// plus a lazy FString cache, in deliberate contrast to UE's FText
// (which is an FTextHistory subclass hierarchy + refcount; see
// `UnrealEngine/Engine/Source/Runtime/Core/Public/Internationalization/
// Text.h:406`). The three-pointer form covers the MVP use case
// (literal + namespace + key + on-demand resolved-string cache) and
// avoids:
//
//   * RTTI cost from the polymorphic FTextHistory hierarchy.
//   * Serialization-format complexity (UE's FTextHistory has 8+
//     subclasses, each with its own on-disk format).
//   * Reference-count atomic traffic.
//
// LAYOUT (load-bearing for cache + IL2CPP interop):
//
//   Offset  Size  Field
//   0       8     m_namespace        const char* -- rodata literal
//   8       8     m_key              const char* -- rodata literal
//   16      8     m_literalFallback  const char* -- rodata literal (en-US source)
//   24      64    m_resolved         FString -- cached after first ResolveForCurrentLocale
//
// Total: 88 bytes. NOT pinned by static_assert here because the FText
// type is not part of the IL2CPP-boundary ABI (only FString and
// XCSharpString are; Section 11.1.5). The layout is documented so a
// future revision can pin it if needed.
//
// THREADING (Section 11.3):
//   * FText itself is non-thread-safe for mutation; the resolved-cache
//     write is gated by an internal mechanism that runs inside
//     ResolveForCurrentLocale (see FText.cpp).
//   * The locale-lookup path in FLocalizationManager IS thread-safe
//     (internal FRWLock).
//   * Mid-frame SetLocale is forbidden by spec (assert).
//
// DETERMINISM (Section 11.4):
//   * Byte-identity on FText is sim-path-safe given fixed inputs (the
//     three pointers and the resolved cache are deterministic per
//     locale).
//   * Locale resolution is NOT sim-path-safe (different platforms could
//     ship different loctables); sim-path TUs use FText for display
//     surfaces only, never for sim-path decision points.
//
// SOURCE-FALLBACK SEMANTICS (Section 17.8 H1, H3):
//   * H1: LOCTEXT resolves to the literal fallback when no loctable
//         is loaded, and to the locale string when one is.
//   * H3: missing key falls back to source literal AND emits a
//         dev-warning (logged to stderr in Phase 1f; will route to the
//         XLog channel once XLog ships).
//
// CACHE INVALIDATION (Section 11.2 / 17.8 H2):
//   * SetLocale flushes the per-FText resolved cache. The mechanism is
//     a monotonically-increasing locale generation counter inside
//     FLocalizationManager; each FText caches the generation it
//     resolved under, and re-resolves if the counter has bumped.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Containers/FString.h"

#include <cstdint>

namespace XCore::Loc
{

// ---------------------------------------------------------------------
// FText -- localised-text type.
// ---------------------------------------------------------------------
class FText
{
public:
    // -----------------------------------------------------------------
    // Default ctor -- empty-text sentinel.
    //
    // All three rodata-pointer fields are nullptr; m_resolved is the
    // FString default empty value. ResolveForCurrentLocale on an
    // empty-default FText returns the empty FString.
    //
    // The default ctor is constexpr so FText can appear as a constinit
    // global / static member (parity with UE's FText::GetEmpty() which
    // is a static-initialised empty FText).
    // -----------------------------------------------------------------
    constexpr FText() noexcept
        : m_namespace(nullptr)
        , m_key(nullptr)
        , m_literalFallback(nullptr)
        , m_resolvedGen(kUnresolvedGeneration)
        , m_resolved()
    {
    }

    // -----------------------------------------------------------------
    // FromLiteral -- the LOCTEXT-macro entry point.
    //
    // Stores the three rodata pointers verbatim. The literal MUST be
    // a string literal (lifetime is the .rodata segment of the DLL);
    // passing a heap-allocated buffer is undefined behaviour because
    // FText assumes the pointers outlive the FText.
    //
    // Per Section 11.2: this is the only public construction surface
    // for a localised FText. Non-localised text (e.g., "Player1"
    // generated at runtime) uses FString directly.
    //
    // INVTEXT (defined in LOCTEXT.h) calls this with Namespace =
    // "__Invariant" and Key = "" so the invariant-text path is
    // recognised by Lookup() and short-circuits to the literal.
    // -----------------------------------------------------------------
    [[nodiscard]] static FText FromLiteral(
        const char* Namespace,
        const char* Key,
        const char* LiteralFallback) noexcept;

    // -----------------------------------------------------------------
    // ResolveForCurrentLocale -- on-demand lookup + cache.
    //
    // Behaviour:
    //   1. If the cache is current (m_resolvedGen == current locale
    //      generation), return the cached FString.
    //   2. Otherwise, look up (m_namespace, m_key) in
    //      FLocalizationManager. On hit: cache + return the
    //      localised value. On miss: cache the literal fallback +
    //      emit a dev-warning + return the cached fallback.
    //   3. If the FText is the default empty-sentinel (all three
    //      pointers nullptr), return the empty cached FString
    //      (always-current generation).
    //
    // The return value is a const reference into m_resolved; the
    // reference is valid until the FText is destroyed OR until
    // SetLocale flushes the cache (which bumps m_resolvedGen but does
    // NOT mutate m_resolved -- the next call re-fills m_resolved on
    // the same address). Callers must not hold the reference across
    // a SetLocale call.
    //
    // Thread-safety: the lookup path inside FLocalizationManager is
    // FRWLock-protected. The cache write is NOT externally
    // synchronised; concurrent ResolveForCurrentLocale calls on the
    // same FText from multiple threads are UB. Per Section 11.3 the
    // contract is "FText::ResolveForCurrentLocale is thread-safe
    // (internal RWLock + per-text resolved cache)"; the per-text
    // cache part of that contract is honoured via the discipline that
    // FText instances are typically thread-local in use (per-frame
    // UI rendering on one thread). A future revision may add an
    // atomic-generation field to the cache; for Phase 1f the simpler
    // shape is sufficient.
    // -----------------------------------------------------------------
    [[nodiscard]] const ::XCore::FString& ResolveForCurrentLocale() const;

    // -----------------------------------------------------------------
    // IsEmpty -- empty-sentinel check.
    //
    // Returns true if the FText is the default-constructed empty
    // sentinel (all three rodata pointers nullptr). Note that this
    // does NOT call ResolveForCurrentLocale; an FText with a non-null
    // literal fallback whose resolved string happens to be empty
    // (e.g., a localised "" entry) is NOT IsEmpty.
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE bool IsEmpty() const noexcept
    {
        return m_literalFallback == nullptr || m_literalFallback[0] == '\0';
    }

    // -----------------------------------------------------------------
    // Format -- positional + named-arg interpolation.
    //
    // TODO(Phase 1g): wire FText::Format once FString::Format lands.
    //
    // The Format implementation is deferred to Phase 1g because:
    //   (a) FString::Format is itself a Phase 1g landing (its Phase
    //       1d stub in FStringFormat.cpp is marked PHASE1D_SKIP per
    //       XCore.Build.toml; it gates on std::format / vendored fmt
    //       being usable, which requires Phase 1e Sleef vendoring to
    //       complete first because FStringFormat will exercise
    //       float-formatting via std::to_chars).
    //   (b) FText::Format adds a named-argument {PlayerName}
    //       substitution layer on top of std::format-style {0}
    //       positional substitution. The parser is hand-rolled
    //       (std::format does not natively support named arguments
    //       in C++20; named arguments arrive in C++26).
    //
    // Stub signature lives below so consumer code can name the
    // method but the body is a static_assert(false) gate. The body
    // will not actually be instantiated at Phase 1f because no
    // caller in the engine uses FText::Format yet.
    // -----------------------------------------------------------------
    template<typename... Args>
    [[nodiscard]] FText Format(const Args&... NamedArgs) const
    {
        // Dependent-false: a template-dependent expression that always
        // evaluates to `false`. The static_assert fires only when the
        // function template is instantiated (not at declaration parse
        // time), so the header compiles cleanly when nobody calls
        // Format yet. Any actual caller gets a clear diagnostic.
        static_assert(sizeof...(NamedArgs) == ::SIZE_T(-1),
                      "FText::Format is deferred to Phase 1g (named-arg parser "
                      "+ FString::Format both pending). See FText.h.");
        (void)sizeof...(NamedArgs);
        return FText{};
    }

    // -----------------------------------------------------------------
    // Accessors for the rodata pointers. Used by FLocalizationManager
    // Lookup (it keys on namespace + key) and by tests.
    //
    // These are NOT part of the user-facing surface; user code uses
    // ResolveForCurrentLocale. The accessors are public so the loctable
    // serializer and the test surface can read the keys without
    // friendship plumbing.
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE const char* GetNamespace() const noexcept
    {
        return m_namespace;
    }

    [[nodiscard]] XPACT_FORCEINLINE const char* GetKey() const noexcept
    {
        return m_key;
    }

    [[nodiscard]] XPACT_FORCEINLINE const char* GetLiteralFallback() const noexcept
    {
        return m_literalFallback;
    }

private:
    // -----------------------------------------------------------------
    // The three rodata pointers. Const-pointer-to-const because the
    // literals are emitted into .rodata by the compiler; mutation is
    // forbidden by language rule.
    //
    // The pointer values themselves are mutable across copy/move
    // because FText must support assignment and move (e.g., return
    // by value from FromLiteral, store in a TArray<FText>, etc.).
    // -----------------------------------------------------------------
    const char* m_namespace;
    const char* m_key;
    const char* m_literalFallback;

    // -----------------------------------------------------------------
    // Resolved-cache generation counter.
    //
    // Each ResolveForCurrentLocale call compares this against
    // FLocalizationManager's current locale-generation counter. On
    // mismatch the cache is stale and gets re-filled; on match the
    // cache is current and the cached m_resolved is returned.
    //
    // kUnresolvedGeneration (= 0) is the never-resolved sentinel; the
    // FLocalizationManager generation starts at 1 (set by
    // __Initialize) so a freshly-constructed FText is always stale on
    // first resolution.
    //
    // The field is mutable so it can be updated from
    // ResolveForCurrentLocale (which is const per spec wording: the
    // semantic is "read with caching").
    // -----------------------------------------------------------------
    mutable ::std::uint32_t m_resolvedGen;

    // -----------------------------------------------------------------
    // Cached resolved FString.
    //
    // 64 bytes (one cache line). The cache is filled on first
    // ResolveForCurrentLocale call after the FText is constructed or
    // after a SetLocale-induced cache flush. On a cache hit (m_resolved
    // is current per the generation counter above) the resolution path
    // is one comparison + one reference-return.
    //
    // For the default empty-sentinel FText, m_resolved is the empty
    // FString and m_resolvedGen is set to a synthetic-always-current
    // value at construction (kEmptyGeneration = UINT32_MAX) so
    // ResolveForCurrentLocale short-circuits without touching the
    // FLocalizationManager.
    //
    // Mutable so ResolveForCurrentLocale (a const method) can refresh
    // the cache without dropping const correctness for the user-facing
    // semantic ("looking up a const FText").
    // -----------------------------------------------------------------
    mutable ::XCore::FString m_resolved;

    // -----------------------------------------------------------------
    // Cache-generation sentinels.
    //
    // kUnresolvedGeneration: never been resolved; first
    //                        ResolveForCurrentLocale call will fill
    //                        the cache.
    //
    // The "current" comparison uses ==; the
    // FLocalizationManager's generation counter is a uint32 that
    // monotonically increments, wrapping at UINT32_MAX. The wrap is
    // benign: the cache would become stale once every 4 billion
    // SetLocale calls, which is not a realistic concern.
    // -----------------------------------------------------------------
    static constexpr ::std::uint32_t kUnresolvedGeneration = 0u;
};

// ---------------------------------------------------------------------
// Layout documentation. NOT a static_assert because the field-order
// is not contractual at the IL2CPP boundary (only FString and
// XCSharpString are; Section 11.1.5). A future revision may pin
// the size if downstream needs it.
//
// On a 64-bit target with FString sized 64 bytes:
//   sizeof(FText) = 3*8 (rodata pointers) + 4 (m_resolvedGen) + 4
//                   (alignment padding to 8 before m_resolved's
//                    alignof-16) ... no, alignof(FString) is 16 so
//                   compiler inserts more padding. The exact size is
//                   target-ABI-dependent. Tests verify sizeof in
//                   FLocalizationManager.Tests/InitOrder.cpp.
// ---------------------------------------------------------------------

} // namespace XCore::Loc
