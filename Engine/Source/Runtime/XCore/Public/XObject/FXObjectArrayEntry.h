// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectArrayEntry.h -- 32-byte global-object-table entry
// (XCoreXObject Rev 4 §3.3 + §11.1 XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG).
// =====================================================================
//
// XCoreXObject Rev 4 Section 3.3 ("FXObjectArray (the global object
// table)") + Contract Rev 13.9 addendum tag
// `XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG`:
//
//   "FXObjectArrayEntry-v1: 32 bytes; Object@0, SerialNumber@8,
//    ClusterRootIndex@12, StateBits@16 (atomic; pending-destroy +
//    root-pinned + hot-reload + garbage bits), _reserved@24;
//    alignof = 8. Rev 3: rotating reachability flag moved to XObject
//    header per FIX-M-R2-3."
//
// PURPOSE: per-live-XObject metadata held in the global FXObjectArray
// (the equivalent of UE's FUObjectItem in FUObjectArray). One entry
// per live XObject; indexed by `XObject::InternalIndex`. The array is
// a single contiguous TArray-like structure (NOT UE's chunk-pointer-
// array indirection); entries are never relocated once allocated, so
// the per-entry pointer is stable for the entry's lifetime.
//
// FIELD ORDERING (per spec §3.3):
//
//   * Object@0           -- XObject* pointer (null = slot is free).
//                            First field for direct array-indexed
//                            access in the GC mark scan inner loop.
//   * SerialNumber@8     -- bumps on each slot reuse; XWeakPtr deref
//                            checks this value to detect dangling
//                            references. Cross-arch determinism
//                            invariant per FIX-A-CRIT-2 forbids
//                            sim-path reads; XObject::GetSerialNumber
//                            guards via XPACT_CHECK_SL at the read
//                            site.
//   * ClusterRootIndex@12 -- if this object is in a cluster (Phase 2;
//                              UE-MISS-2 / FIX-A-MIN-49 reservation),
//                              the InternalIndex of the cluster root.
//                              Pre-Phase-2 value is 0; the slot is
//                              part of the 32-byte ABI lock so the
//                              future cluster activation is non-
//                              breaking.
//   * StateBits@16       -- atomic uint64; carries pending-destroy +
//                            root-pinned + hot-reload-in-progress +
//                            garbage-shadow bits. The rotating
//                            reachability flag MOVED to XObject@36
//                            per Rev 3 FIX-M-R2-3 (the GC mark inner
//                            loop reads it directly off the XObject
//                            header for one fewer cache miss per
//                            mark).
//   * _reserved@24       -- 8 bytes reserved for future use (remote-
//                            handle bookkeeping, distributed-object
//                            shard id, ...). Part of the 32-byte ABI
//                            lock; consumers MUST NOT read or write
//                            this slot.
//
// REV 3 (FIX-M-R2-3): the rotating reachability flag (bits 0..2 of
// what was historically StateBits) MOVED to a new
// `XObject::ReachabilityFlag` slot at offset 36 of the XObject header.
// The motivation is one less cache miss per object marked in the GC
// mark loop (~50-200 cycles per object on Quest 3 ARM64; significant
// at 100k objects per scan). FXObjectArrayEntry retains only the
// state bits that don't participate in the mark inner loop:
//
//   * Bit 1: kPendingDestroyBit  (deferred-destroy queued)
//   * Bit 2: kRootPinnedBit      (mirror of EObjectFlags::MarkAsRootSet
//                                  for fast root-scan)
//   * Bit 3: kHotReloadInProgress (XLiveCoding swap in flight)
//   * Bit 7: kGarbageBit         (mirror of EObjectFlags::MarkedAsGarbage
//                                  for fast sweep-time check; per
//                                  FIX-A-HIGH-19)
//
// (The constants are defined inside FXObjectArray::*; Phase 5.c lands
// the array body. Phase 5.a only ships the entry struct itself.)
//
// HOT-RELOAD: the entry struct has NO virtual methods + IS trivially-
// destructible (the std::atomic destructor is trivial on every
// supported platform). The array body owns the SerialNumber-bump on
// slot release (see Phase 5.c).
//
// INDEX 0 IS RESERVED: a freshly-zeroed XWeakPtr / XObjectKey with
// InternalIndex == 0 represents "null"; the FXObjectArray never
// allocates index 0 to a real object (per spec §3.3 trailing prose).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <atomic>
#include <cstddef>          // offsetof
#include <cstdint>
#include <type_traits>      // is_standard_layout etc.

