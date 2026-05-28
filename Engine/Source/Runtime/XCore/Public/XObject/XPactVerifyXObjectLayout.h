// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XPactVerifyXObjectLayout.h -- the build-time ABI-pin macro per
// XCoreXObject Rev 4 §11.3.
// =====================================================================
//
// XCoreXObject Rev 4 Section 11.3 ("Static_assert pin set
// (XCoreXObject side)") freezes the XObject-side ABI surface via the
// `XPACT_VERIFY_XOBJECT_LAYOUT()` macro. The macro is emitted into
// every XHT-generated `.gen.cpp` so a patched DLL compiled against a
// different ABI fails to link with a clean compile-time error before
// any runtime damage occurs.
//
// Phase 5.a SCOPE: the macro pins the layouts for the types that
// ship at Phase 5.a (XObject, EObjectFlags, FXObjectArrayEntry) plus
// the Phase 5.a' Contract Rev 13.9 cascade pins for FStruct + FClass
// (which Phase 5.a' / Phase 4b.7 already shipped at the
// XCore-4b-side; we re-pin them here so the XObject-side macro is
// the single source of truth for downstream consumers).
//
// Types that land in LATER phases (FXObjectLifecycleTable,
// FXObjectRefSchema, FXObjectRefSchemaOp, FObjectKey, XPtr / XWeakPtr /
// XStrongPtr, FXObjectArray) are pinned by the spec §11.3 macro but
// are NOT yet C++-real at Phase 5.a -- the macro defers those pins
// behind XPACT_VERIFY_XOBJECT_LAYOUT_PHASE_5A_ONLY which Phase 5.a
// callers compile against. Once the later phases ship, the umbrella
// macro pins the full set.
//
// USAGE (per spec §11.3):
//
//   #include "XObject/XPactVerifyXObjectLayout.h"
//
//   namespace { XPACT_VERIFY_XOBJECT_LAYOUT(); }
//
// The macro instantiates static_asserts at the call-site scope; the
// surrounding anonymous namespace prevents ODR-collision when the
// macro is emitted into multiple .gen.cpp TUs of the same module.
//
// LAYOUT-TAG STRING-EQUALITY CHECKS:
//
// The macro also verifies the per-type XPACT_*_LAYOUT_TAG string
// literals match the values defined in XReflectionRuntime.h (the
// single-source-of-truth for the ABI tag strings). The
// XPactDetail::CompileTimeStrEq helper from XReflectionRuntime.h does
// the constexpr byte-by-byte compare; a mismatch fires a clean
// compile error. This catches the case where a developer edits the
// tag string in one of the THREE sources (this header / AbiLayoutPins.cs
// / ContractSurface.cs) but forgets the other two.
//
// =====================================================================

#include "XReflectionRuntime.h"               // XPACT_*_LAYOUT_TAG defines + XPactDetail::CompileTimeStrEq
#include "XObject/XObject.h"                  // sizeof(XObject) + offsetof
#include "XObject/EObjectFlags.h"             // EObjectFlags + bit positions
#include "XObject/FXObjectArrayEntry.h"       // sizeof(FXObjectArrayEntry) + offsetof
#include "XObject/XObjectKey.h"               // sizeof(XObjectKey) + offsetof (Phase 5.c)
#include "XObject/XPtr.h"                     // sizeof(XPtr<XObject>) + offsetof (Phase 5.c)
#include "XObject/XWeakPtr.h"                 // sizeof(XWeakPtr<XObject>) + offsetof (Phase 5.c)
#include "XObject/XStrongPtr.h"               // sizeof(XStrongPtr<XObject>) (Phase 5.c)
#include "Reflection/FStruct.h"               // sizeof(FStruct) + RefSchema offset (Rev 13.9 cascade)
#include "Reflection/FClass.h"                // sizeof(FClass) + LifecycleTable offset (Rev 13.9 cascade)

#include <cstddef>          // offsetof
#include <cstdint>          // uint32_t

