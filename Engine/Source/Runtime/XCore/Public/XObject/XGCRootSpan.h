// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCRootSpan.h -- range-span GC root registration
// (XCoreXObject Rev 4 §5.3 + Rev 2 FIX-A-MED-35 + Rev 2 FIX-A-HIGH-11).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5.3 ("GC root protocol"). Phase 5.e
// deliverable: the XGCRootSpan ABI + the process-singleton
// XGCRootSpanRegistry that owns the active-span table.
//
// PURPOSE: containers that hold XObject pointers (TArray<XPtr<T>>,
// TMap<K, XPtr<V>>, TSet<XPtr<T>>, the IL2CPP-emitted List<object>
// scaffolding) register their backing storage with this registry at
// construction time. The GC mark phase walks every active span and
// pushes the referenced XObjects into the gray queue.
//
// =====================================================================
// SPAN KIND -- TYPED vs CONSERVATIVE
// =====================================================================
//
// Per spec §5.3 + Rev 2 FIX-A-HIGH-11:
//
//   * kObject  -- TYPED span. Every slot in [BaseAddress, BaseAddress +
//                  ByteLength) is a `XObject*`-shaped 8-byte word AT a
//                  multiple of ElementStride. The GC mark phase reads
//                  each slot as a raw XObject* and pushes it (after
//                  nullptr filtering). NO per-slot validation is
//                  performed -- the typed contract guarantees every
//                  non-null slot is a live XObject.
//
//   * kConservative -- CONSERVATIVE span. Every aligned 8-byte word in
//                       [BaseAddress, BaseAddress + ByteLength) is a
//                       CANDIDATE XObject*. The GC mark phase reads each
//                       word and validates via the spec §5.3 four-gate
//                       order (heap-range -> FXObjectArray index ->
//                       entry-bind -> SerialNumber match) before
//                       accepting the candidate as a root. NEVER
//                       deref the candidate before validation.
//
// Conservative spans cost ~10-20 cycles per word vs ~2-3 cycles per
// word for typed spans (per spec §5.3 trailing prose). The Conservative
// kind exists for the IL2CPP-emitted `List<object>` registration where
// the C# transpiler cannot statically determine which slots hold
// XObject references. Per XCoreXObject Rev 4 §1.1: this is the
// explicit per-mutation root-register / unregister cost the Master
// Plan acknowledges; XIL2CPP emits a build-time warning when a
// Conservative span appears on a sim-path TU per Phase 5.e's
// XPACT_GC_CONSERVATIVE_WARN macro (XPactGCConservativeWarn.h).
//
// =====================================================================
// XGCRootSpan ABI (32 bytes; XPACT_XGC_ROOTSPAN_LAYOUT_TAG)
// =====================================================================
//
//   * BaseAddress@0   (8 bytes) -- pointer to the first slot.
//   * ByteLength@8    (8 bytes) -- total byte length of the span.
//   * ElementStride@16(8 bytes) -- bytes between consecutive slots.
//                                    For kObject typed spans: 8 (one
//                                    XObject* per slot). For
//                                    Conservative spans: 8 (every
//                                    aligned 8-byte word is a candidate
//                                    -- larger strides skip candidates,
//                                    which is wrong for the conservative
//                                    contract).
//   * Kind@24         (1 byte)  -- EXGCRootSpanKind (kObject=0,
//                                   kConservative=1). uint8 backing for
//                                   compactness; 7 bytes of pad follow.
//   * _pad@25         (7 bytes) -- pad to 32-byte total.
//
//   Total: 32 bytes; alignof = 8 (matches the 8-byte fields).
//
// =====================================================================
// REGISTRY API
// =====================================================================
//
//   * AddSpan(const XGCRootSpan&)        -- register a span; returns
//                                            a handle (int32) for
//                                            later RemoveSpan. Acquires
//                                            EXCLUSIVE lock.
//   * RemoveSpan(int32 Handle)           -- unregister a span. Idempotent
//                                            on an already-removed handle.
//                                            Acquires EXCLUSIVE lock.
//   * ForEachValidObjectInSpans(Visitor) -- iterate every span; for
//                                            kObject typed spans visit
//                                            every non-null XObject*
//                                            slot; for kConservative
//                                            spans validate each
//                                            candidate via spec §5.3
//                                            order and visit only valid
//                                            candidates. SHARED lock
//                                            acquired (matches the rest
//                                            of the engine's iteration
//                                            discipline).
//   * GetSpanCount()                     -- diagnostic; count of active
//                                            spans.
//   * GetConservativeSpanCount()         -- diagnostic; count of
//                                            kConservative spans.
//
// CONCURRENCY: FRWLock-protected. AddSpan / RemoveSpan acquire
// EXCLUSIVE; the iteration / counters acquire SHARED. Per the engine-
// wide lock-discipline contract, the registry never holds its own
// lock while calling out to any other lock-holding subsystem.
//
// HOT-RELOAD: NO virtual methods. The singleton is a function-local
// static; the registered spans survive across hot-reload of downstream
// modules (only spans owned by an unloaded module are explicitly
// RemoveSpan'd by the hot-reload coordinator).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FRWLock.h"
#include "XObject/FXObjectAllocator.h"        // IsHeapAddress for Conservative validation
#include "XObject/FXObjectArray.h"             // FXObjectArray for entry-bind + SerialNumber validation
#include "XObject/XObject.h"                    // XObject* slot type

