// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// X1_XObjectSizeofLocks.cpp -- Foundation Prototype X1 acceptance:
// sizeof(XObject) == 56 + offset locks per spec §11.3 on every
// supported compiler (MSVC 19.44+, Clang 17+, GCC 13+).
// =====================================================================
//
// X1 acceptance gate (spec §13.2):
//   "sizeof(XObject) == 56; offset locks per §11.3 hold on every
//    supported compiler (MSVC 19.44+, Clang 17+, GCC 13+)."
//
// The header-level static_asserts in Public/XObject/XObject.h are the
// PRIMARY ABI lock; a build that produces a different XObject layout
// than the addendum fails to compile. This runtime gate is the
// belt-and-braces CI artifact that confirms the static_asserts ran +
// the runtime sizeof matches.
//
// This test extends the Phase 5.a XObject.Tests/SizeofAndAlignof.cpp
// pattern with explicit Foundation Prototype X1 framing.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "XObject/FXObjectArrayEntry.h"
#include "XObject/XObject.h"

#include <cstddef>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::XObject;
    using ::XCore::FXObjectArrayEntry;

    Phase5L::ResetAllForTests();

    // -----------------------------------------------------------------
    // sizeof + alignof locks (X1 primary acceptance).
    // -----------------------------------------------------------------
    P5L_CHECK(sizeof(XObject) == 56,
              "X1: sizeof(XObject) != 56");
    P5L_CHECK(alignof(XObject) == 8,
              "X1: alignof(XObject) != 8");

    // -----------------------------------------------------------------
    // Per-member offset locks (spec §2.2 + §11.3 ABI table).
    // -----------------------------------------------------------------
    P5L_CHECK(offsetof(XObject, ClassPrivate) == 0,
              "X1: offsetof(ClassPrivate) != 0");
    P5L_CHECK(offsetof(XObject, InternalIndex) == 8,
              "X1: offsetof(InternalIndex) != 8");
    P5L_CHECK(offsetof(XObject, SerialNumber) == 12,
              "X1: offsetof(SerialNumber) != 12");
    P5L_CHECK(offsetof(XObject, Outer) == 16,
              "X1: offsetof(Outer) != 16");
    P5L_CHECK(offsetof(XObject, NamePrivate) == 24,
              "X1: offsetof(NamePrivate) != 24");
    P5L_CHECK(offsetof(XObject, ObjectFlags) == 32,
              "X1: offsetof(ObjectFlags) != 32");
    P5L_CHECK(offsetof(XObject, ReachabilityFlag) == 36,
              "X1: offsetof(ReachabilityFlag) != 36");
    P5L_CHECK(offsetof(XObject, _reservedCluster0) == 40,
              "X1: offsetof(_reservedCluster0) != 40");
    P5L_CHECK(offsetof(XObject, _reservedCluster1) == 48,
              "X1: offsetof(_reservedCluster1) != 48");

    // -----------------------------------------------------------------
    // Per-member sizeof locks.
    // -----------------------------------------------------------------
    P5L_CHECK(sizeof(static_cast<XObject*>(nullptr)->ClassPrivate) == 8,
              "X1: sizeof(XObject::ClassPrivate) != 8");
    P5L_CHECK(sizeof(static_cast<XObject*>(nullptr)->InternalIndex) == 4,
              "X1: sizeof(XObject::InternalIndex) != 4");
    P5L_CHECK(sizeof(static_cast<XObject*>(nullptr)->SerialNumber) == 4,
              "X1: sizeof(XObject::SerialNumber) != 4");

    // -----------------------------------------------------------------
    // FXObjectArrayEntry parallel lock: spec §3.3 + §11.3 pins the
    // parallel entry struct at 32 bytes.
    // -----------------------------------------------------------------
    P5L_CHECK(sizeof(FXObjectArrayEntry) == 32,
              "X1 collateral: sizeof(FXObjectArrayEntry) != 32");

    // -----------------------------------------------------------------
    // Non-polymorphism trait (XObject MUST NOT carry a vtable; spec
    // §2.2 hot-reload safety + Prime Directive).
    // -----------------------------------------------------------------
    P5L_CHECK(!::std::is_polymorphic_v<XObject>,
              "X1: XObject must not be polymorphic (hot-reload invariant)");

    std::cout << "X1: sizeof(XObject) = " << sizeof(XObject)
              << ", alignof(XObject) = " << alignof(XObject) << "\n";

    return P5L_REPORT_PASS("FoundationPrototype.X1_XObjectSizeofLocks");
}