// ---------------------------------------------------------------------
// XPACT_VERIFY_XOBJECT_LAYOUT -- the Phase 5.a build-time ABI pin set.
//
// Emits static_asserts pinning:
//
//   * XObject: sizeof + alignof + every member offset (per spec §2.2
//     + §11.3).
//   * FXObjectArrayEntry: sizeof + member offsets (per spec §3.3 +
//     §11.3).
//   * FStruct + FClass: Rev 13.9 cascade pins (per spec §11.3; types
//     already shipped at Phase 5.a' / 4b.7).
//   * XPACT_*_LAYOUT_TAG string-equality verification (the contract-
//     wired tag strings match the values in XReflectionRuntime.h).
//   * Plugin-range bit-position locks for EObjectFlags (UserFlag_1..8
//     occupy bits 24..31 per Rev 3 FIX-M-R2-2).
//
// PHASE 5.c UPDATE: the object-handle pins (XObjectKey, XPtr<XObject>,
// XWeakPtr<XObject>, XStrongPtr<XObject>) NOW LANDED at Phase 5.c and
// are wired into the macro body below. The remaining deferred pins
// (FXObjectLifecycleTable @ 72 bytes, FXObjectRefSchema @ 24,
// FXObjectRefSchemaOp @ 24) ship at Phase 5.d / 5.g'+ and are
// documented in the deferred-pins block at the bottom.
//
// XSoftPtr<XObject> is NOT pinned by this macro because spec §6.3
// explicitly states XSoftPtr "is NOT in the ABI-lock set because its
// size is necessarily variable; FSoftObjectProperty's storage in
// reflected slots is its own bounded payload". The XSoftPtr.h
// header's own static_assert documents the Phase 5.c snapshot size
// (16 bytes) but it is intentionally outside the cross-DLL pin
// surface.
//
// The macro is defined as a do-while(0) block wrapped in a struct
// declaration so the static_asserts live at namespace scope (the
// caller's anonymous namespace per the usage pattern above).
// Specifically: the macro emits the static_asserts directly without
// wrapping; each static_assert is a declaration (not a statement) so
// it MUST be at namespace / class scope, not in a function body.
// ---------------------------------------------------------------------

