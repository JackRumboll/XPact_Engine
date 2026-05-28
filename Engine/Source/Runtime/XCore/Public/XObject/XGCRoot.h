// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCRoot.h -- native-code GC root pinning (XCoreXObject Rev 4 §5.1, §5.2).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5 ("Write Barriers + GC Root Protocol")
// Section 5.1 ("Pinned roots in FXObjectArray") + Section 5.2 (the
// XGCRoot::AddRoot/RemoveRoot API). Phase 5.e deliverable.
//
// PURPOSE: a process-singleton static-API class that lets native C++
// code pin an XObject* as a GC root. Use this when the C++ caller
// holds an XObject reference in a stack / static / global slot that the
// XIL2CPP-emitted property-scan walker cannot see and the XGCRootSpan
// container ABI is not applicable (e.g., a bare pointer member on a
// non-reflected helper class, an editor tool window, a singleton like
// XEngine / XGameInstance, or the pre-CDO bootstrap).
//
// =====================================================================
// SEMANTIC: BIT-PIN (NOT REFCOUNT)
// =====================================================================
//
// XGCRoot uses the kRootPinnedBit (bit 2) on FXObjectArrayEntry::
// StateBits per Phase 5.c bit-layout pinning. The bit IS the per-object
// pin state -- there is NO per-XGCRoot refcount or registry list. The
// FXObjectArrayEntry already carries the canonical pin state in its
// 64-bit atomic StateBits word; XGCRoot is a thin static API surface
// over that bit.
//
// BIT-PIN vs REFCOUNT IS A DELIBERATE DESIGN CHOICE (Prime Directive).
// The spec body at §5.2 says "multiple AddRoot calls on the same
// object are reference-counted". XPact's Phase 5.e implementation
// DIVERGES: AddRoot returns false (no-op) if the object is already
// pinned; the bit is binary (pinned vs not). Rationale:
//
//   1. The CANONICAL multi-holder strong-reference protocol in XPact is
//      XStrongPtr<T> (Phase 5.c). XStrongPtr uses the 24-bit refcount
//      sub-field in StateBits bits 32..55; the collector treats any
//      non-zero refcount as root-pinned. A caller wanting "ref-counted
//      keep-alive" should hold an XStrongPtr<T>, not call AddRoot N
//      times.
//
//   2. The kRootPinnedBit is for the "I am THE owner of this pin" use
//      case (engine singletons, editor tool windows, pre-CDO bootstrap).
//      In those cases reference-counting is the WRONG semantic --
//      multiple owners of a singleton is a design smell (and the bit
//      flip is much cheaper than a CAS-loop refcount bump).
//
//   3. Splitting the two roles separates concerns cleanly:
//        * XStrongPtr<T>       -- ref-counted keep-alive (multi-holder).
//        * XGCRoot::AddRoot    -- binary pin (single-holder of an
//                                  externally-tracked slot).
//      A caller that mis-uses AddRoot for multi-holder semantics gets
//      a clear failure mode (the second AddRoot returns false) instead
//      of a silent footgun (the spec's reference-counted variant would
//      let two callers AddRoot, then one calls RemoveRoot and the
//      object stays pinned because the other's refcount is still > 0
//      -- which obfuscates ownership analysis).
//
// CAUTION DOCUMENTED ON THE API: clearing the bit while other
// AddRoot-holders exist is undefined-behaviour-equivalent (the
// object becomes collectable while the other caller still expects it
// pinned). For multi-holder scenarios use XStrongPtr<T>.
//
// =====================================================================
// API SURFACE
// =====================================================================
//
//   * AddRoot(XObject*)            -- set kRootPinnedBit; idempotent.
//                                      Returns true iff the bit
//                                      transitioned from clear to set
//                                      on this call.
//   * RemoveRoot(XObject*)         -- clear kRootPinnedBit. Returns
//                                      true iff the bit transitioned
//                                      from set to clear on this call.
//   * IsRooted(const XObject*)     -- diagnostic; returns true iff the
//                                      object's kRootPinnedBit is set.
//   * GetRootedCount()             -- diagnostic; iterates the
//                                      FXObjectArray and counts pinned
//                                      entries (SHARED lock; O(N)).
//   * ForEachRoot(Visitor)         -- mark-phase consumer; iterates
//                                      every pinned XObject exactly
//                                      once (SHARED lock; O(N)).
//
// CONCURRENCY: every API operates on the atomic StateBits word via
// CAS-loop. AddRoot / RemoveRoot are LOCK-FREE; they do NOT acquire
// the FXObjectArray's RWLock. The FXObjectArray slot identity is
// already stable for the entry's lifetime (per the never-relocate
// invariant from spec §3.3 + §9.1) so atomic CAS on the StateBits
// word is sufficient.
//
// IsRooted / GetRootedCount / ForEachRoot acquire the SHARED lock on
// FXObjectArray (matches ForEachObject; same pattern). ForEachRoot's
// visitor MUST be cheap (it runs under the shared lock).
//
// HOT-RELOAD: NO virtual methods. The class is non-constructible
// (all methods static; ctor + dtor + copy + move deleted).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FRWLock.h"
#include "XObject/FXObjectArray.h"           // kFXObjectArrayRootPinnedBit + Get()
#include "XObject/XObject.h"                  // XObject

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore
{

    // -----------------------------------------------------------------
    // XGCRoot -- process-singleton static-API for native-code root pin.
    //
    // All methods are static; the class is non-constructible. State
    // lives entirely in FXObjectArrayEntry::StateBits' kRootPinnedBit
    // (bit 2); XGCRoot is a thin wrapper that provides the documented
    // entry points + the diagnostic iteration helpers.
    // -----------------------------------------------------------------
    class XGCRoot
    {
    public:
        // =============================================================
        // AddRoot -- pin an XObject as a GC root.
        //
        // Sets kRootPinnedBit on the object's FXObjectArrayEntry.
        // Atomic CAS; lock-free. Idempotent: a second AddRoot on an
        // already-pinned object returns false + does NOT mutate any
        // state. The first AddRoot returns true.
        //
        // Pre-conditions:
        //   * Object MUST be a valid live XObject (registered with
        //     FXObjectArray via NewObject; InternalIndex > 0).
        //   * Object MAY be nullptr; nullptr returns false + no-op.
        //
        // Post-conditions:
        //   * Iff returns true: the kRootPinnedBit transitioned from
        //     clear to set on this call; the object will not be
        //     collected until the matching RemoveRoot.
        //   * Iff returns false: the object was already pinned (or was
        //     nullptr); the bit state is unchanged by this call.
        //
        // BIT-PIN SEMANTIC: AddRoot uses the binary kRootPinnedBit (bit
        // 2 of FXObjectArrayEntry::StateBits). For ref-counted keep-
        // alive use XStrongPtr<T> (Phase 5.c) which uses the 24-bit
        // refcount sub-field at bits 32..55.
        //
        // CONCURRENCY: atomic CAS on the StateBits word; no lock
        // acquired. Race-safe against concurrent AddRoot / RemoveRoot /
        // XStrongPtr AddRef / ReleaseRef on the same or different
        // entries.
        // =============================================================
        [[nodiscard]] static bool AddRoot(XObject* Object) noexcept;

        // =============================================================
        // RemoveRoot -- clear the kRootPinnedBit.
        //
        // Atomic CAS; lock-free. Returns true iff the bit transitioned
        // from set to clear on this call. A second RemoveRoot on an
        // already-unpinned object returns false + no-op.
        //
        // Pre-conditions:
        //   * Object MAY be nullptr; nullptr returns false + no-op.
        //   * If Object is non-null, it MUST be a valid live XObject
        //     (registered with FXObjectArray).
        //
        // CAUTION (multi-holder footgun): if multiple callers have
        // each called AddRoot on the same object, the FIRST RemoveRoot
        // call unpins the object -- the OTHER callers' "pin" expectation
        // is now violated. For multi-holder scenarios use XStrongPtr<T>
        // (Phase 5.c) which is refcount-based.
        //
        // CONCURRENCY: same as AddRoot. No lock acquired.
        // =============================================================
        [[nodiscard]] static bool RemoveRoot(XObject* Object) noexcept;

        // =============================================================
        // IsRooted -- diagnostic predicate.
        //
        // Returns true iff the object's kRootPinnedBit is currently
        // set. Returns false for nullptr or for an unregistered XObject
        // (InternalIndex == INDEX_NONE) or for an out-of-range index.
        //
        // Atomic load on the StateBits word; no lock. The result is a
        // snapshot at the load moment; production code should NOT
        // depend on the value being stable past the call.
        // =============================================================
        [[nodiscard]] static bool IsRooted(const XObject* Object) noexcept;

        // =============================================================
        // GetRootedCount -- diagnostic; count of pinned entries.
        //
        // Walks FXObjectArray under SHARED lock counting entries with
        // kRootPinnedBit set. O(N) in committed array capacity. Returns
        // an atomic snapshot count.
        //
        // Phase 5.e ships the linear-scan baseline. A cached counter
        // bumped on AddRoot / RemoveRoot is a future optimisation;
        // realistic root counts (1k - 5k per spec §4.8) make the scan
        // sub-millisecond at Foundation Prototype scale (~50k objects).
        // =============================================================
        [[nodiscard]] static ::std::size_t GetRootedCount() noexcept;

        // =============================================================
        // ForEachRoot -- mark-phase consumer; visit every pinned object.
        //
        // Iterates FXObjectArray under SHARED lock, calling the visitor
        // once per entry with kRootPinnedBit set. The visitor receives
        // `(int32_t InternalIndex, XObject* Object)`. The visitor MUST
        // be cheap (it runs under the shared lock; no allocation, no
        // lock-acquiring calls, no callback into FXObjectArray's
        // mutating API).
        //
        // The iteration is the Phase 5.g GC mark phase's primary
        // consumer (along with XGCRootSpanRegistry::
        // ForEachValidObjectInSpans). Phase 5.e ships the iteration
        // API; the collector wiring lands at Phase 5.g.
        //
        // Template body in-header so each call site monomorphises
        // (matches FXObjectArray::ForEachObject's discipline).
        // =============================================================
        template <typename Visitor>
        static void ForEachRoot(Visitor&& V) noexcept;

        // -------------------------------------------------------------
        // Non-constructible. All API is static.
        // -------------------------------------------------------------
        XGCRoot()                          = delete;
        ~XGCRoot()                         = delete;
        XGCRoot(const XGCRoot&)            = delete;
        XGCRoot(XGCRoot&&)                 = delete;
        XGCRoot& operator=(const XGCRoot&) = delete;
        XGCRoot& operator=(XGCRoot&&)      = delete;
    };

    // =================================================================
    // ForEachRoot template body.
    //
    // The implementation mirrors FXObjectArray::ForEachObject's pattern:
    // acquire SHARED lock, snapshot capacity, walk every committed
    // index skipping nullptr Object slots, test kRootPinnedBit on
    // StateBits, invoke visitor on hit.
    //
    // The visitor receives (int32_t InternalIndex, XObject* Object).
    // The Object pointer is the entry's bound XObject (the same value
    // FXObjectArray::GetObjectAtIndexUnchecked would return), so the
    // visitor can use it directly without further lookups.
    //
    // The body lives in-header to match FXObjectArray::ForEachObject's
    // pattern + to give the compiler the freedom to inline the visitor
    // body into the iteration loop (a vital perf win for the GC mark
    // hot path).
    // =================================================================
    template <typename Visitor>
    void XGCRoot::ForEachRoot(Visitor&& V) noexcept
    {
        // The FXObjectArray::ForEachObject template uses the array's
        // public surface to iterate live entries; we additionally
        // filter on kRootPinnedBit. The lock-acquire happens inside
        // ForEachObject -- we route through it rather than re-acquire
        // a separate lock so the iteration is consistent with the rest
        // of the FXObjectArray surface.
        //
        // The pin-bit check reads the StateBits word directly. The
        // FXObjectArray's never-relocate invariant (spec §3.3 + §9.1)
        // guarantees the entry pointer is stable for the entry's
        // lifetime, so reading StateBits under the shared lock is
        // race-free against the iteration but observes any concurrent
        // bit flip atomically.
        //
        // PER-ENTRY ATOMIC LOAD: we load StateBits with memory_order_
        // acquire so the visitor observes a happens-before-correct view
        // of any state updated alongside the bit flip (e.g., the
        // SerialNumber bump on the next AllocateEntry after a free).
        FXObjectArray& Array = FXObjectArray::Get();
        Array.ForEachObject(
            [&V, &Array](::int32 InternalIndex, XObject* Object) noexcept
            {
                // For Phase 5.e we route the kRootPinnedBit probe through
                // FXObjectArray::IsRootPinnedUnchecked, which reads the
                // entry's StateBits without re-acquiring the SHARED lock
                // (the iteration's ForEachObject visitor body is already
                // under it; the Unchecked variant exists precisely for
                // this nested-lock-avoidance use case).
                //
                // The visitor's invocation is conditional on the bit
                // being set; unpinned entries are silently skipped.
                if (Array.IsRootPinnedUnchecked(InternalIndex))
                {
                    V(InternalIndex, Object);
                }
            });
    }

} // namespace XCore