#include <atomic>
#include <cstddef>          // offsetof
#include <cstdint>
#include <type_traits>      // is_standard_layout etc.

namespace XCore
{

    // -----------------------------------------------------------------
    // EXGCRootSpanKind -- typed vs conservative root span discriminator.
    //
    // Backed by uint8_t for 1-byte storage in the XGCRootSpan struct.
    // The enumerator values are wire-compatible with the XIL2CPP-
    // emitted Conservative scaffolding's enum encoding.
    // -----------------------------------------------------------------
    enum class EXGCRootSpanKind : ::std::uint8_t
    {
        // kObject: typed XObject* span. Every slot is a real XObject*;
        // the mark phase visits each non-null slot directly. ~2-3
        // cycles per slot per spec §5.3.
        kObject = 0,

        // kConservative: candidate-XObject* span. Every aligned 8-byte
        // word is a CANDIDATE; the mark phase validates each via the
        // spec §5.3 four-gate order (heap-range -> FXObjectArray index
        // -> entry-bind -> SerialNumber match) before treating it as a
        // root. ~10-20 cycles per word per spec §5.3 trailing prose.
        kConservative = 1,
    };

    // -----------------------------------------------------------------
    // XGCRootSpan -- 32-byte range-span root registration record.
    //
    // alignas(8) -- matches the 8-byte pointer + uint64 fields. NO
    // virtual methods. The struct is trivially-copyable + trivially-
    // destructible (POD-like) so the registry can store it by value in
    // its TArray-backed table.
    //
    // Layout-locked at Contract Rev 13.9 via the Phase 5.e ABI tag
    // XPACT_XGC_ROOTSPAN_LAYOUT_TAG.
    // -----------------------------------------------------------------
    struct alignas(8) XGCRootSpan
    {
        // --- BaseAddress (offset 0; 8 bytes) ---
        //
        // Pointer to the first slot of the span. For a container
        // backing TArray<XPtr<T>> this is the data() pointer. May be
        // nullptr for an empty span (ByteLength == 0); the iteration
        // visitor treats nullptr-base + non-zero ByteLength as a
        // programmer error (defensive XPACT_CHECK).
        const void*               BaseAddress;       //  0  +8

        // --- ByteLength (offset 8; 8 bytes) ---
        //
        // Total byte length of the span. For a typed span of N XObject*
        // slots, ByteLength = N * 8. For a conservative span over a
        // C# List<object> backing array, ByteLength = capacity * 8.
        //
        // Zero ByteLength means "empty span"; AddSpan accepts these
        // and the iteration treats them as no-op. They're useful for
        // containers that pre-register their span and then resize the
        // backing storage; on each resize the container re-registers
        // with the updated ByteLength.
        ::std::size_t             ByteLength;        //  8  +8