namespace XCore
{
    // Forward declaration. XObject is defined in XObject/XObject.h;
    // FXObjectArrayEntry references it as a pointer slot only, so the
    // forward declaration is sufficient (no circular include).
    class XObject;

    // -----------------------------------------------------------------
    // FXObjectArrayEntry -- 32-byte per-XObject GC-state entry.
    //
    // Per spec §3.3: alignas(8). NO virtual methods.
    //
    // The entry IS trivially destructible (std::atomic<uint64_t> has a
    // trivial destructor on every supported platform). It is NOT
    // trivially copyable (std::atomic<T> is non-copyable; the entry
    // must be referenced by pointer / reference once installed in the
    // array).
    //
    // Layout-locked at Contract Rev 13.9 per
    // XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG. Any byte-layout change
    // ABI-breaks the FXObjectArray storage shape and every XWeakPtr
    // deref that reads SerialNumber.
    // -----------------------------------------------------------------
    struct alignas(8) FXObjectArrayEntry
    {
        // --- Object pointer (offset 0; 8 bytes) ---
        //
        // Points at the live XObject in the heap. nullptr indicates a
        // free slot awaiting reuse. The slot field IS reassigned on
        // slot reuse (FXObjectArray::BindObject); the entry struct
        // itself is never relocated (entries are pointer-stable for
        // their array lifetime; see §9.1).
        XObject*                Object;             //  0  +8

        // --- SerialNumber (offset 8; 4 bytes) ---
        //
        // Per-slot generation counter; bumps on each
        // FXObjectArray::ReleaseSlot. An XWeakPtr / XObjectKey holds
        // {InternalIndex, SerialNumber}; deref checks the captured
        // SerialNumber against the current entry's SerialNumber to
        // detect dangling references (the slot was reused for a
        // different object since the weak handle was captured).
        //
        // SIMPATH NOTE (FIX-A-CRIT-2): sim-path TUs MAY NOT read this
        // field. The cross-arch determinism invariant is enforced at
        // the access site by XObject::GetSerialNumber (which carries
        // XPACT_CHECK_SL); this struct's field is public for the
        // array body's bookkeeping but should NOT be read directly
        // from sim-path code.
        ::std::uint32_t         SerialNumber;       //  8  +4

        // --- ClusterRootIndex (offset 12; 4 bytes) ---
        //
        // If this object is part of a cluster (Phase 2 feature; the
        // 16 bytes at XObject::_reservedCluster0/1 reserve the
        // per-object cluster bookkeeping), this field stores the
        // InternalIndex of the cluster's root object. The collector
        // treats cluster members as "marked if cluster root is
        // marked", which amortises mark cost across cluster size.
        //
        // Pre-Phase-2 value: 0 (no cluster membership). The slot is
        // part of the 32-byte ABI lock; consumers MUST NOT repurpose
        // it.
        ::std::uint32_t         ClusterRootIndex;   // 12  +4

        // --- StateBits (offset 16; 8 bytes; atomic) ---
        //
        // Atomic 64-bit word holding per-entry state bits that the
        // collector / hot-reload coordinator probe in tight loops.
        // The specific bit assignments (kPendingDestroyBit @ 1,
        // kRootPinnedBit @ 2, kHotReloadInProgress @ 3, kGarbageBit
        // @ 7) are defined alongside the FXObjectArray API (Phase
        // 5.c); this struct just owns the storage.
        //
        // REV 3 (FIX-M-R2-3): bits 0..2 were historically the rotating
        // reachability flag. Those bits MOVED to XObject@36 to save
        // one cache miss per object marked in the GC inner loop. The
        // remaining StateBits slots are for entry-level state that
        // does NOT participate in the mark hot path (deferred-destroy,
        // root-pin, hot-reload, garbage shadow).
        ::std::atomic<::std::uint64_t> StateBits;  // 16  +8

        // --- Reserved (offset 24; 8 bytes) ---
        //
        // Reserved for future use (remote-handle bookkeeping for
        // distributed-object support; per spec §3.3 trailing prose).
        // Part of the 32-byte ABI lock; consumers MUST NOT read or
        // write this slot. Pre-Phase-2 value: 0.
        ::std::uint64_t         _reserved;          // 24  +8

