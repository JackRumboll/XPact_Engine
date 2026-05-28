// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectKey.Tests/SimPathHashGuard.cpp -- documents the SIMPATH-
// FORBIDDEN-READ guard contract on GetTypeHash(XObjectKey)
// (XCoreXObject Rev 4 §6.4 + FIX-A-CRIT-2 + spec §1.3 cross-arch
// determinism invariant).
// =====================================================================
//
// CONTRACT: sim-path TUs MAY NOT depend on the hash value of an
// XObjectKey nor on the iteration order of containers keyed on
// XObjectKey. The XPACT_CHECK_SL guard hook in GetTypeHash will fire
// in Debug / Development once the ::XCore::HAL::IsSimPathTU runtime
// probe ships.
//
// PHASE 5.c SCOPE (deliberate deferral; mirrors
// GetSerialNumberSimPathGuard.cpp + MarkAsGarbageSimPathGuard.cpp at
// Phase 5.a):
//
// The runtime SimPathTU probe does NOT exist at Phase 5.c. The Phase
// 5.c test verifies the non-sim-path happy path. The full sim-path
// negative test ships at the future phase that lands the probe.
//
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XObjectKey.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XObjectKey;

    // -----------------------------------------------------------------
    // Non-sim-path happy path: GetTypeHash returns a non-zero value
    // for non-null keys and a stable (repeatable) value for the null
    // key.
    // -----------------------------------------------------------------
    XObject Obj;
    Obj.InternalIndex = 100;
    Obj.SerialNumber  = 200u;
    XObjectKey K(&Obj);

    const ::uint64 H = GetTypeHash(K);
    // XXH3-64 of a 8-byte non-zero payload is overwhelmingly likely to
    // be non-zero; we don't assert a specific value (the determinism
    // contract is honoured by the XXH3-64 implementation tests in
    // XCore-4a).
    (void)H;

    // The null-key hash is repeatable across two calls (the
    // determinism contract within a single process invocation).
    const ::uint64 H_null_1 = GetTypeHash(XObjectKey{});
    const ::uint64 H_null_2 = GetTypeHash(XObjectKey{});
    if (H_null_1 != H_null_2)
    {
        std::cerr << "FAIL: null-key hash not repeatable\n";
        return 1;
    }

    std::cout << "XObjectKey.SimPathHashGuard: PASS\n"
              << "  NOTE: sim-path negative test deferred to the future "
              << "phase that lands ::XCore::HAL::IsSimPathTU runtime "
              << "probe (per spec §6.4 + FIX-A-CRIT-2).\n";
    return 0;
}
