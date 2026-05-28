// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStackRootScaffolding.Tests/RegisterMap.cpp -- Register/Unregister
// round trip (XCoreXObject Rev 4 §5.4; Phase 5.e).
// =====================================================================
//
// RegisterStackRootMap appends the map pointer to the process-global
// registry; UnregisterStackRootMap removes it. The
// GetRegisteredStackRootMapCount accessor reflects the active count.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XStackRootScaffolding.h"

#include <iostream>

namespace
{
    // Static FStackRootMap fixtures emulating XIL2CPP's per-function
    // .rodata layout. The Safepoints arrays are stub-shaped (zero
    // entries) because Phase 5.e only ships the storage + registration
    // surface; the actual safepoint-walker lands at Phase 5.g.
    static const ::XCore::Detail::FStackRootMap s_MapA{
        /*FunctionAddress*/ reinterpret_cast<const void*>(static_cast<::uintptr_t>(0x1000)),
        /*SafepointCount */ 0,
        /*_pad           */ 0,
        /*Safepoints     */ nullptr,
    };
    static const ::XCore::Detail::FStackRootMap s_MapB{
        reinterpret_cast<const void*>(static_cast<::uintptr_t>(0x2000)),
        0,
        0,
        nullptr,
    };
    static const ::XCore::Detail::FStackRootMap s_MapC{
        reinterpret_cast<const void*>(static_cast<::uintptr_t>(0x3000)),
        0,
        0,
        nullptr,
    };
}

int main()
{
    using ::XCore::Detail::GetRegisteredStackRootMapCount;
    using ::XCore::Detail::RegisterStackRootMap;
    using ::XCore::Detail::UnregisterStackRootMap;
    using ::XCore::Detail::__ResetStackRootRegistryForTests;

    ::XCore::HAL::FMemory::__Init();
    __ResetStackRootRegistryForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Baseline.
    Check(GetRegisteredStackRootMapCount() == 0u,
          "baseline count != 0");

    // Register A; count == 1.
    RegisterStackRootMap(&s_MapA);
    Check(GetRegisteredStackRootMapCount() == 1u, "count != 1 after Register A");

    // Register A again: idempotent (no-op).
    RegisterStackRootMap(&s_MapA);
    Check(GetRegisteredStackRootMapCount() == 1u,
          "count != 1 after duplicate Register A");

    // Register B + C.
    RegisterStackRootMap(&s_MapB);
    RegisterStackRootMap(&s_MapC);
    Check(GetRegisteredStackRootMapCount() == 3u,
          "count != 3 after Register A+B+C");

    // Unregister B.
    UnregisterStackRootMap(&s_MapB);
    Check(GetRegisteredStackRootMapCount() == 2u,
          "count != 2 after Unregister B");

    // Re-unregister B: no-op.
    UnregisterStackRootMap(&s_MapB);
    Check(GetRegisteredStackRootMapCount() == 2u,
          "count != 2 after duplicate Unregister B");

    // Unregister non-registered nullptr: no-op.
    UnregisterStackRootMap(nullptr);
    Check(GetRegisteredStackRootMapCount() == 2u,
          "count changed after Unregister nullptr");

    // Register nullptr: no-op.
    RegisterStackRootMap(nullptr);
    Check(GetRegisteredStackRootMapCount() == 2u,
          "count changed after Register nullptr");

    // Cleanup.
    UnregisterStackRootMap(&s_MapA);
    UnregisterStackRootMap(&s_MapC);
    Check(GetRegisteredStackRootMapCount() == 0u,
          "post-cleanup count != 0");

    __ResetStackRootRegistryForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStackRootScaffolding.RegisterMap: PASS\n";
        return 0;
    }
    std::cerr << "XStackRootScaffolding.RegisterMap: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