        // -------------------------------------------------------------
        // Construction.
        //
        // The default ctor zero-initialises every field. The atomic
        // member's default ctor is constexpr (C++20 [atomics.types.
        // operations]/2: the default constructor for `atomic<T>` is
        // constexpr and value-initialises the stored T). The struct's
        // default ctor is therefore eligible for constinit storage
        // (used by the FXObjectArray bootstrap that reserves
        // InternalIndex 0 as the "null sentinel" at process start).
        // -------------------------------------------------------------

        constexpr FXObjectArrayEntry() noexcept
            : Object(nullptr)
            , SerialNumber(0)
            , ClusterRootIndex(0)
            , StateBits(0)
            , _reserved(0)
        {
        }

        // FXObjectArrayEntry is non-copyable + non-movable: the
        // std::atomic member is non-copyable; the entry must be
        // referenced by pointer / reference once installed in the
        // FXObjectArray storage.
        FXObjectArrayEntry(const FXObjectArrayEntry&)            = delete;
        FXObjectArrayEntry(FXObjectArrayEntry&&)                 = delete;
        FXObjectArrayEntry& operator=(const FXObjectArrayEntry&) = delete;
        FXObjectArrayEntry& operator=(FXObjectArrayEntry&&)      = delete;

        // Destructor is trivial: std::atomic<uint64_t> + scalar fields
        // all have trivial destructors. Explicitly defaulted to
        // document the trivial-destructor contract.
        ~FXObjectArrayEntry() noexcept = default;
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.1 tag
    // XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG + spec §3.3 + §11.3
    // XPACT_VERIFY_XOBJECT_LAYOUT macro pin).
    //
    // Any byte-layout change breaks the FXObjectArray storage shape +
    // every XWeakPtr / XObjectKey deref that reads SerialNumber.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FXObjectArrayEntry) == 32,
                  "FXObjectArrayEntry ABI lock: 32 bytes (1/2 cache line) "
                  "per XCoreXObject Rev 4 §3.3 + Contract Rev 13.9 "
                  "XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG.");
    static_assert(alignof(FXObjectArrayEntry) == 8,
                  "FXObjectArrayEntry ABI lock: 8-byte alignment per "
                  "§3.3 alignas(8).");

    static_assert(offsetof(FXObjectArrayEntry, Object)           ==  0,
                  "FXObjectArrayEntry ABI lock: Object at offset 0");
    static_assert(offsetof(FXObjectArrayEntry, SerialNumber)     ==  8,
                  "FXObjectArrayEntry ABI lock: SerialNumber at offset 8");
    static_assert(offsetof(FXObjectArrayEntry, ClusterRootIndex) == 12,
                  "FXObjectArrayEntry ABI lock: ClusterRootIndex at offset 12");
    static_assert(offsetof(FXObjectArrayEntry, StateBits)        == 16,
                  "FXObjectArrayEntry ABI lock: StateBits at offset 16 "
                  "(Rev 3 per FIX-M-R2-3: rotating reachability flag MOVED "
                  "to XObject@36; StateBits retains pending-destroy + "
                  "root-pinned + hot-reload + garbage bits only).");
    static_assert(offsetof(FXObjectArrayEntry, _reserved)        == 24,
                  "FXObjectArrayEntry ABI lock: _reserved at offset 24");

    // Field-size locks. uint32 fields MUST be 4 bytes; the atomic
    // uint64 MUST be 8 bytes (lock-free on every supported platform
    // for 64-bit aligned access; std::atomic<uint64_t>::
    // is_always_lock_free holds on Win64 / Linux-x86_64 / Android-
    // ARM64).
    static_assert(sizeof(FXObjectArrayEntry::SerialNumber)     == 4,
                  "FXObjectArrayEntry ABI lock: SerialNumber is uint32 (4 bytes)");
    static_assert(sizeof(FXObjectArrayEntry::ClusterRootIndex) == 4,
                  "FXObjectArrayEntry ABI lock: ClusterRootIndex is uint32 (4 bytes)");
    static_assert(sizeof(FXObjectArrayEntry::StateBits)        == 8,
                  "FXObjectArrayEntry ABI lock: StateBits is atomic uint64 (8 bytes)");

    // Trait locks. The entry MUST be trivially-destructible (so
    // FXObjectArray storage destruction is cheap + reorder-safe).
    // The atomic member rules out trivial-copy + trivial-default-
    // construct, but trivial-destruct is preserved.
    static_assert(::std::is_trivially_destructible_v<FXObjectArrayEntry>,
                  "FXObjectArrayEntry must be trivially destructible "
                  "(FXObjectArray storage teardown depends on this).");

} // namespace XCore
