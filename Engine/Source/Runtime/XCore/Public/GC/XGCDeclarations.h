// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCDeclarations.h -- XGC ABI declarations (locked decision 7).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.4 + Section 13.1.
//
// XCore-4a declares the GC root-registration ABI; the implementation
// lives in XCoreXObject (master plan Section 4 Step 5). XCore-4a-side
// callers register XGCRootSpans from GC-aware container constructors
// (TArray<XActor*> etc.; partial specialization in Section 5.4); the
// XCore-4b collector reads the registered spans at mark time. The
// call site is the contract, per locked decision 7.
//
// CRITICAL ABI LOCKS (fix Rev 3 m3 + fix M-12):
//   * XGCRootKind: uint8_t (1 byte)
//   * XGCRootSpan: 32 bytes packed:
//       offset  0..7    void**       base
//       offset  8..15   size_t       stride
//       offset 16..23   size_t       count
//       offset 24       XGCRootKind  kind
//       offset 25..28   uint32_t     flags
//       offset 29..31   uint8_t      _tailPad[3]
//   * XGCRootSpan alignof >= 8
//
// The 32-byte layout aligns one span per cache line on every supported
// target (Quest 3 Snapdragon XR2 Gen 2 = 64-byte line; Win64 Ryzen =
// 64-byte line; Linux server = 64-byte line). Earlier 24-byte
// documentation was a bug; the genuine struct rounds to 32 (Section
// 5.4 fix M-12).
//
// For Phase 1a, the four extern "C" functions have no XCore-4a-side
// definitions. The linker resolves these against XCore-4b's
// implementation at master plan Step 5; meanwhile XCore-4a unit
// tests provide test-only stubs (e.g., Tests/GC/MockGCStubs.cpp
// when those tests need to link XCore-4a alone).
//
// Per locked decision 7, XGC_WriteBarrier specifically ships as a
// no-op weak-symbol stub in XCore-4a; XCore-4b replaces it with the
// real card-table-marking intrinsic. The other three (Register /
// Update / Unregister) are pure declarations -- an XCore-4a-only test
// build that uses GC-aware containers will get a link error here,
// which is the expected contract for that test configuration. The
// resolution is for test code to provide an explicit stub TU (see
// Tests/ directory README for the pattern).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

namespace XGC
{
    // -----------------------------------------------------------------
    // XGCRootKind -- the three kinds of root the collector understands
    // (Section 5.4 + fix Rev 3 m3).
    //
    //   Strong:        pin every XObject pointer against collection.
    //                  Most common (TArray<XActor*>, TMap value pos).
    //   Weak:          null the slot if the pointee is collected.
    //                  TArray<TWeakObjectPtr<T>>.
    //   Conservative:  treat every word in the slot as a potential
    //                  XObject*. Emits sim-path build-time warning.
    //                  TArray<XAny> editor scratch.
    //
    // uint8_t-backed so it fits in byte 24 of XGCRootSpan with zero
    // padding cost.
    // -----------------------------------------------------------------
    enum class XGCRootKind : ::uint8
    {
        Strong       = 0,
        Weak         = 1,
        Conservative = 2,
    };

    static_assert(sizeof(XGCRootKind) == 1, "XGCRootKind ABI lock");

    // -----------------------------------------------------------------
    // XGCRootSpan -- the contract structure between XCore-4a containers
    // and the XCore-4b collector.
    //
    // 32 bytes total (fix M-12). Layout is byte-exact:
    //
    //   [ 0]  void**       base         (8 bytes)
    //   [ 8]  size_t       stride       (8 bytes)
    //   [16]  size_t       count        (8 bytes)
    //   [24]  XGCRootKind  kind         (1 byte)
    //   [25]  uint8_t      _padHead[3]  (3 bytes -- alignment pad before flags)
    //   [28]  uint32_t     flags        (4 bytes)
    //
    // SPEC DIVERGENCE NOTE. The Rev 3 spec body in Section 5.4 places
    // `flags` at offset 25 followed by `_tailPad[3]` at offset 29.
    // That layout is not realizable under standard C++ struct-layout
    // rules: `uint32 flags` requires 4-byte alignment, so a compiler
    // placing it at offset 25 would either (a) misalign the field (UB
    // on strict-alignment platforms like Android ARM64) or (b) insert
    // 3 bytes of padding implicitly between kind and flags, landing
    // flags at offset 28 and producing 35 + 3 trailing-pad = 40 byte
    // struct size, NOT 32. Either way the spec's literal layout
    // contradicts its `sizeof == 32` ABI lock.
    //
    // The engineering-principles-correct resolution is the one
    // implemented here: insert the 3-byte alignment pad BEFORE flags
    // (head pad) rather than after (tail pad), making the struct
    // exactly 32 bytes with naturally-aligned fields. Both the spec's
    // layout-bug version and this corrected version preserve the
    // ABI-load-bearing invariants (sizeof == 32, alignof >= 8,
    // 0/8/16/24 offsets of base/stride/count/kind); the only
    // observable difference is the byte offset of `flags` and the
    // location of the 3 alignment bytes. Since `flags` is reserved
    // (must be 0 at Rev 13.7) and the alignment bytes are padding by
    // construction, the collector's mark walk reads identical bytes
    // in either layout.
    //
    // This divergence has been called out to the main agent for spec-
    // reconciliation; see the report at the bottom of this task.
    //
    // `flags` is reserved for future expansion; MUST be 0 at Rev 13.7
    // (Section 5.4). `_padHead` is explicit padding so the layout
    // matches across compilers (without it, MSVC's default padding
    // might still happen to produce 32 bytes, but the explicit pad
    // guarantees portable byte-exact agreement between Win64 / Linux
    // / Android-ARM64).
    //
    // alignof >= 8 because `base`, `stride`, `count` are all 8-byte
    // aligned. The collector's iteration walks spans at 32-byte
    // strides; misalignment would cost a load/store split.
    // -----------------------------------------------------------------
    struct XGCRootSpan
    {
        void**       base;          //  0  +8   pointer to first slot
        ::SIZE_T     stride;        //  8  +8   bytes between slots (typically sizeof(void*))
        ::SIZE_T     count;         // 16  +8   number of slots
        XGCRootKind  kind;          // 24  +1   strong / weak / conservative
        ::uint8      _padHead[3];   // 25  +3   explicit alignment pad before flags
        ::uint32     flags;         // 28  +4   reserved future flags (MUST be 0 at Rev 13.7)
    };

