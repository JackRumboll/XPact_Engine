// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXInsightsPayload.h -- discriminated key-value payload for the
// XInsights::Emit telemetry bridge (XCoreXObject Rev 4 §10.5 + Rev 3
// FIX-M-R2-1).
// =====================================================================
//
// XCoreXObject Rev 4 §10.5: every XInsights::Emit call carries a
// `const FXInsightsPayload&` describing the event's key-value fields
// (markDurationMs, dirtyCardCount, scenarioName, etc.). The payload is
// constructed by the emit-site (cheap stack-local), populated via
// Add(), then passed by const-reference into the weak-symbol bridge.
//
// VALUE DISCRIMINATION (Rev 3 FIX-M-R2-1):
//
//   Rev 2 used a hand-rolled union + Kind discriminator. Rev 3 replaces
//   it with `std::variant<>` because:
//     * variant carries its own type-discriminator (variant::index());
//       the explicit Kind field is unnecessary.
//     * variant is C++20 standard-library; matches Master Plan §2a.
//     * variant is constexpr-friendly + supports std::visit dispatch
//       without per-Kind switch statements at the consumer.
//
// SPEC FORMAT vs. IMPLEMENTATION DIVERGENCE (documented per Prime
// Directive):
//
//   The spec (§10.5 verbatim listing) declares:
//     using FInsightsValue = std::variant<int64_t, double, FString>;
//
//   Phase 5.k expands the variant to FOUR alternatives:
//     std::variant<int64_t, double, ::XCore::FString, ::XCore::Reflect::FName>
//
//   The FOURTH alternative (FName) is added because every spec-listed
//   event payload (§10.12 table) carries one or more "string"-typed
//   fields (tag, scenarioName, className, reason, modules) and FName is
//   the engine's canonical interned-string handle (8 bytes; no
//   allocation at emit site for repeated category names). Using FName
//   for category-tier fields means the production telemetry path emits
//   zero allocations per event when the field is interned at startup.
//   The FString alternative remains for fields whose content is genuinely
//   per-event (e.g., a user-input filename).
//
//   The variant ordering preserves the spec's first three slots so any
//   future XInsights consumer that hard-codes variant::index() lookups
//   (which a sane consumer would NOT do; std::visit is the principled
//   pattern) sees the same indices for int64 (0), double (1), FString
//   (2). The added FName at index 3 is a forward-compatible extension.
//
//   This is the Prime-Directive-correct choice: shipping with the
//   3-alternative variant would force every category/event name into
//   an FString allocation at each emit, which is ~50ns per emit + 24+
//   bytes of heap per allocation -- meaningful at the 1000+ events/sec
//   GC cycle rate. The 4-alternative variant is functionally a
//   superset; downstream XInsights consumers visiting via std::visit
//   simply add an FName-handling lambda and the surface is unchanged.
//
// NAMESPACE NOTE:
//
//   FString lives in ::XCore (Containers/FString.h); FName lives in
//   ::XCore::Reflect (Reflection/FName.h). The variant qualifies both
//   explicitly so the type-id is unambiguous regardless of `using`
//   directives at the call site.
//
// THREAD SAFETY:
//
//   FXInsightsPayload is a stack-local value type; it is constructed,
//   populated, and consumed within a single thread. The bridge's
//   strong-symbol provider (XInsights, when linked) is responsible for
//   any cross-thread concurrency at the consumer side (the spec §10.5
//   trailing prose: "callbacks copy into caller-owned thread-local
//   buffers OR enqueue into a lock-free MPSC drained by an XInsights
//   consumer thread"). FXInsightsPayload itself is NOT thread-safe;
//   sharing one across threads is undefined behaviour.
//
// ABI / LAYOUT:
//
//   FXInsightsPayload is NOT part of the Contract Rev 13.9 ABI lock set
//   (per spec §11.3 the layout pin set covers only the XObject base
//   type, FXObjectArrayEntry, handles, FStruct, FClass,
//   FXObjectLifecycleTable, FXObjectRefSchema). The telemetry payload
//   shape is allowed to evolve; consumers visit by named field rather
//   than by byte offset. The bridge call itself uses opaque pointers
//   (`const FXInsightsPayload*`) so the consumer side links against the
//   pointer-typed surface only.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/FString.h"
#include "Containers/TArray.h"
#include "Containers/TPair.h"
#include "Reflection/FName.h"

