// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCWriteBarrier.h -- the card-table + SATB pre-store write barrier
// (XCoreXObject Rev 4 §4.3 + §4.5 + §5.2 + §10.7).
// =====================================================================
//
// XCoreXObject Rev 4 §4.3 (SATB barrier semantics) + §4.5 (card-table
// mark) + §5.2 (write-barrier emission) + §10.7 (XPACT_GC_STORE
// pattern for XIL2CPP). Replaces XCore-4a's weak-symbol XGC_WriteBarrier
// stub (Private/GC/XGCWriteBarrierStub.cpp) with the strong-symbol
// implementation per spec §13.1 trailing prose.
//
// =====================================================================
//
// API SHAPE (per spec §5.2 + §10.7):
//
//   * XPACT_GC_STORE(Slot, NewValue) -- macro the spec names as the
//     primary user-facing surface. Expands to:
//
//         XGC_WriteBarrierImpl(&(Slot), (NewValue));
//         (Slot) = (NewValue);
//
//     (Pre-store ordering per spec §4.3 + §5.2: barrier FIRST, then
//     the slot write.)
//
//   * XGC_WriteBarrier(Slot, NewValue) -- alias macro for the
//     XPACT_GC_STORE form. The XCore-4a-side weak-symbol extern "C"
//     XGC_WriteBarrier function (declared in
//     Public/GC/XGCDeclarations.h) takes a pointer to the slot +
//     newValue; this header's MACRO form is the inline-friendly
//     equivalent.
//
//   * ::XCore::XGCWriteBarrierImpl(XObject** Slot, XObject* NewValue)
//     -- the actual inline function. Called by the macro.
//
// THE EXTERN "C" XGC_WriteBarrier SYMBOL.
//
// Phase 5.f also provides the strong-linkage definition of
// extern "C" void XGC_WriteBarrier(void** Slot, void* NewValue) (the
// symbol XCore-4a containers like TArray<XObject*> link against via
// XGCDeclarations.h). The strong symbol delegates to
// XGCWriteBarrierImpl after a void** -> XObject** cast; the cast is
// safe because TArray<XActor*> stores XObject*-shaped slots per the
// XPACT_XPTR_LAYOUT_TAG contract.
//
// =====================================================================
//
// SEMANTICS (per spec §4.3 + §5.2):
//
//   1. Load OLD value from the slot.
//   2. If g_XGCIsConcurrentMarkActive (relaxed load) AND OLD != null:
//        push OLD into the per-thread SATB queue.
//      Else:
//        no-op.
//   3. Dirty the card containing &slot via
//      FXObjectGCCardTable::MarkCardDirty.
//   4. CALLER: write the new value into the slot.
//
// Step 1 is the "snapshot" capture: the OLD reference is preserved in
// the SATB log so the mark phase visits it even if the mutator
// subsequently removes the only reference path.
//
// Step 3's card-dirty mark is UNCONDITIONAL (every store dirties a
// card, regardless of whether mark is active). The card table is the
// generational-scan-reduction substrate; it must be maintained
// continuously so the next minor collection can identify "recently
// written" cards.
//
// =====================================================================
//
// PERFORMANCE COST (per spec §5.2 trailing prose):
//
// ~3-5 cycles per emit:
//   * TLS load for the SATB queue access (~2 cycles on x86_64 / ARM64).
//   * Atomic load of g_XGCIsConcurrentMarkActive (~1 cycle; relaxed).
//   * Branch on the load (~1 cycle; statically predicted not-taken).
//   * Card-table store (~1 cycle; byte write to the L1-resident card
//     array).
//
// On a frame with 100k reference stores, barrier cost is ~0.3-0.5 ms.
// Within the per-frame budget (90 Hz = 11 ms; barrier is ~3-5%).
//
// XPACT_FORCEINLINE on the impl function so the call is collapsed at
// the user site; the typical XPACT_GC_STORE expansion compiles to
// ~6-10 instructions.
//
// =====================================================================
//
// HOT-RELOAD COMMITMENT.
//
// The barrier is the HOTTEST path in the engine (every property
// store). Per Prime Directive: cycle-correct + fence-correct or it's
// worthless. The barrier:
//
//   * NEVER allocates.
//   * NEVER acquires a lock on the fast path.
//   * NEVER calls a virtual function (or any indirection beyond the
//     inline TLS lookup + card-table byte write).
//
// The SATB drain path (when the per-thread queue fills) DOES acquire
// FXObjectGlobalSatbLog's lock briefly, but this is amortised: a
// 256-entry drain happens once every ~256 stores per thread.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"

#include <atomic>
#include <cstddef>

namespace XCore { class XObject; }

