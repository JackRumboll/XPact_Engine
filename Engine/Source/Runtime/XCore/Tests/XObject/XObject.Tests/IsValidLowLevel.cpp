// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/IsValidLowLevel.cpp -- defensive validity probe
// (XCoreXObject Rev 4 §2.5).
// =====================================================================
//
// Phase 5.a SCOPE: the IsValidLowLevel probe verifies local-state
// invariants (this != nullptr, ClassPrivate plausible, InternalIndex
// in range). The FXObjectArray cross-check (verify the array's entry
// at InternalIndex points back to `this` and the captured
// SerialNumber matches) is deferred to Phase 5.c when FXObjectArray
// ships.
//
// This test exercises the Phase 5.a probe subset.
//
// =====================================================================

#include "XObject/XObject.h"

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
    using ::XCore::XObject;

    // -----------------------------------------------------------------
    // Default-constructed XObject: ClassPrivate is nullptr -> probe
    // returns false.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        Check(!Obj.IsValidLowLevel(),
              "default-ctor IsValidLowLevel() should be false (ClassPrivate nullptr)");
    }

    // -----------------------------------------------------------------
    // ClassPrivate populated with a dummy non-null pointer: probe
    // returns true (the probe only checks non-null; type identity is
    // not validated at this level).
    //
    // NOTE: we cannot construct a real FClass at Phase 5.a (the
    // FClass static initialisers ship via XHT-emitted .gen.cpp;
    // production tests of IsValidLowLevel would route through
    // NewObject which lands at Phase 5.b+). We use a stack-allocated
    // address as a non-null pointer surrogate.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        // Cast a stack address to const FClass*. Phase 5.a IsValid-
        // LowLevel only probes nullptr-ness of ClassPrivate.
        ::std::uint64_t DummyClassStorage = 0;
        Obj.ClassPrivate =
            reinterpret_cast<const ::XCore::Reflect::FClass*>(&DummyClassStorage);
        Check(Obj.IsValidLowLevel(),
              "IsValidLowLevel() should be true with non-null ClassPrivate "
              "and default InternalIndex (INDEX_NONE)");
        // Reset before the XObject destructor runs (the dangling
        // dummy pointer goes out of scope).
        Obj.ClassPrivate = nullptr;
    }

    // -----------------------------------------------------------------
    // InternalIndex out-of-range (< INDEX_NONE) -> probe returns
    // false. INDEX_NONE is -1; any value below -1 is invalid.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::std::uint64_t DummyClassStorage = 0;
        Obj.ClassPrivate =
            reinterpret_cast<const ::XCore::Reflect::FClass*>(&DummyClassStorage);
        Obj.InternalIndex = -2;
        Check(!Obj.IsValidLowLevel(),
              "IsValidLowLevel() should be false with InternalIndex < INDEX_NONE");
        Obj.ClassPrivate  = nullptr;
        Obj.InternalIndex = ::INDEX_NONE;
    }

    // -----------------------------------------------------------------
    // Positive InternalIndex with valid ClassPrivate -> probe returns
    // true. The FXObjectArray cross-check is deferred to Phase 5.c;
    // until then the probe is conservatively true.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::std::uint64_t DummyClassStorage = 0;
        Obj.ClassPrivate =
            reinterpret_cast<const ::XCore::Reflect::FClass*>(&DummyClassStorage);
        Obj.InternalIndex = 12345;
        Check(Obj.IsValidLowLevel(),
              "IsValidLowLevel() should be true with positive InternalIndex");
        Obj.ClassPrivate = nullptr;
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XObject.IsValidLowLevel: PASS\n";
        return 0;
    }
    std::cerr << "XObject.IsValidLowLevel: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