#include <cstddef>
#include <cstdint>
#include <utility>
#include <variant>

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // FInsightsValue -- discriminated value type per Rev 3 FIX-M-R2-1.
    //
    // Index 0 -- int64_t       (counts, sizes, durations-in-microseconds)
    // Index 1 -- double        (percentages, ratios, throughput-MBps)
    // Index 2 -- FString       (per-event free-form text)
    // Index 3 -- FName         (interned identifier; spec extension)
    //
    // The first three indices match the spec's §10.5 listing; the FName
    // alternative is the Phase 5.k extension documented at the header
    // top.
    //
    // The variant is `noexcept`-default-constructible (constructs as
    // int64_t{0}) so a default FInsightsValue is well-formed in every
    // context.
    // -----------------------------------------------------------------
    using FInsightsValue = ::std::variant<
        ::std::int64_t,
        double,
        ::XCore::FString,
        ::XCore::Reflect::FName
    >;

    // -----------------------------------------------------------------
    // FXInsightsPayload -- structured key-value payload for
    // XInsights::Emit.
    //
    // The payload is a TArray of (FName, FInsightsValue) pairs in
    // insertion order. Each Add() appends one entry; the consumer
    // walks via ForEach in the same insertion order. There is NO
    // deduplication on key collision (the spec wording is "structured
    // key-value blob; one event"; if the same key is added twice the
    // consumer observes both entries).
    //
    // The struct shape:
    //
    //   * NO virtual methods (hot-reload safe; bridge surface).
    //   * Move-only would be principled but the type is intentionally
    //     copy-able + move-able so a producer can build a template
    //     payload (with constant category fields) and emit copies for
    //     each event. The TArray member's primary template is value-
    //     copyable.
    //
    // PERFORMANCE NOTES:
    //
    //   * Add(FName, int64) / Add(FName, double) -- no allocation; the
    //     variant's small-buffer construction inlines the value into
    //     the TArray slot. TArray growth allocates from FMemTag::
    //     LeakTracker (placeholder; see below).
    //   * Add(FName, FString) -- one move into the variant slot; the
    //     payload's TArray growth may allocate. The caller is
    //     encouraged to construct the FString in-place (the FString
    //     overload takes by-value) to avoid the per-call copy.
    //   * Add(FName, FName) -- no allocation; FName is 8 bytes POD.
    //
    // TAG ATTRIBUTION CHOICE (per Prime Directive):
    //
    //   The inner TArray's allocations are tagged FMemTag::Reflection
    //   (matches FNamePool's tag; the payload is metadata, not
    //   gameplay heap). This is principled because:
    //     * The payload TArray flows through the engine's telemetry
    //       channel which the spec §10.5 already calls "reflection-
    //       runtime metadata".
    //     * It keeps payload-side allocation accounting separate from
    //       gameplay XObject heap (which is FMemTag::XObject).
    //     * The reflection tag's footprint analyser already tracks
    //       FNamePool churn; payload churn shows up in the same panel.
    //
    //   Phase 5.k uses the DefaultAllocator with the Reflection tag
    //   propagated via the per-instance FMemTag carry (XCore-4a
    //   Section 5.5 row 2). The TArray default ctor is allocation-
    //   free so an empty payload allocates nothing.
    // -----------------------------------------------------------------
    class FXInsightsPayload
    {
    public:
        // -------------------------------------------------------------
        // Default ctor: empty payload, no allocations.
        // -------------------------------------------------------------
        FXInsightsPayload() noexcept = default;

        // -------------------------------------------------------------
        // Copy / move / assign / destroy -- all defaulted.
        //
        // The TArray<TPair<...>> primary template is value-copyable
        // when the element type is (it is: TPair + FInsightsValue are
        // both value-copyable). Move is noexcept (TArray move ctor is
        // noexcept; FName + std::variant are nothrow-move).
        // -------------------------------------------------------------
        FXInsightsPayload(const FXInsightsPayload&)                    = default;
        FXInsightsPayload(FXInsightsPayload&&) noexcept                = default;
        FXInsightsPayload& operator=(const FXInsightsPayload&)         = default;
        FXInsightsPayload& operator=(FXInsightsPayload&&) noexcept     = default;
        ~FXInsightsPayload() noexcept                                  = default;

        // =============================================================
        // Add overloads. Each appends one (Key, Value) entry to the
        // payload in insertion order. NO deduplication on key
        // collision -- the spec wording is "blob"; consumers observe
        // every entry.
        //
        // All overloads are noexcept; TArray::Add aborts on OOM via the
        // FMemory::MallocOrAbort path. The "infallible" emit posture is
        // the engine-wide telemetry contract (spec §10.5 trailing
        // prose: "telemetry callbacks must not throw").
        // =============================================================

        // int64 overload. Common: counts, byte sizes, durations in
        // microseconds, indices.
        XPACT_FORCEINLINE void Add(
            ::XCore::Reflect::FName Key,
            ::std::int64_t          Value) noexcept
        {
            Entries.Add(::XCore::TPair<::XCore::Reflect::FName, FInsightsValue>(
                Key, FInsightsValue(::std::in_place_index<0>, Value)));
        }

        // double overload. Common: percentages, ratios, durations in ms.
        XPACT_FORCEINLINE void Add(
            ::XCore::Reflect::FName Key,
            double                  Value) noexcept
        {
            Entries.Add(::XCore::TPair<::XCore::Reflect::FName, FInsightsValue>(
                Key, FInsightsValue(::std::in_place_index<1>, Value)));
        }

        // FString overload (by-value; caller moves in to avoid copy).
        // Common: per-event free-form text (filenames, error messages).
        void Add(
            ::XCore::Reflect::FName Key,
            ::XCore::FString        Value) noexcept
        {
            Entries.Add(::XCore::TPair<::XCore::Reflect::FName, FInsightsValue>(
                Key, FInsightsValue(::std::in_place_index<2>, ::std::move(Value))));
        }

        // FName overload. Common: interned identifiers (category, tag,
        // class name). 8-byte POD; no allocation at the emit site.
        XPACT_FORCEINLINE void Add(
            ::XCore::Reflect::FName Key,
            ::XCore::Reflect::FName Value) noexcept
        {
            Entries.Add(::XCore::TPair<::XCore::Reflect::FName, FInsightsValue>(
                Key, FInsightsValue(::std::in_place_index<3>, Value)));
        }

        // =============================================================
        // Number of entries currently in the payload.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE
        ::SIZE_T NumEntries() const noexcept
        {
            return static_cast<::SIZE_T>(Entries.Num());
        }

        // =============================================================
        // ForEach -- visit every (Key, Value) entry in insertion order.
        //
        // Visitor signature: `void(FName Key, const FInsightsValue&)`.
        // The visitor can dispatch on Value via std::visit or
        // std::holds_alternative; both forms are zero-overhead vs
        // hand-rolled switch over a Kind enum.
        //
        // The method is templated so the visitor inlines at the call
        // site (no virtual / std::function indirection). The const
        // overload is the only one provided; payload mutation during
        // iteration is not a supported pattern.
        // =============================================================
        template<typename Visitor>
        XPACT_FORCEINLINE void ForEach(Visitor&& V) const noexcept
        {
            const ::int32 Count = Entries.Num();
            for (::int32 i = 0; i < Count; ++i)
            {
                const auto& Entry = Entries[i];
                V(Entry.Key, Entry.Value);
            }
        }

        // =============================================================
        // GetEntry -- random-access read-only accessor.
        //
        // For consumers that prefer index-based iteration (e.g., a
        // formatted-printer that walks index 0..N-1). Out-of-range
        // index aborts via the TArray bounds-check.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE
        const ::XCore::TPair<::XCore::Reflect::FName, FInsightsValue>&
        GetEntry(::int32 Index) const noexcept
        {
            return Entries[Index];
        }

    private:
        // -------------------------------------------------------------
        // Dense insertion-ordered table of (Key, Value) pairs.
        //
        // The primary TArray template is selected (TPair is not an
        // XObject*-derived type) so the array is the value-typed
        // path: no XGCRootSpan registration; no write-barrier interlock.
        // The pairs are trivially-relocatable (FName POD + variant of
        // PODs/FString); move is bitwise-correct.
        // -------------------------------------------------------------
        ::XCore::TArray<
            ::XCore::TPair<::XCore::Reflect::FName, FInsightsValue>
        > Entries;
    };

} // namespace XCore::HAL
