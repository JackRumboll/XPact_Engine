// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/SizeofAndAlignof.cpp -- Phase 5.a ABI lock runtime check.
// =====================================================================
//
// XCoreXObject Rev 4 §2.2 + §11.3 + Contract Rev 13.9 layout tags
// XPACT_XOBJECT_LAYOUT_TAG + XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG.
//
// Verifies sizeof / alignof / per-member offsets at RUNTIME in a
// separate TU (not just at the header static_assert sites) to catch
// any toolchain divergence in offsetof or padding computation. The
// header-side static_asserts are the primary ABI lock; this runtime
// gate is the belt-and-braces CI artifact.
//
// =====================================================================

#include "XObject/EObjectFlags.h"
#include "XObject/FXObjectArrayEntry.h"
#include "XObject/XObject.h"

#include <cstddef>
#include <cstdint>
#include <iostream>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::FXObjectArrayEntry;
    using ::XCore::XObject;

    // -----------------------------------------------------------------
    // XObject -- 56 bytes; alignof 8; per-member offsets (per spec
    // §2.2).
    // -----------------------------------------------------------------
    Check(sizeof(XObject)  == 56, "sizeof(XObject) != 56");
    Check(alignof(XObject) ==  8, "alignof(XObject) != 8");

    Check(offsetof(XObject, ClassPrivate)      ==  0, "XObject::ClassPrivate offset != 0");
    Check(offsetof(XObject, InternalIndex)     ==  8, "XObject::InternalIndex offset != 8");
    Check(offsetof(XObject, SerialNumber)      == 12, "XObject::SerialNumber offset != 12");
    Check(offsetof(XObject, Outer)             == 16, "XObject::Outer offset != 16");
    Check(offsetof(XObject, NamePrivate)       == 24, "XObject::NamePrivate offset != 24");
    Check(offsetof(XObject, ObjectFlags)       == 32, "XObject::ObjectFlags offset != 32");
    Check(offsetof(XObject, ReachabilityFlag)  == 36, "XObject::ReachabilityFlag offset != 36");
    Check(offsetof(XObject, _reservedCluster0) == 40, "XObject::_reservedCluster0 offset != 40");
    Check(offsetof(XObject, _reservedCluster1) == 48, "XObject::_reservedCluster1 offset != 48");

    // Per-field size checks (catches a hypothetical platform that
    // packed enum-class underlying-type differently or stretched
    // std::atomic<uint32_t>).
    Check(sizeof(XObject::ClassPrivate)      == 8, "XObject::ClassPrivate not 8 bytes");
    Check(sizeof(XObject::InternalIndex)     == 4, "XObject::InternalIndex not 4 bytes");
    Check(sizeof(XObject::SerialNumber)      == 4, "XObject::SerialNumber not 4 bytes");
    Check(sizeof(XObject::Outer)             == 8, "XObject::Outer not 8 bytes");
    Check(sizeof(XObject::NamePrivate)       == 8, "XObject::NamePrivate not 8 bytes");
    Check(sizeof(XObject::ObjectFlags)       == 4, "XObject::ObjectFlags not 4 bytes");
    Check(sizeof(XObject::ReachabilityFlag)  == 4, "XObject::ReachabilityFlag not 4 bytes");
    Check(sizeof(XObject::_reservedCluster0) == 8, "XObject::_reservedCluster0 not 8 bytes");
    Check(sizeof(XObject::_reservedCluster1) == 8, "XObject::_reservedCluster1 not 8 bytes");

    // -----------------------------------------------------------------
    // FXObjectArrayEntry -- 32 bytes; alignof 8; per-member offsets
    // (per spec §3.3 + §11.3).
    // -----------------------------------------------------------------
    Check(sizeof(FXObjectArrayEntry)  == 32, "sizeof(FXObjectArrayEntry) != 32");
    Check(alignof(FXObjectArrayEntry) ==  8, "alignof(FXObjectArrayEntry) != 8");

    Check(offsetof(FXObjectArrayEntry, Object)           ==  0, "FXObjectArrayEntry::Object offset != 0");
    Check(offsetof(FXObjectArrayEntry, SerialNumber)     ==  8, "FXObjectArrayEntry::SerialNumber offset != 8");
    Check(offsetof(FXObjectArrayEntry, ClusterRootIndex) == 12, "FXObjectArrayEntry::ClusterRootIndex offset != 12");
    Check(offsetof(FXObjectArrayEntry, StateBits)        == 16, "FXObjectArrayEntry::StateBits offset != 16");
    Check(offsetof(FXObjectArrayEntry, _reserved)        == 24, "FXObjectArrayEntry::_reserved offset != 24");

    Check(sizeof(FXObjectArrayEntry::Object)           == 8, "FXObjectArrayEntry::Object not 8 bytes");
    Check(sizeof(FXObjectArrayEntry::SerialNumber)     == 4, "FXObjectArrayEntry::SerialNumber not 4 bytes");
    Check(sizeof(FXObjectArrayEntry::ClusterRootIndex) == 4, "FXObjectArrayEntry::ClusterRootIndex not 4 bytes");
    Check(sizeof(FXObjectArrayEntry::StateBits)        == 8, "FXObjectArrayEntry::StateBits not 8 bytes");
    Check(sizeof(FXObjectArrayEntry::_reserved)        == 8, "FXObjectArrayEntry::_reserved not 8 bytes");

    // -----------------------------------------------------------------
    // EObjectFlags -- 4 bytes (uint32_t underlying).
    // -----------------------------------------------------------------
    Check(sizeof(EObjectFlags) == 4, "sizeof(EObjectFlags) != 4");

    // -----------------------------------------------------------------
    // Trait checks (catches accidental introduction of virtuals or
    // non-trivial-destructor inheritance into the XObject body).
    // -----------------------------------------------------------------
    Check(!std::is_polymorphic_v<XObject>,
          "XObject must NOT be polymorphic (hot-reload commitment).");
    Check(std::is_trivially_destructible_v<XObject>,
          "XObject must be trivially destructible (FXObjectAllocator slab teardown).");
    Check(std::is_trivially_destructible_v<FXObjectArrayEntry>,
          "FXObjectArrayEntry must be trivially destructible.");

    if (g_FailureCount == 0)
    {
        std::cout << "XObject.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XObject.SizeofAndAlignof: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