        // --- ElementStride (offset 16; 8 bytes) ---
        //
        // Bytes between consecutive slot starts. For kObject typed
        // spans: 8 (one XObject* per slot). For kConservative spans:
        // 8 (every aligned 8-byte word is a candidate; larger strides
        // would skip candidates and break the conservative contract).
        //
        // Non-8 strides are reserved for future use (e.g., a Conservative
        // scan that knows its container holds an 8-byte XObject* at a
        // 16-byte stride within a {XObject*; meta} pair structure).
        // Phase 5.e enforces ElementStride == 8 via an XPACT_CHECK at
        // AddSpan; non-8 strides return a sentinel handle (-1) +
        // diagnostic in Dev/Debug.
        ::std::size_t             ElementStride;     // 16  +8

        // --- Kind (offset 24; 1 byte) ---
        //
        // EXGCRootSpanKind discriminator. uint8 backing per the §5.3
        // ABI table at the spec body.
        EXGCRootSpanKind          Kind;              // 24  +1

        // --- _pad (offset 25; 7 bytes) ---
        //
        // Pad to 32-byte total. Consumers MUST NOT read or write this
        // region. Initialised to zero by the default ctor.
        ::std::uint8_t            _pad[7];           // 25  +7

        // -------------------------------------------------------------
        // Construction.
        //
        // Default ctor zero-initialises every field. constexpr so a
        // XGCRootSpan can sit in constinit storage if a future container
        // wants to pre-emit its span as compile-time data. The 7-byte
        // pad MUST be zeroed for the bytewise registry-table operations
        // to be deterministic (matters at Contract Rev 13.9 ABI lock).
        // -------------------------------------------------------------
        constexpr XGCRootSpan() noexcept
            : BaseAddress(nullptr)
            , ByteLength(0)
            , ElementStride(0)
            , Kind(EXGCRootSpanKind::kObject)
            , _pad{0, 0, 0, 0, 0, 0, 0}
        {
        }

        // Convenience constructor: typed kObject span over a contiguous
        // XObject** array of `Count` slots.
        constexpr XGCRootSpan(
            const XObject* const*  InBase,
            ::std::size_t          InCount,
            EXGCRootSpanKind       InKind = EXGCRootSpanKind::kObject) noexcept
            : BaseAddress(static_cast<const void*>(InBase))
            , ByteLength(InCount * sizeof(XObject*))
            , ElementStride(sizeof(XObject*))
            , Kind(InKind)
            , _pad{0, 0, 0, 0, 0, 0, 0}
        {
        }

        // POD-friendly: trivial copy + move + destruct.
        XGCRootSpan(const XGCRootSpan&)             = default;
        XGCRootSpan(XGCRootSpan&&)                  = default;
        XGCRootSpan& operator=(const XGCRootSpan&)  = default;
        XGCRootSpan& operator=(XGCRootSpan&&)       = default;
        ~XGCRootSpan()                              = default;
    };

    // ---------------------------------------------------------------------
    // ABI locks (XPACT_XGC_ROOTSPAN_LAYOUT_TAG per XCoreXObject Rev 4
    // §11.1 / §11.2 + the Phase 5.e contract addendum).
    // ---------------------------------------------------------------------
    static_assert(sizeof(XGCRootSpan) == 32,
                  "XGCRootSpan ABI lock: 32 bytes per XCoreXObject Rev 4 "
                  "§5.3 (Phase 5.e).");
    static_assert(alignof(XGCRootSpan) == 8,
                  "XGCRootSpan alignment ABI lock: 8-byte aligned.");
    static_assert(offsetof(XGCRootSpan, BaseAddress)    ==  0,
                  "XGCRootSpan ABI lock: BaseAddress at offset 0.");
    static_assert(offsetof(XGCRootSpan, ByteLength)     ==  8,
                  "XGCRootSpan ABI lock: ByteLength at offset 8.");
    static_assert(offsetof(XGCRootSpan, ElementStride)  == 16,
                  "XGCRootSpan ABI lock: ElementStride at offset 16.");
    static_assert(offsetof(XGCRootSpan, Kind)           == 24,
                  "XGCRootSpan ABI lock: Kind at offset 24.");
    static_assert(offsetof(XGCRootSpan, _pad)           == 25,
                  "XGCRootSpan ABI lock: _pad at offset 25 (7 bytes).");
    static_assert(sizeof(XGCRootSpan::Kind) == 1,
                  "XGCRootSpan ABI lock: Kind is uint8 (EXGCRootSpanKind).");
    static_assert(::std::is_trivially_copyable_v<XGCRootSpan>,
                  "XGCRootSpan must be trivially copyable (the registry "
                  "stores spans by value in its TArray-backed table).");
    static_assert(::std::is_trivially_destructible_v<XGCRootSpan>,
                  "XGCRootSpan must be trivially destructible.");

