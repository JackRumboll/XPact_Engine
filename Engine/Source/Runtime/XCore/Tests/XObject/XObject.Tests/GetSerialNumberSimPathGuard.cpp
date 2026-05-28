// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/GetSerialNumberSimPathGuard.cpp -- documents the
// sim-path guard contract on XObject::GetSerialNumber
// (XCoreXObject Rev 4 §2.2 + FIX-A-CRIT-2).
// =====================================================================
//
// CONTRACT (per spec §2.2 + FIX-A-CRIT-2 cross-arch determinism
// invariant):
//
//   uint32_t XObject::GetSerialNumber() const noexcept {
//       XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU());
//       return SerialNumber;
//   }
//
// Sim-path TUs MAY NOT read the SerialNumber. The slot reuse cadence
// is non-deterministic relative to the sim-tick boundary; reading it
// would couple sim-path decisions to the GC's reclaim timing which
// differs across architectures with different GC cycle costs (Win64
// x86_64 vs Quest 3 ARM64).
//
// PHASE 5.a SCOPE (deliberate deferral; mirrors
// MarkAsGarbageSimPathGuard.cpp):
//
// The runtime SimPathTU probe (::XCore::HAL::IsSimPathTU) does NOT
// exist at Phase 5.a. The Phase 5.a test verifies the non-sim-path
// happy path. The full sim-path negative test ships at the future
// phase that lands the probe.
//
// =====================================================================

#include "XObject/XObject.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;

    // -----------------------------------------------------------------
    // Non-sim-path happy path: GetSerialNumber returns 0 for a fresh
    // XObject (SerialNumber default-initialised to 0).
    // -----------------------------------------------------------------
    XObject Obj;

    if (Obj.GetSerialNumber() != 0u)
    {
        std::cerr << "FAIL: fresh XObject SerialNumber != 0\n";
        return 1;
    }

    // Direct field access (the test TU is non-sim-path so reading
    // SerialNumber via the accessor is allowed). Verify that the
    // accessor returns the same value the field holds.
    Obj.SerialNumber = 42u;
    if (Obj.GetSerialNumber() != 42u)
    {
        std::cerr << "FAIL: SerialNumber accessor did not return 42 after "
                  << "direct field write\n";
        return 1;
    }

    // -----------------------------------------------------------------
    // InternalIndex parallel-accessor sanity check (same memory layout
    // pattern; the spec §2.5 also lists GetInternalIndex).
    // -----------------------------------------------------------------
    if (Obj.GetInternalIndex() != ::INDEX_NONE)
    {
        std::cerr << "FAIL: fresh XObject InternalIndex != INDEX_NONE\n";
        return 1;
    }
    Obj.InternalIndex = 123;
    if (Obj.GetInternalIndex() != 123)
    {
        std::cerr << "FAIL: InternalIndex accessor did not return 123 after "
                  << "direct field write\n";
        return 1;
    }

    std::cout << "XObject.GetSerialNumberSimPathGuard: PASS\n"
              << "  NOTE: sim-path negative test deferred to the "
              << "future phase that lands ::XCore::HAL::IsSimPathTU "
              << "runtime probe (per spec §2.2 + FIX-A-CRIT-2 "
              << "cross-arch determinism invariant).\n";
    return 0;
}