#define XPACT_VERIFY_XOBJECT_LAYOUT()                                                              \
    /* ===== XObject (per spec §2.2 + §11.3) ===== */                                              \
    static_assert(sizeof(::XCore::XObject)                       == 56,                            \
                  "XObject ABI lock (Contract Rev 13.9 / "                                         \
                  "XPACT_XOBJECT_LAYOUT_TAG): 56 bytes per XCoreXObject "                          \
                  "Rev 4 §2.2.");                                                                  \
    static_assert(alignof(::XCore::XObject)                      ==  8,                            \
                  "XObject alignment ABI lock: 8-byte aligned.");                                  \
    static_assert(offsetof(::XCore::XObject, ClassPrivate)       ==  0,                            \
                  "XObject.ClassPrivate offset lock (GC mark hot path).");                         \
    static_assert(offsetof(::XCore::XObject, InternalIndex)      ==  8,                            \
                  "XObject.InternalIndex offset lock.");                                           \
    static_assert(offsetof(::XCore::XObject, SerialNumber)       == 12,                            \
                  "XObject.SerialNumber offset lock.");                                            \
    static_assert(offsetof(::XCore::XObject, Outer)              == 16,                            \
                  "XObject.Outer offset lock.");                                                   \
    static_assert(offsetof(::XCore::XObject, NamePrivate)        == 24,                            \
                  "XObject.NamePrivate offset lock.");                                             \
    static_assert(offsetof(::XCore::XObject, ObjectFlags)        == 32,                            \
                  "XObject.ObjectFlags offset lock.");                                             \
    static_assert(offsetof(::XCore::XObject, ReachabilityFlag)   == 36,                            \
                  "XObject.ReachabilityFlag offset lock (Rev 3 per "                               \
                  "FIX-M-R2-3; consumes former _padObjectFlags slot).");                           \
    static_assert(offsetof(::XCore::XObject, _reservedCluster0)  == 40,                            \
                  "XObject._reservedCluster0 offset lock.");                                       \
    static_assert(offsetof(::XCore::XObject, _reservedCluster1)  == 48,                            \
                  "XObject._reservedCluster1 offset lock.");                                       \
    /* ===== FXObjectArrayEntry (per spec §3.3 + §11.3) ===== */                                   \
    static_assert(sizeof(::XCore::FXObjectArrayEntry)            == 32,                            \
                  "FXObjectArrayEntry ABI lock: 32 bytes (1/2 cache line) "                        \
                  "per XCoreXObject Rev 4 §3.3.");                                                 \
    static_assert(alignof(::XCore::FXObjectArrayEntry)           ==  8,                            \
                  "FXObjectArrayEntry alignment lock.");                                           \
    static_assert(offsetof(::XCore::FXObjectArrayEntry, Object)            ==  0,                  \
                  "FXObjectArrayEntry.Object offset lock.");                                       \
    static_assert(offsetof(::XCore::FXObjectArrayEntry, SerialNumber)      ==  8,                  \
                  "FXObjectArrayEntry.SerialNumber offset lock.");                                 \
    static_assert(offsetof(::XCore::FXObjectArrayEntry, ClusterRootIndex)  == 12,                  \
                  "FXObjectArrayEntry.ClusterRootIndex offset lock.");                             \
    static_assert(offsetof(::XCore::FXObjectArrayEntry, StateBits)         == 16,                  \
                  "FXObjectArrayEntry.StateBits offset lock (Rev 3 per "                           \
                  "FIX-M-R2-3: rotating reachability flag MOVED to "                               \
                  "XObject@36).");                                                                 \
    static_assert(offsetof(::XCore::FXObjectArrayEntry, _reserved)         == 24,                  \
                  "FXObjectArrayEntry._reserved offset lock.");                                    \
    /* ===== FStruct + FClass cascade pins (Rev 13.9; per spec §11.3) ===== */                     \
    static_assert(sizeof(::XCore::Reflect::FStruct)              == 120,                           \
                  "FStruct ABI lock (Rev 13.9 micro-bump: 112 baseline + "                         \
                  "8 byte RefSchema appended at offset 112).");                                    \
    static_assert(offsetof(::XCore::Reflect::FStruct, RefSchema) == 112,                           \
                  "FStruct.RefSchema offset lock (Rev 3 per FIX-H-R2-1; "                          \
                  "appended after SerializeStructFn@104).");                                       \
    static_assert(sizeof(::XCore::Reflect::FClass)               == 240,                           \
                  "FClass ABI lock (Rev 13.9 micro-bump: 224 baseline + "                          \
                  "16 net = 8 RefSchema in FStruct base + 8 LifecycleTable "                       \
                  "in FClass-specific; Rev 3 corrected arithmetic per "                            \
                  "FIX-C-R2-1).");                                                                 \
    static_assert(offsetof(::XCore::Reflect::FClass, LifecycleTable) == 232,                       \
                  "FClass.LifecycleTable offset lock (Rev 3 per FIX-H-R2-2; "                      \
                  "FClass-absolute = 120 FStruct base + 112 FClass-specific "                      \
                  "offset).");                                                                     \
    /* ===== EObjectFlags plugin-range bit position locks                                      */  \
    /*       (Rev 3 per FIX-M-R2-2; bits 24..31 reserved for plugins)  ===== */                    \
    static_assert(::XCore::ToUnderlying(::XCore::EObjectFlags::UserFlag_1) == (1u << 24),          \
                  "EObjectFlags::UserFlag_1 bit lock (Rev 3 plugin range).");                      \
    static_assert(::XCore::ToUnderlying(::XCore::EObjectFlags::UserFlag_8) == (1u << 31),          \
                  "EObjectFlags::UserFlag_8 bit lock (Rev 3 plugin range top).");                  \
    static_assert(::XCore::ToUnderlying(::XCore::EObjectFlags::MarkedAsGarbage) == (1u << 12),     \
                  "EObjectFlags::MarkedAsGarbage bit lock (Rev 2 per "                             \
                  "FIX-A-HIGH-19).");                                                              \
    /* ===== XPACT_*_LAYOUT_TAG string-equality contract checks =====                          */  \
    /*       (catches drift between this header / AbiLayoutPins.cs /                           */  \
    /*        ContractSurface.cs when one source is edited but not                             */  \
    /*        the others)                                                                      */  \
    static_assert(::XPactDetail::CompileTimeStrEq(                                                 \
                      XPACT_XOBJECT_LAYOUT_TAG,                                                    \
                      "XObject-v2: 56 bytes; ClassPrivate@0, InternalIndex@8, "                    \
                      "SerialNumber@12, Outer@16, NamePrivate@24, ObjectFlags@32, "                \
                      "ReachabilityFlag@36 (Rev 3 per FIX-M-R2-3 moved from "                      \
                      "FXObjectArrayEntry), _reservedCluster0@40, _reservedCluster1@48; "          \
                      "alignof = 8; no virtuals on the GC-relevant surface "                       \
                      "(FakeVTable pattern). Rev 2: cluster reservation expanded "                 \
                      "to 16 bytes; remote-id reservation dropped (FIX-A-MIN-49 / O5). "           \
                      "Rev 3: per-object reachability flag consumes former "                       \
                      "_padObjectFlags slot."),                                                    \
                  "XPACT_XOBJECT_LAYOUT_TAG string mismatch -- one of the three "                  \
                  "sources (XReflectionRuntime.h / AbiLayoutPins.cs / "                            \
                  "ContractSurface.cs) drifted from the contract.");                               \
    static_assert(::XPactDetail::CompileTimeStrEq(                                                 \
                      XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG,                                         \
                      "FXObjectArrayEntry-v1: 32 bytes; Object@0, SerialNumber@8, "                \
                      "ClusterRootIndex@12, StateBits@16 (atomic; pending-destroy + "              \
                      "root-pinned + hot-reload + garbage bits), _reserved@24; "                   \
                      "alignof = 8. Rev 3: rotating reachability flag moved to "                   \
                      "XObject header per FIX-M-R2-3."),                                           \
                  "XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG string mismatch.");                         \
    /* ===== Object handle pins (Phase 5.c per spec §6 + §11.3) ===== */                           \
    static_assert(sizeof(::XCore::XObjectKey)                  ==  8,                              \
                  "XObjectKey ABI lock (8 bytes; XCoreXObject Rev 4 §6.4).");                      \
    static_assert(alignof(::XCore::XObjectKey)                 ==  4,                              \
                  "XObjectKey ABI lock: 4-byte alignment.");                                       \
    static_assert(offsetof(::XCore::XObjectKey, InternalIndex) ==  0,                              \
                  "XObjectKey.InternalIndex offset lock.");                                        \
    static_assert(offsetof(::XCore::XObjectKey, SerialNumber)  ==  4,                              \
                  "XObjectKey.SerialNumber offset lock.");                                         \
    static_assert(sizeof(::XCore::XPtr<::XCore::XObject>)      ==  8,                              \
                  "XPtr<XObject> ABI lock (8 bytes; raw-T*-compatible; "                           \
                  "XCoreXObject Rev 4 §6.1).");                                                    \
    static_assert(alignof(::XCore::XPtr<::XCore::XObject>)     ==  8,                              \
                  "XPtr<XObject> ABI lock: 8-byte alignment.");                                    \
    static_assert(offsetof(::XCore::XPtr<::XCore::XObject>, Ptr) == 0,                             \
                  "XPtr<XObject>.Ptr offset lock (reinterpret_cast-"                               \
                  "compatible with XObject**).");                                                  \
    static_assert(sizeof(::XCore::XWeakPtr<::XCore::XObject>)  ==  8,                              \
                  "XWeakPtr<XObject> ABI lock (8 bytes; matches "                                  \
                  "FWeakObjectPtr; XCoreXObject Rev 4 §6.2).");                                    \
    static_assert(alignof(::XCore::XWeakPtr<::XCore::XObject>) ==  4,                              \
                  "XWeakPtr<XObject> ABI lock: 4-byte alignment.");                                \
    static_assert(offsetof(::XCore::XWeakPtr<::XCore::XObject>, InternalIndex) == 0,               \
                  "XWeakPtr<XObject>.InternalIndex offset lock.");                                 \
    static_assert(offsetof(::XCore::XWeakPtr<::XCore::XObject>, SerialNumber)  == 4,               \
                  "XWeakPtr<XObject>.SerialNumber offset lock.");                                  \
    static_assert(sizeof(::XCore::XStrongPtr<::XCore::XObject>) == 8,                              \
                  "XStrongPtr<XObject> ABI lock (8 bytes; raw-T* storage; "                        \
                  "XCoreXObject Rev 4 §6.5 / FIX-A-MIN-38).");                                     \
    static_assert(alignof(::XCore::XStrongPtr<::XCore::XObject>) == 8,                             \
                  "XStrongPtr<XObject> ABI lock: 8-byte alignment.");                              \
    static_assert(offsetof(::XCore::XStrongPtr<::XCore::XObject>, Ptr) == 0,                       \
                  "XStrongPtr<XObject>.Ptr offset lock.");                                         \
    /* ===== Object-handle tag-string equality contract checks ===== */                            \
    static_assert(::XPactDetail::CompileTimeStrEq(                                                 \
                      XPACT_XOBJECTKEY_LAYOUT_TAG,                                                 \
                      "XObjectKey-v1: 8 bytes; InternalIndex@0 (int32), "                          \
                      "SerialNumber@4 (uint32); ABI-compatible with XWeakPtr; "                    \
                      "alignof = 4"),                                                              \
                  "XPACT_XOBJECTKEY_LAYOUT_TAG string mismatch -- one of the "                     \
                  "three sources (XReflectionRuntime.h / AbiLayoutPins.cs / "                      \
                  "ContractSurface.cs) drifted from the contract.");                               \
    static_assert(::XPactDetail::CompileTimeStrEq(                                                 \
                      XPACT_XWEAKPTR_LAYOUT_TAG,                                                   \
                      "XWeakPtr-v1: 8 bytes; InternalIndex@0 (int32), "                            \
                      "SerialNumber@4 (uint32); matches XCore-4b's "                               \
                      "FWeakObjectPtr placeholder shape; alignof = 4"),                            \
                  "XPACT_XWEAKPTR_LAYOUT_TAG string mismatch.");                                   \
    static_assert(::XPactDetail::CompileTimeStrEq(                                                 \
                      XPACT_XPTR_LAYOUT_TAG,                                                       \
                      "XPtr-v1: 8 bytes; Ptr@0 (raw T* compatible); "                              \
                      "ABI-equivalent to T*; alignof = 8"),                                        \
                  "XPACT_XPTR_LAYOUT_TAG string mismatch.")                                        \
    /* Deliberate no-semicolon terminator: the macro is used as                              */    \
    /*   `XPACT_VERIFY_XOBJECT_LAYOUT();`                                                    */    \
    /* at the call site; the trailing static_assert above ends with `)`                      */    \
    /* and the call-site semicolon closes it.                                                */

