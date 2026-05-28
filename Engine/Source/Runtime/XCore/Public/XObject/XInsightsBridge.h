// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XInsightsBridge.h -- weak-symbol bridge for XInsights::Emit
// (XCoreXObject Rev 4 §10.5 + Rev 2 FIX-A-CRIT-6).
// =====================================================================
//
// XCoreXObject Rev 4 §10.5 contract:
//
//   XCoreXObject calls `XCore_XInsights_Emit(Category, Event, Payload)`
//   at every telemetry event (GC cycle complete, allocator failure,
//   hot-reload class replaced, etc.). XInsights, when linked, provides
//   the STRONG-SYMBOL implementation that actually collects + forwards
//   to XLog / XTelemetry / local trace-buffer. When XInsights is NOT
//   linked (e.g., XCore-only test target; XCoreXObject Phase 5.k
//   without XInsights), the WEAK-SYMBOL default body silently
//   discards.
//
// This matches XCore-4a's existing weak-symbol pattern documented at
// FMallocBinnedX.h ("XINSIGHTS DEFERRAL" header block) and at
// XGCWriteBarrierStub.cpp (GC barrier weak/strong layering).
//
// =====================================================================
//
// PLATFORM MECHANISM (per spec §10.5 + Phase 5.k Prime-Directive
// posture):
//
// Clang / GCC (Linux + Android): `[[gnu::weak]]` is the standard
// attribute and the linker resolves the strong override correctly when
// both weak + strong are present in the link. The weak attribute
// applies to the DEFINITION (in XInsightsBridgeStub.cpp); declarations
// can be plain `extern "C"`.
//
// MSVC (Win64): MSVC has NO portable function-level weak-symbol
// attribute. `__declspec(selectany)` exists but applies only to DATA
// definitions, not functions. The conventional MSVC patterns are:
//
//   (1) `/ALTERNATENAME` linker directives -- requires per-target
//       link-script changes; XBT does not generate them today.
//
//   (2) STRONG-LINKAGE everywhere + dllexport ordering -- the loader
//       resolves to the strong override first because the strong-
//       providing DLL is loaded first and its dllexport entry beats
//       the weak-providing DLL's strong-but-non-exported entry.
//
//   (3) STRONG-LINKAGE everywhere + COMPILE-TIME GATE -- the weak
//       stub TU compiles its body only when no strong provider gate
//       is defined; when the strong provider's gate header is
//       included anywhere in the same compile target, the stub body
//       is #if-skipped.
//
// Phase 5.k adopts the COMBINED pattern from XCore-4a's GC barrier:
//
//   * The bridge DECLARATION below is plain `extern "C"`. No `weak`
//     attribute on the declaration; both Clang and MSVC accept this.
//
//   * The bridge DEFAULT body lives in
//     `Private/XObject/XInsightsBridgeStub.cpp`. On Clang/GCC the
//     definition carries `[[gnu::weak]]`. On MSVC the definition is
//     plain strong-linkage, BUT the entire .cpp body is gated on
//     `#if !defined(XPACT_HAS_XINSIGHTS_STRONG_BRIDGE)`. When
//     XInsights ships its strong provider TU, that TU includes a
//     gate-defining header (matching the pattern from
//     `XGCWriteBarrierStrongSymbolGate.h`), and the stub body
//     compiles to an empty TU on MSVC. On Clang/GCC the weak
//     attribute makes the gate cosmetic -- the strong override would
//     win anyway -- but the gate is included for symmetry.
//
//   * The strong provider's `XCore_XInsights_Emit` definition (when
//     XInsights ships) carries `__declspec(dllexport)` on MSVC so
//     the loader prefers it; on Clang/GCC it is a plain strong
//     definition and the weak attribute on the stub yields to it.
//
// The macro `XPACT_WEAK` defined below carries the per-platform
// attribute for the DEFINITION site. Callers who declare additional
// weak-symbol entry points (none today; the bridge is the only one
// in Phase 5.k) use the same macro for consistency.
//
// =====================================================================
//
// THREAD SAFETY:
//
// `XCore_XInsights_Emit` is callable from any thread. The default
// no-op stub is trivially thread-safe (no state). The strong override
// is XInsights's responsibility; the spec §10.5 trailing prose
// ("callbacks copy into caller-owned thread-local buffers OR enqueue
// into a lock-free MPSC drained by an XInsights consumer thread")
// documents the requirement.
//
// =====================================================================
//
// COST WHEN XINSIGHTS NOT LINKED:
//
// One indirect call through a known-stable function pointer (the
// `XCore_XInsights_Emit` extern "C" symbol). The body is `return;`
// (single ret instruction). The C++ wrapper `Emit()` below is
// XPACT_FORCEINLINE so the call site emits one CALL + one RET (~3
// cycles). No allocation; no atomic; no lock.
//
// This is below the threshold where adding a runtime "is XInsights
// linked?" check would be a net optimization -- the gate's branch +
// load is cheaper than the call only on the hottest paths (GC mark
// per-object), and the GC mark path does NOT emit per-object; it
// emits once per cycle. The current design is correct.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FName.h"
#include "XObject/FXInsightsPayload.h"

#include <cstddef>

// =====================================================================
// XPACT_WEAK -- portability shim for the weak-symbol DEFINITION
// attribute.
//
// Used at the strong-symbol stub's body in XInsightsBridgeStub.cpp.
// Phase 5.k introduces this macro; future weak-symbol bridges (e.g.,
// XLog ABI evolution) can reuse it.
// =====================================================================
#if defined(__clang__) || defined(__GNUC__)
    #define XPACT_WEAK __attribute__((weak))
