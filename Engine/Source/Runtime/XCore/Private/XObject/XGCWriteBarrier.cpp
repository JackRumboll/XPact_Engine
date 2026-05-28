// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCWriteBarrier.cpp -- strong-symbol implementations of the
// XCore-4a-side extern "C" GC ABI declarations (Phase 5.f).
// =====================================================================
//
// Replaces the no-op weak-symbol stubs in
// Private/GC/XGCWriteBarrierStub.cpp with strong-symbol implementations
// per XCoreXObject Rev 4 §13.1 trailing prose:
//
//     "XCoreXObject's link step replaces XCore-4a's weak-symbol stubs
//      for XGC_WriteBarrier / XGC_RegisterRootSpan / XGC_UpdateRootSpan
//      / XGC_UnregisterRootSpan with strong-symbol implementations.
//      The replacement is automatic at link time; no source change to
//      XCore-4a is needed."
//
// At Phase 5.f the strong-symbol implementations are:
//
//   * XGC_WriteBarrier  -- delegates to ::XCore::XGCWriteBarrierImpl.
//                          The actual card-table mark + SATB push.
//
//   * XGC_RegisterRootSpan / UpdateRootSpan / UnregisterRootSpan
//                       -- STILL no-op. The XGCRoot subsystem (the
//                          root registry that consumes these spans)
//                          lands at Phase 5.g+. Phase 5.f leaves them
//                          as no-ops so XCore-4a containers
//                          (TArray<XActor*>) continue to compile +
//                          register spans against the new strong
//                          symbols without behavioral change.
//
// =====================================================================
//
// SYMBOL OVERRIDE MECHANICS.
//
// Per the spec / XGCWriteBarrierStub.cpp prose: on Clang/GCC the
// stubs are tagged [[gnu::weak]] so the strong definitions here
// override them. On MSVC the stub is strong-linkage; both stubs and
// strong definitions are in the same DLL (XCore.dll), which causes a
// duplicate-symbol error if both are linked.
//
// SOLUTION:
//
// The Phase 5.f shipped code REPLACES the body of the stub functions
// here -- the stubs in XGCWriteBarrierStub.cpp are conditionally
// compiled OUT when Phase 5.f's strong symbols are present. The
// conditional compilation uses a macro XPACT_HAS_XCOREXOBJECT_STRONG_GC
// defined here; XGCWriteBarrierStub.cpp checks for the macro at
// compile time and #if-skips its body if defined.
//
// This is the principled approach for Phase 5.f's in-same-DLL build:
//   * No duplicate-symbol error on MSVC.
//   * The weak-symbol pattern still works on Clang/GCC because the
//     weak attribute is still emitted (XGCWriteBarrierStub.cpp's body
//     is just empty in the strong-symbol presence case).
//   * No source change to XCore-4a's container code (TArray.h /
//     XGCDeclarations.h) is required.
//
// =====================================================================

// Include the gate header so the stub TU's #if guard fires.
// XGCWriteBarrierStrongSymbolGate.h defines
// XPACT_HAS_XCOREXOBJECT_STRONG_GC = 1 unconditionally; the gate is
// the explicit signal that the strong-symbol TU is being built. See
// the header's own block for the MSVC vs Clang/GCC rationale.
#include "XObject/XGCWriteBarrierStrongSymbolGate.h"

#include "GC/XGCDeclarations.h"
#include "XObject/XGCWriteBarrier.h"

// ---------------------------------------------------------------------
// XGC_WriteBarrier -- strong-symbol implementation.
//
// XCore-4a-side containers (TArray<XObject*> et al; see
// Containers/TArray.h:60) call XGC_WriteBarrier via the
// XGCDeclarations.h declaration. The call is `XGC_WriteBarrier(
// &slot, newValue)`.
//
// Phase 5.f delegates to the inline impl: cast void** to XObject**
// (the slot stores an XObject* per the XPACT_XPTR_LAYOUT_TAG
// contract; the cast is ABI-stable per spec §6.1).
// ---------------------------------------------------------------------
extern "C" void XGC_WriteBarrier(void** Slot, void* NewValue) noexcept
{
    ::XCore::XGCWriteBarrierImpl(
        reinterpret_cast<::XCore::XObject**>(Slot),
        static_cast<::XCore::XObject*>(NewValue));
    // NOTE: the slot WRITE is the CALLER's responsibility per the
    // extern "C" function form's spec (see XGCDeclarations.h:204-231
    // "Pre-store barrier semantics: XGC_WriteBarrier(&slot, newValue);
    // slot = newValue;"). The function form ONLY performs the barrier;
    // the macro form (XPACT_GC_STORE) performs barrier + store.
}

// ---------------------------------------------------------------------
// XGC_RegisterRootSpan / XGC_UpdateRootSpan / XGC_UnregisterRootSpan
// -- still no-op at Phase 5.f.
//
// Phase 5.g+ wires the XGCRoot subsystem (the root-span registry) so
// the GC mark phase can enumerate spans. Until then the strong-symbol
// definitions are deliberate no-ops; they exist so the linker resolves
// them in preference to the weak stubs in XGCWriteBarrierStub.cpp.
//
// TODO(Phase 5.g): land Public/XObject/XGCRoot.h + the root-span
// registry body. Replace these no-ops with the real registry calls.
// ---------------------------------------------------------------------

extern "C" void XGC_RegisterRootSpan(::XGC::XGCRootSpan* /*Span*/) noexcept
{
    // TODO(Phase 5.g): enqueue Span into the collector's root-set.
}

extern "C" void XGC_UpdateRootSpan(
    ::XGC::XGCRootSpan* /*Span*/,
    void**              /*NewBase*/,
    ::SIZE_T            /*NewCount*/) noexcept
{
    // TODO(Phase 5.g): atomically update Span->base + Span->count
    // against the mark walker.
}

extern "C" void XGC_UnregisterRootSpan(::XGC::XGCRootSpan* /*Span*/) noexcept
{
    // TODO(Phase 5.g): remove Span from the collector's root-set.
}