    // -----------------------------------------------------------------
    // Tunables for the XGCRootSpanRegistry.
    // -----------------------------------------------------------------

    // Sentinel handle returned by AddSpan on failure (invalid span
    // shape, ElementStride != 8, etc.). Negative values are guaranteed
    // not to collide with a real handle (handles are >= 0).
    inline constexpr ::int32 kXGCRootSpanInvalidHandle = -1;

    // Initial slot-table capacity. Spans pre-allocate this many slots
    // at registry construction; subsequent grows double the capacity
    // (under EXCLUSIVE lock). Typical project: <500 active spans (per
    // spec §4.8 acceptance criteria); 64 is a sensible starting size
    // that almost never needs to grow.
    inline constexpr ::int32 kXGCRootSpanInitialCapacity = 64;

    // -----------------------------------------------------------------
    // XGCRootSpanRegistry -- process-singleton root-span table.
    //
    // Stores active spans in a slot table indexed by handle. Each slot
    // is a {Active, Span} pair; RemoveSpan flips Active to false +
    // pushes the slot index onto a free-list LIFO so the next AddSpan
    // reuses the slot.
    //
    // No copy, no move, no public ctor.
    // -----------------------------------------------------------------
    class XGCRootSpanRegistry
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        //
        // Magic-static. The ctor pre-allocates the slot table at
        // kXGCRootSpanInitialCapacity entries via FMemory::Malloc
        // (FMemTag::Reflection). Failures abort.
        // -------------------------------------------------------------
        [[nodiscard]] static XGCRootSpanRegistry& Get() noexcept;

        // -------------------------------------------------------------
        // Test-only reset. Drops every active span; resets counters.
        // ONLY used by the Phase 5.e test suite.
        // -------------------------------------------------------------
        void __ResetForTests() noexcept;

        // =============================================================
        // AddSpan -- register a span.
        //
        // Returns a non-negative handle on success. Returns
        // kXGCRootSpanInvalidHandle on shape error (ElementStride != 8
        // is rejected; ByteLength % ElementStride != 0 is rejected;
        // nullptr Base with non-zero ByteLength is rejected).
        //
        // The Span value is COPIED into the registry's slot table; the
        // caller's stack-local Span object is free to be modified or
        // destroyed after AddSpan returns. The handle remains valid
        // until the matching RemoveSpan.
        //
        // EXCLUSIVE lock acquired.
        // =============================================================
        ::int32 AddSpan(const XGCRootSpan& Span) noexcept;

        // =============================================================
        // RemoveSpan -- unregister a span.
        //
        // Marks the slot inactive + pushes its index onto the free
        // list. Idempotent on an already-removed handle (no-op +
        // returns).
        //
        // EXCLUSIVE lock acquired.
        // =============================================================
        void RemoveSpan(::int32 Handle) noexcept;

        // =============================================================
        // ForEachValidObjectInSpans -- mark-phase consumer.
        //
        // Iterates every active span under SHARED lock. For kObject
        // typed spans, the visitor receives every non-null XObject*
        // slot in the span. For kConservative spans, every aligned
        // 8-byte word in the span is validated via the spec §5.3
        // four-gate order (heap-range -> FXObjectArray index -> entry-
        // bind -> SerialNumber match); only validated candidates are
        // visited.
        //
        // The visitor signature is `void (XObject* Object)`. The
        // visitor MUST be cheap (it runs under the shared lock; no
        // allocation, no lock-acquiring calls into XGCRootSpanRegistry
        // or FXObjectArray's mutating API).
        //
        // The visitor MAY be called multiple times with the same
        // XObject* if the same object appears in multiple spans -- the
        // GC mark phase handles dedup via the rotating reachability
        // flag (per spec §4.0 + Rev 3 FIX-M-R2-3); XGCRootSpanRegistry
        // does NOT dedup here.
        //
        // Template body in-header so each call site monomorphises.
        // =============================================================
        template <typename Visitor>
        void ForEachValidObjectInSpans(Visitor&& V) const noexcept;