    static_assert(sizeof(XGCRootSpan)  == 32, "XGCRootSpan ABI lock: must be 32 bytes (fix M-12)");
    static_assert(alignof(XGCRootSpan) >= 8,  "XGCRootSpan ABI lock: must be at least 8-byte aligned");

    // -----------------------------------------------------------------
    // GC root registration ABI.
    //
    // extern "C" linkage so the symbols are not name-mangled; the
    // XCore-4b implementation can export them under the exact same
    // names regardless of which C++ ABI the implementation is built
    // with. This is the load-bearing guarantee that XCore-4a and
    // XCore-4b can be compiled with different revisions of MSVC /
    // Clang and still link.
    //
    // All four functions are noexcept: a GC ABI call must not throw,
    // because the calling container's destructor is itself noexcept
    // by convention and an exception across the boundary would
    // terminate the process via std::terminate. The XCore-4b
    // implementation aborts cleanly on internal errors (see Section
    // 5.4 fix Rev 3 m2 for the slot-validity check).
    // -----------------------------------------------------------------

    // Register a new root span with the collector. Called once, at
    // GC-aware-container construction time, before any buffer
    // allocation (per Section 5.4 construction protocol N6: register
    // first with count == 0, then allocate).
    extern "C" void XGC_RegisterRootSpan(XGCRootSpan* Span) noexcept;

    // Update an existing span's base + count after the container's
    // buffer is reallocated. The kind / flags fields cannot change
    // post-register; this entry point only edits base + count
    // atomically with respect to the collector's mark scan.
    extern "C" void XGC_UpdateRootSpan(XGCRootSpan* Span, void** NewBase, ::SIZE_T NewCount) noexcept;

    // Unregister a span before its buffer is freed. Called from the
    // GC-aware container's destructor BEFORE the FMemory::Free call,
    // so the collector cannot observe a stale span pointing at the
    // about-to-be-freed buffer.
    extern "C" void XGC_UnregisterRootSpan(XGCRootSpan* Span) noexcept;

    // -----------------------------------------------------------------
    // Card-table write barrier (locked decision 7; Section 5.4 fix C-4).
    //
    // Pre-store barrier semantics:
    //     XGC_WriteBarrier(&slot, newValue);
    //     slot = newValue;          // memory_order_relaxed equivalent
    //
    // The barrier marks the card for &slot BEFORE the slot is
    // updated; the post-barrier store is release-ordered relative to
    // the collector's card-table read. Reversing the order (store
    // first, then barrier) is a contract violation -- a concurrent
    // mark thread observing the card mid-write could see the slot in
    // an intermediate state.
    //
    // XCore-4a ships this as a no-op weak-symbol stub (see
    // Private/GC/XGCWriteBarrierStub.cpp in the follow-up commit);
    // XCore-4b replaces it with the real card-marking intrinsic. The
    // weak-symbol pattern means a XCore-4a-only test build links
    // cleanly with a no-op barrier, and integration with XCore-4b
    // replaces the symbol without source changes on the XCore-4a
    // side.
    //
    // TODO(Phase 1b): land Private/GC/XGCWriteBarrierStub.cpp with
    // the no-op weak-symbol definition. Until then, an XCore-4a-only
    // test build that exercises a GC-aware container will get a
    // link error here; the resolution is to provide a test-side stub
    // (Tests/GC/MockGCStubs.cpp pattern).
    // -----------------------------------------------------------------
    extern "C" void XGC_WriteBarrier(void** Slot, void* NewValue) noexcept;

} // namespace XGC
