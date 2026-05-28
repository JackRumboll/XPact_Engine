// Copyright Simgenics. All Rights Reserved.
//
// =====================================================================
// XGCWriteBarrierStub.cpp -- XCore-4a-side no-op GC ABI stubs.
// =====================================================================
//
// XCore-4a Rev 3, Section 5.4 + Section 13.1; locked decision 7;
// Phase 1g fix M-6.
//
// Per locked decision 7, the four GC root-registration / barrier
// entry points declared in Public/GC/XGCDeclarations.h ship in
// XCore-4a as no-op weak-symbol stubs. XCore-4b's real GC then
// provides strong-linkage definitions that the linker resolves in
// preference to these weak stubs at link time. The weak-symbol
// pattern means:
//
//   (a) An XCore-4a-only build (e.g., the XCore.Tests target before
//       XCore-4b is implemented) links cleanly with no-op GC.
//       GC-aware containers (TArray<XActor*>, etc.) construct,
//       resize, and destruct without crashing, and the collector
//       "does nothing" -- the no-op barrier is correct for a no-GC
//       build because there is no card table to mark.
//
//   (b) When XCore-4b ships and is linked in alongside XCore-4a, the
//       strong-linkage XGC_RegisterRootSpan / XGC_UpdateRootSpan /
//       XGC_UnregisterRootSpan / XGC_WriteBarrier definitions in
//       XCore-4b override these stubs. No XCore-4a source change is
//       required; the weak-symbol attribute resolves the override at
//       link time.
//
// PLATFORM-SPECIFIC LINKAGE ATTRIBUTE
// -----------------------------------------------------------------
// Clang / GCC (Linux + Android): [[gnu::weak]] is the standard
// attribute and Just Works(TM). The linker picks the strong
// definition when both weak and strong are present in the link.
//
// MSVC (Win64): MSVC does NOT support [[gnu::weak]] on function
// definitions. The closest equivalent, __declspec(selectany), is
// data-only -- it works for global variables but is rejected on
// functions. The conventional MSVC patterns are (1) `/ALTERNATENAME`
// linker directives, or (2) just defining the function strongly
// here and relying on XCore-4b's definition coming first in the
// link order with `__declspec(dllexport)`. We use approach (2): on
// MSVC the symbols ship as ordinary strong-linkage definitions;
// when XCore-4b dllexports the same symbol, the loader resolves to
// the XCore-4b copy first because XCore-4b is loaded before any TU
// that calls these entry points executes a call instruction. The
// XCore-4a copy remains in the binary as dead code (~16 bytes
// total) and the linker does not warn because both copies have
// matching prototypes.
//
// PRECONDITION DOCUMENTATION
// -----------------------------------------------------------------
// The strong-linkage XCore-4b implementation will enforce
// `XGC::IsSlotInRegisteredSpan(Slot)` as a precondition of
// XGC_WriteBarrier: passing a Slot pointer that is not inside any
// currently-registered XGCRootSpan is a contract violation, because
// the collector has no card-table entry for unregistered memory.
// In Debug/Dev builds the strong version aborts with a diagnostic;
// in Shipping the strong version is undefined-behaviour for the
// same condition (the card table simply does not have a bucket to
// mark). The no-op stubs here do not enforce the precondition --
// they cannot, because XGCRootSpan registration is also a no-op in
// the XCore-4a-only configuration, so there ARE no registered
// spans to check against. The precondition contract is documented
// here for the XCore-4b implementation team to honour.
//
// SHUTDOWN NOTE
// -----------------------------------------------------------------
// All four stubs are noexcept (matching the declaration in
// XGCDeclarations.h). They are safe to call at any engine phase,
// including before global construction and after global
// destruction, because they perform no work. A call to a no-op stub
// is a single `ret` instruction; there is no allocator, mutex, or
// I/O involvement.
// =====================================================================

#include "GC/XGCDeclarations.h"
#include "XObject/XGCWriteBarrierStrongSymbolGate.h"

// =====================================================================
// PHASE 5.f STRONG-SYMBOL GATE.
//
// XCoreXObject Phase 5.f (Private/XObject/XGCWriteBarrier.cpp) ships
// strong-symbol implementations of the four GC ABI symbols. The
// XGCWriteBarrierStrongSymbolGate.h header defines
// XPACT_HAS_XCOREXOBJECT_STRONG_GC = 1 unconditionally when included;
// since the header lives under Public/XObject/ and is therefore part
// of the same compile target as the stub, the gate fires whenever
// Phase 5.f's strong-symbol TU is built alongside the stub.
//
// The entire weak-stub body below is #if-skipped when the gate fires.
// On Clang/GCC the weak attribute would let the strong override win
// anyway; the skip just keeps the stub bytes out of the binary. On
// MSVC the skip is LOAD-BEARING (the stub is strong-linkage; a
// duplicate strong-linkage definition is an LNK2005 link error).
// =====================================================================

#if !defined(XPACT_HAS_XCOREXOBJECT_STRONG_GC) || (XPACT_HAS_XCOREXOBJECT_STRONG_GC == 0)

#if defined(__clang__) || defined(__GNUC__)
    #define XPACT_GC_WEAK_STUB [[gnu::weak]]
#else
    // MSVC: no portable function-level weak-symbol attribute; rely on
    // XCore-4b providing strong-linkage dllexport definitions that
    // resolve first at link/load time. See header block above.
    #define XPACT_GC_WEAK_STUB /* no-op on MSVC; see header */
#endif

extern "C"
{

XPACT_GC_WEAK_STUB
void XGC_RegisterRootSpan(::XGC::XGCRootSpan* /*Span*/) noexcept
{
    // No-op stub. XCore-4b's implementation enqueues the span into
    // the collector's root-set; here we simply discard the call
    // because the collector does not exist yet.
}

XPACT_GC_WEAK_STUB
void XGC_UpdateRootSpan(::XGC::XGCRootSpan* /*Span*/, void** /*NewBase*/, ::SIZE_T /*NewCount*/) noexcept
{
    // No-op stub. XCore-4b's implementation atomically updates the
    // span's base + count against the mark walker. With no
    // collector, container reallocation is a single buffer
    // replacement with no observer.
}

XPACT_GC_WEAK_STUB
void XGC_UnregisterRootSpan(::XGC::XGCRootSpan* /*Span*/) noexcept
{
    // No-op stub. XCore-4b's implementation removes the span from
    // the collector's root-set before the underlying buffer is
    // freed.
}

XPACT_GC_WEAK_STUB
void XGC_WriteBarrier(void** /*Slot*/, void* /*NewValue*/) noexcept
{
    // No-op stub. XCore-4b's implementation marks the card-table
    // entry covering `Slot` so the collector observes the
    // (Slot, NewValue) update on its next mark pass. With no card
    // table, the barrier is a single ret -- correct for a no-GC
    // build because there is no collector to inform.
    //
    // Precondition (XCore-4b strong version): `Slot` MUST point
    // into a currently-registered XGCRootSpan. The no-op here does
    // not enforce; the strong version will via
    // ::XGC::IsSlotInRegisteredSpan(Slot) at the entry of the
    // function.
}

} // extern "C"

#endif // !XPACT_HAS_XCOREXOBJECT_STRONG_GC