        // =============================================================
        // Diagnostic counts.
        // =============================================================

        // Active span count (across both kObject + kConservative).
        // Atomic read; no lock.
        [[nodiscard]] ::std::size_t GetSpanCount() const noexcept;

        // Active kConservative span count. SHARED lock; O(N) over
        // active spans. Diagnostic API only (production sim-path
        // monitoring would track this via per-AddSpan counter
        // increments; Phase 5.e ships the simple scan).
        [[nodiscard]] ::std::size_t GetConservativeSpanCount() const noexcept;

        // =============================================================
        // ValidateConservativeCandidate -- public wrapper around the
        // private four-gate validator.
        //
        // The free function `XCore::ValidateConservativeCandidate`
        // declared in XGCConservativeValidate.h is the consumer-facing
        // API; it delegates to this method. Exposed publicly (rather
        // than via `friend`) because the API surface is already
        // intentionally exposed through the free function -- friending
        // would add coupling without information-hiding benefit.
        //
        // No lock: the four-gate validator itself acquires the
        // FXObjectArray SHARED lock for the heap-range / index /
        // entry-bind / SerialNumber probes.
        // =============================================================
        [[nodiscard]] XObject* ValidateConservativeCandidate(
            const void* Candidate) const noexcept;

    private:
        XGCRootSpanRegistry() noexcept;
        ~XGCRootSpanRegistry() noexcept;

        XGCRootSpanRegistry(const XGCRootSpanRegistry&)            = delete;
        XGCRootSpanRegistry(XGCRootSpanRegistry&&)                 = delete;
        XGCRootSpanRegistry& operator=(const XGCRootSpanRegistry&) = delete;
        XGCRootSpanRegistry& operator=(XGCRootSpanRegistry&&)      = delete;

        // -------------------------------------------------------------
        // FSlot -- one registry table entry.
        //
        // Active=false slots are on the free-list (m_freeListHead /
        // m_freeListNext per-slot) so AddSpan can re-use slot indices
        // for handle stability. Slots are NEVER relocated (handle ==
        // slot index; the index is stable across grows).
        // -------------------------------------------------------------
        struct FSlot
        {
            XGCRootSpan Span;
            bool        Active;
            // Free-list next pointer: stored as int32 in m_freeListNext
            // (separate array) to keep FSlot trivially-copyable. -1
            // sentinels end-of-chain.
        };

        // -------------------------------------------------------------
        // Internal helpers.
        //
        // EnsureCapacityUnderLock: grow the slot table when the bump
        // head reaches m_slotCapacity. Doubles capacity via FMemory::
        // Realloc. Pre-condition: caller holds m_lock EXCLUSIVE.
        //
        // ValidateConservativeCandidateInternal: the per-word validator
        // used by ForEachValidObjectInSpans on Conservative spans. Pulls
        // the four-gate check out of the template body so the per-T
        // instantiation cost stays bounded.
        // -------------------------------------------------------------
        void EnsureCapacityUnderLock() noexcept;
        XObject* ValidateConservativeCandidateInternal(
            const void* Candidate) const noexcept;

        // -------------------------------------------------------------
        // State.
        //
        // m_slots             -- dense FSlot array; index == handle.
        // m_freeListNext      -- per-slot next-free-handle chain.
        // m_slotCapacity      -- m_slots / m_freeListNext capacity.
        // m_bumpHead          -- next bump-allocated handle (== current
        //                         m_slotCount when free list is empty).
        // m_freeListHead      -- LIFO of recently-removed slot indices;
        //                         -1 means empty.
        // m_activeCount       -- live (Active == true) span count;
        //                         atomic so GetSpanCount can read
        //                         without lock.
        // m_lock              -- RWLock; SHARED for iteration / reads;
        //                         EXCLUSIVE for AddSpan / RemoveSpan /
        //                         grow.
        // -------------------------------------------------------------
        FSlot*                          m_slots;
        ::int32*                        m_freeListNext;
        ::int32                         m_slotCapacity;
        ::int32                         m_bumpHead;
        ::int32                         m_freeListHead;
        ::std::atomic<::std::int32_t>   m_activeCount;
        mutable ::XCore::HAL::FRWLock   m_lock;