#else
    // MSVC: no function-level weak attribute. The stub TU relies on
    // the compile-time gate (see XInsightsBridgeStub.cpp header) so
    // its body is excluded when the strong provider is in the link.
    // The macro expands to nothing on MSVC; the symbol's linkage is
    // ordinary external.
    #define XPACT_WEAK /* MSVC: gate-based; see XInsightsBridgeStub.cpp */
#endif

// =====================================================================
// extern "C" declaration of the weak-symbol bridge entry point.
//
// Naming: `XCore_XInsights_Emit` (not `XInsights::Emit`). The C-style
// name is chosen so:
//   * The symbol mangling is fixed across compilers (no per-compiler
//     C++ mangling drift); the strong override in XInsights binds
//     against a stable name.
//   * MSVC's `__declspec(dllexport)` and Clang/GCC's `[[gnu::weak]]`
//     both apply cleanly to extern "C" function definitions.
//   * The weak-symbol pattern matches XCore-4a's XGC_* extern "C"
//     ABI (XGCDeclarations.h), preserving a consistent shape across
//     all of the engine's link-time-resolved hooks.
//
// Parameters:
//   * `Category` -- null-terminated UTF-8 byte pointer naming the
//                   top-level event category. Phase 5.k's emit-helpers
//                   pass a pointer derived from one of the
//                   XInsightsEvents::CategoryGC / CategoryAllocator /
//                   CategoryHotReload FNames via FName::GetBaseBytes().
//   * `Event`    -- null-terminated UTF-8 byte pointer naming the
//                   specific event within the category.
//   * `Payload`  -- pointer to the structured key-value payload. The
//                   payload's lifetime extends until the call returns.
//                   The strong provider must COPY any data it wants
//                   to retain across the call (the spec wording is
//                   "synchronous emit; payload is caller-owned").
//
// The pointer-typed signature (vs. const-ref) is chosen so the
// C-linkage surface is clean (C does not have references) and so the
// strong provider can be implemented in C if desired.
// =====================================================================

extern "C"
{
    // The default WEAK body lives in XInsightsBridgeStub.cpp. The
    // declaration here is what every callsite (XInsightsBridge::Emit
    // wrapper + the per-category helpers) references.
    void XCore_XInsights_Emit(
        const char*                                       Category,
        const char*                                       Event,
        const ::XCore::HAL::FXInsightsPayload*            Payload) noexcept;

} // extern "C"

// =====================================================================
// C++ wrapper namespace.
//
// Production code (the per-category EmitHelpers + direct call sites
// inside FXObjectAllocator + FXObjectGCCardTable) calls
// `::XCore::HAL::XInsightsBridge::Emit(...)` rather than the raw
// extern "C" symbol. The wrapper is XPACT_FORCEINLINE so it compiles
// to a single CALL to XCore_XInsights_Emit.
//
// =====================================================================

namespace XCore::HAL::XInsightsBridge
{
    // -----------------------------------------------------------------
    // Emit -- C++ wrapper that takes FName Category + FName Event +
    // const FXInsightsPayload& and forwards to the extern "C" bridge.
    //
    // The FName arguments resolve to base-byte pointers via
    // FName::GetBaseBytes(); the C-linkage call site then sees plain
    // null-terminated UTF-8 strings. The FName layer is the principled
    // typed-name carrier on the C++ side (avoiding the "what do I do
    // with a `const char*`?" cargo-cult question at every emit site);
    // the C-string layer is the principled typed-payload carrier on
    // the link-time-resolved C side (avoiding C++-mangling drift +
    // cross-compiler-FName-layout assumptions).
    //
    // ZERO-ALLOCATION GUARANTEE: this wrapper performs no allocation.
    // FName::GetBaseBytes() returns the interned pointer (stable for
    // the process lifetime per XCore-4b §4.7). The payload is passed
    // by const-ref + address-of; no copy.
    //
    // The wrapper is XPACT_FORCEINLINE so the compiler emits the call
    // directly at the emit site; downstream call-graph analysis sees
    // through the wrapper.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE void Emit(
        ::XCore::Reflect::FName Category,
        ::XCore::Reflect::FName Event,
        const ::XCore::HAL::FXInsightsPayload& Payload) noexcept
    {
        // GetBaseBytes returns a const char* pointing at the interned
        // base UTF-8 bytes (NUL-terminated, no numbered-suffix). The
        // bridge's strong provider sees a stable pointer until the
        // process exits. For NAME_None (Index == 0) GetBaseBytes
        // returns the literal "None" bytes per FName.h doc; the
        // strong provider can dispatch on that as a sentinel if
        // desired.
        const char* const CategoryCstr = Category.GetBaseBytes();
        const char* const EventCstr    = Event.GetBaseBytes();
        ::XCore_XInsights_Emit(CategoryCstr, EventCstr, &Payload);
    }

    // -----------------------------------------------------------------
    // Emit overload that takes an EMPTY payload by value.
    //
    // Convenience for sites that need to emit a category+event with
    // no body (e.g., a heartbeat tick). The empty FXInsightsPayload
    // is constructed on the stack and passed by const-ref through the
    // delegating call below.
    //
    // The empty payload's TArray is allocation-free (its default ctor
    // does not call FMemory). The temporary's lifetime extends to the
    // end of the full-expression containing the call -- well within
    // the bridge's synchronous-call contract.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE void Emit(
        ::XCore::Reflect::FName Category,
        ::XCore::Reflect::FName Event) noexcept
    {
        const ::XCore::HAL::FXInsightsPayload EmptyPayload;
        Emit(Category, Event, EmptyPayload);
    }

} // namespace XCore::HAL::XInsightsBridge
