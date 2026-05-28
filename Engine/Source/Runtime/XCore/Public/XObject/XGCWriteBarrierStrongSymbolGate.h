// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCWriteBarrierStrongSymbolGate.h -- compile-time sentinel for the
// XCoreXObject Phase 5.f strong-symbol GC ABI takeover.
// =====================================================================
//
// Defines XPACT_HAS_XCOREXOBJECT_STRONG_GC to 1 unconditionally when
// this header is included. The header's PRESENCE in a TU's include
// graph is the signal that Phase 5.f's strong GC ABI implementations
// exist in the same build target.
//
// THE STUB TU (Private/GC/XGCWriteBarrierStub.cpp) INCLUDES THIS
// HEADER and compiles its body conditionally:
//
//     #if !XPACT_HAS_XCOREXOBJECT_STRONG_GC
//         // ... weak-symbol stub bodies ...
//     #endif
//
// At Phase 5.f, the header lands and the stub becomes a no-op TU. The
// strong-symbol bodies in Private/XObject/XGCWriteBarrier.cpp take
// over.
//
// Why a SEPARATE HEADER rather than gating on
// XPACT_XOBJECT_LAYOUT_TAG (which the stub already has access to via
// XReflectionRuntime.h)? Per Prime Directive: the layout-tag macro's
// presence is the ABI lock, NOT the implementation-existence
// signal. Conflating the two would surprise downstream consumers who
// expect "tag present" to mean "layout pinned" not "implementation
// shipped". This sentinel is the explicit signal.
//
// RATIONALE FOR MSVC DUPLICATE-SYMBOL HANDLING:
//
// On Clang/GCC the existing stub uses [[gnu::weak]] and the strong
// definition in XGCWriteBarrier.cpp overrides it automatically. The
// gate is mainly to keep the code surface clean (no leftover stub
// bytes in the binary).
//
// On MSVC the existing stub is STRONG-linkage (MSVC has no
// portable function-level weak attribute). Without this gate, the
// MSVC link would fail with LNK2005 (duplicate symbol). The gate is
// LOAD-BEARING on MSVC.
//
// =====================================================================

#define XPACT_HAS_XCOREXOBJECT_STRONG_GC 1
