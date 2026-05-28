// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInsightsBridgeStub.cpp -- default WEAK no-op body for the
// XInsights::Emit bridge (XCoreXObject Rev 4 §10.5; Phase 5.k).
// =====================================================================
//
// This TU defines the DEFAULT body of `XCore_XInsights_Emit`. The body
// silently discards events. When XInsights ships its strong-symbol
// provider TU, the weak attribute (on Clang/GCC) or the compile-time
// gate header (on MSVC) routes the link to the strong provider; the
// stub becomes dead code (~3 bytes; one `ret`).
//
// PLATFORM-SPECIFIC LINKAGE per XInsightsBridge.h header block:
//
//   * Clang / GCC: [[gnu::weak]] is the standard attribute. The linker
//     picks the strong definition when both weak and strong are
//     present in the link.
//
//   * MSVC: no portable function-level weak attribute exists. The
//     ENTIRE body of this TU is gated on
//       #if !defined(XPACT_HAS_XINSIGHTS_STRONG_BRIDGE) || \
//           (XPACT_HAS_XINSIGHTS_STRONG_BRIDGE == 0)
//     so when XInsights's strong provider lands and brings the gate
//     header into the build scope, this TU compiles to an empty unit
//     and no LNK2005 fires.
//
// HOT-PATH COST:
//
//   The default no-op body is a single `ret` instruction. On Clang/GCC
//   the [[gnu::weak]] attribute does not prevent inlining at the call
//   site (the C++ inline rules treat weak symbols as visible to the
//   optimizer), so the wrapper `XInsightsBridge::Emit` may be inlined
//   into a single CALL (or even tail-call-eliminated to a single RET).
//   On MSVC the strong-linkage default cannot be inlined across TU
//   boundaries; the wrapper compiles to one CALL + one RET (~3
//   cycles).
//
// =====================================================================

#include "XObject/XInsightsBridge.h"
#include "XObject/XInsightsBridgeStrongSymbolGate.h"

// =====================================================================
// COMPILE-TIME GATE.
//
// When XInsights's strong provider is in scope, the gate header sets
// XPACT_HAS_XINSIGHTS_STRONG_BRIDGE = 1 and this TU's body is
// excluded. On MSVC the exclusion is load-bearing (otherwise LNK2005).
// On Clang/GCC the gate is cosmetic (the [[gnu::weak]] attribute
// resolves the override at link time).
// =====================================================================

#if !defined(XPACT_HAS_XINSIGHTS_STRONG_BRIDGE) || (XPACT_HAS_XINSIGHTS_STRONG_BRIDGE == 0)

extern "C"
{

// ---------------------------------------------------------------------
// The DEFAULT body. Silent no-op. XInsights's strong override (when
// linked) takes precedence; this body becomes unreachable code.
//
// The function MUST be noexcept (matches the declaration in
// XInsightsBridge.h) so callers can rely on the "telemetry never
// throws" engine contract.
//
// All three arguments are unused at the default body. The (void)
// casts suppress unused-parameter warnings on every supported
// compiler (MSVC C4100, Clang -Wunused-parameter, GCC -Wunused-
// parameter).
// ---------------------------------------------------------------------
XPACT_WEAK
void XCore_XInsights_Emit(
    const char*                                       Category,
    const char*                                       Event,
    const ::XCore::HAL::FXInsightsPayload*            Payload) noexcept
{
    (void)Category;
    (void)Event;
    (void)Payload;
    // Silent discard. The strong provider (XInsights, post-MVP)
    // collects + forwards to XLog / XTelemetry / local trace-buffer.
}

} // extern "C"

#endif // !XPACT_HAS_XINSIGHTS_STRONG_BRIDGE