// ---------------------------------------------------------------------
// Remaining deferred pins (documented here so the macro grows
// idempotently as later phases ship):
//
//   * FXObjectLifecycleTable    (Phase 5.d):
//       static_assert(sizeof(::XCore::FXObjectLifecycleTable) == 72)
//   * FXObjectRefSchema         (Phase 5.g'):
//       static_assert(sizeof(::XCore::Reflect::FXObjectRefSchema) == 24)
//   * FXObjectRefSchemaOp       (Phase 5.g'):
//       static_assert(sizeof(::XCore::Reflect::FXObjectRefSchemaOp) == 24)
//
// Phase 5.c LANDED (in the macro above):
//   * XObjectKey                @ 8 bytes
//   * XPtr<XObject>             @ 8 bytes
//   * XWeakPtr<XObject>         @ 8 bytes
//   * XStrongPtr<XObject>       @ 8 bytes
//   * XPACT_XOBJECTKEY_LAYOUT_TAG / XPACT_XWEAKPTR_LAYOUT_TAG /
//     XPACT_XPTR_LAYOUT_TAG string-equality contract checks.
//
// XSoftPtr is deliberately OUTSIDE the macro pin set per spec §6.3
// (variable-size; not part of the ABI-lock set).
//
// When each phase ships, append the matching static_assert to the
// XPACT_VERIFY_XOBJECT_LAYOUT macro above. The macro is intentionally
// the single point of edit so the additive growth is auditable in
// a single diff.
// ---------------------------------------------------------------------