namespace XCore
{
    // -----------------------------------------------------------------
    // XGCWriteBarrierImpl -- the actual inline barrier function.
    //
    // Hot-path inlined per spec §5.2.
    //
    // ORDERING (per spec §4.3):
    //
    //   * Load *Slot BEFORE the SATB queue push so the OLD value is
    //     captured.
    //   * Push happens-before the slot write so the SATB snapshot is
    //     complete: any mark-phase drain after the push sees the OLD
    //     value.
    //   * Card dirty mark happens-before the slot write so the next
    //     GC's card-walk sees the card as dirty.
    //
    // The slot write itself is the CALLER's responsibility (the
    // XPACT_GC_STORE macro performs it after this function returns).
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE void XGCWriteBarrierImpl(
        XObject** Slot,
        XObject*  NewValue) noexcept
    {
        // Suppress unused-parameter warning when SATB is dead code:
        // NewValue is consumed by the CALLER (the macro) after this
        // function returns. We don't need it inside the barrier
        // because the SATB capture is the OLD value, not the new.
        (void)NewValue;

        // 1. SATB pre-store capture (only when concurrent mark is
        //    active; the relaxed load is cheap on the no-mark steady
        //    state).
        if (XPACT_UNLIKELY(
                ::XCore::g_XGCIsConcurrentMarkActive.load(
                    ::std::memory_order_relaxed)))
        {
            XObject* const OldValue = *Slot;
            if (OldValue != nullptr)
            {
                ::XCore::GetThreadSatbQueue().Push(OldValue);
            }
        }

        // 2. Card-table dirty mark (always; cost is one byte store).
        //    The card-table covers the slot's address; the bounds
        //    check inside MarkCardDirty silently no-ops when the slot
        //    is outside the registered heap range.
        ::XCore::FXObjectGCCardTable::Get().MarkCardDirty(Slot);
    }

    // -----------------------------------------------------------------
    // XGCWriteBarrierImplBytes -- bulk-store barrier path.
    //
    // For TArray<XPtr<T>> reallocation, TMap<K, XPtr<V>> rehashing,
    // and other memcpy-style transfers of XObject* slots. The caller
    // passes the destination address + the byte length; every card
    // touched by the range is dirtied.
    //
    // The SATB pre-store capture for a bulk path is the responsibility
    // of the caller: the typical bulk-store site is a container
    // realloc that has already drained the OLD slots (e.g., TArray
    // grow copies OLD slot values forward) so the SATB snapshot is
    // captured implicitly. Containers that semantically OVERWRITE a
    // range (TMap rehash) must call XGCWriteBarrierImpl per slot.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE void XGCWriteBarrierImplBytes(
        const void* Address,
        ::std::size_t ByteLength) noexcept
    {
        ::XCore::FXObjectGCCardTable::Get().MarkCardRangeDirty(
            Address, ByteLength);
    }

} // namespace XCore

// =====================================================================
// XPACT_GC_STORE -- the primary user-facing macro (per spec §10.7).
//
// Expands to: pre-store barrier + slot write. The barrier MUST
// precede the write per spec §4.3 (the OLD value is captured before
// the slot is overwritten).
//
// Usage:
//
//     XPACT_GC_STORE(this->OwnerRef, newOwner);
//
// is equivalent to:
//
//     ::XCore::XGCWriteBarrierImpl(
//         reinterpret_cast<::XCore::XObject**>(&(this->OwnerRef)),
//         static_cast<::XCore::XObject*>(newOwner));
//     this->OwnerRef = newOwner;
//
// The do-while wrapping is the standard "single statement" macro idiom
// so the macro behaves as one statement at the call site (e.g., as the
// body of an `if`-without-braces).
// =====================================================================

#ifndef XPACT_GC_STORE
#define XPACT_GC_STORE(SLOT, NEW_VALUE)                                 \
    do                                                                  \
    {                                                                   \
        ::XCore::XGCWriteBarrierImpl(                                   \
            reinterpret_cast<::XCore::XObject**>(&(SLOT)),              \
            static_cast<::XCore::XObject*>(NEW_VALUE));                 \
        (SLOT) = (NEW_VALUE);                                           \
    } while (0)
#endif

// =====================================================================
// XGC_WriteBarrier(SLOT, NEW_VALUE) -- alias to XPACT_GC_STORE.
//
// NOTE: this is the MACRO form. It is intentionally different from the
// extern "C" XGC_WriteBarrier function declared in
// Public/GC/XGCDeclarations.h:
//
//   * The MACRO form expands to barrier + store at the call site.
//   * The extern "C" FUNCTION form takes &Slot + NewValue and performs
//     ONLY the barrier (NOT the store; the caller does the store).
//
// XCore-4a containers (TArray<T*> partial specialisation) call the
// extern "C" function form. User code at the C++ call site uses the
// MACRO form for ergonomic correctness (the macro emits both the
// barrier AND the store, removing the chance of forgetting one).
// =====================================================================

#ifndef XGC_WriteBarrier
#define XGC_WriteBarrier(SLOT, NEW_VALUE) XPACT_GC_STORE(SLOT, NEW_VALUE)
#endif