        // -------------------------------------------------------------
        // Friend: the ForEachValidObjectInSpans template body needs
        // direct access to m_slots / m_bumpHead / m_lock. Phase 5.e
        // keeps the access friend-restricted to the registry's own
        // template body (no other class is friended).
        //
        // The friend declaration is a TEMPLATE FRIEND on the public
        // template member; this is the standard C++ pattern for
        // exposing in-header template bodies to private state.
        // -------------------------------------------------------------
    };

    // =================================================================
    // ForEachValidObjectInSpans template body.
    //
    // Pattern:
    //   1. Acquire SHARED lock.
    //   2. Snapshot m_bumpHead via local copy (lock-protected; no need
    //      for atomic).
    //   3. Walk every slot index [0, m_bumpHead); skip inactive slots.
    //   4. For each active slot:
    //      * kObject: walk every 8-byte slot in the span; load each as
    //        XObject*; skip nullptr; invoke visitor.
    //      * kConservative: walk every aligned 8-byte word; validate
    //        via ValidateConservativeCandidateInternal; invoke visitor
    //        on validated candidates.
    //
    // The lock is held for the entire iteration; the visitor MUST be
    // cheap and MUST NOT call into the registry's mutating API. The
    // FXObjectArray::IsRootPinnedUnchecked + IsHeapAddress probes
    // called from ValidateConservativeCandidateInternal acquire their
    // own SHARED locks; both are recursive-acquirable on Win64 SRWLock
    // (the SHARED side is re-entrant) so the nesting is safe.
    // =================================================================
    template <typename Visitor>
    void XGCRootSpanRegistry::ForEachValidObjectInSpans(Visitor&& V) const noexcept
    {
        ::XCore::HAL::FScopedReadLock ReadLock(m_lock);
        const ::int32 LocalBumpHead = m_bumpHead;
        for (::int32 SlotIndex = 0; SlotIndex < LocalBumpHead; ++SlotIndex)
        {
            const FSlot& Slot = m_slots[SlotIndex];
            if (!Slot.Active)
            {
                continue;
            }
            const XGCRootSpan& Span = Slot.Span;
            if (Span.ByteLength == 0 || Span.BaseAddress == nullptr)
            {
                continue;
            }
            // Walk the span. ElementStride == 8 is enforced at AddSpan
            // time; we trust the invariant here.
            const ::std::size_t NumSlots = Span.ByteLength / Span.ElementStride;
            const char* const  BaseBytes = static_cast<const char*>(Span.BaseAddress);

            if (Span.Kind == EXGCRootSpanKind::kObject)
            {
                // Typed: each slot is an XObject*. Load + null-filter +
                // visit.
                for (::std::size_t I = 0; I < NumSlots; ++I)
                {
                    XObject* const* SlotPtr =
                        reinterpret_cast<XObject* const*>(
                            BaseBytes + I * Span.ElementStride);
                    XObject* Object = *SlotPtr;
                    if (Object != nullptr)
                    {
                        V(Object);
                    }
                }
            }
            else
            {
                // Conservative: each word is a CANDIDATE. Validate via
                // the spec §5.3 four-gate order; visit only validated
                // candidates.
                for (::std::size_t I = 0; I < NumSlots; ++I)
                {
                    const void* const* SlotPtr =
                        reinterpret_cast<const void* const*>(
                            BaseBytes + I * Span.ElementStride);
                    const void* Candidate = *SlotPtr;
                    if (Candidate == nullptr)
                    {
                        continue;
                    }
                    XObject* Validated =
                        ValidateConservativeCandidateInternal(Candidate);
                    if (Validated != nullptr)
                    {
                        V(Validated);
                    }
                }
            }
        }
    }

} // namespace XCore
