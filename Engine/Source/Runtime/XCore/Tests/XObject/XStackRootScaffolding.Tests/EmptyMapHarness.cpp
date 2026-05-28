// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStackRootScaffolding.Tests/EmptyMapHarness.cpp -- iteration on empty
// + empty-Safepoints registry (XCoreXObject Rev 4 §5.4; Phase 5.e).
// =====================================================================
//
// ForEachStackRootMap on an empty registry is a no-op (visitor never
// called). After registering empty-Safepoints maps, the visitor IS
// called per map; the visitor MAY observe SafepointCount == 0 +
// Safepoints == nullptr (the empty-map shape XIL2CPP would never
// emit, but the test exercises the registry boundary).
//
// This test validates the Phase 5.g consumer interface: the GC mark
// phase walker must handle an empty stack-root-map registry without
// crashing.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XStackRootScaffolding.h"

#include <iostream>

namespace
{
    static const ::XCore::Detail::FStackRootMap s_EmptyMap1{
        reinterpret_cast<const void*>(static_cast<::uintptr_t>(0xA000)),
        /*SafepointCount*/ 0,
        /*_pad         */ 0,
        /*Safepoints   */ nullptr,
    };
    static const ::XCore::Detail::FStackRootMap s_EmptyMap2{
        reinterpret_cast<const void*>(static_cast<::uintptr_t>(0xB000)),
        0,
        0,
        nullptr,
    };
}

int main()
{
    using ::XCore::Detail::FStackRootMap;
    using ::XCore::Detail::ForEachStackRootMap;
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

    // -----------------------------------------------------------------
    // Iteration on empty registry: visitor never called.
    // -----------------------------------------------------------------
    int VisitCount = 0;
    ForEachStackRootMap(
        [&](const FStackRootMap* /*Map*/) noexcept
        {
            ++VisitCount;
        });
    Check(VisitCount == 0,
          "empty-registry ForEach called visitor");

    // -----------------------------------------------------------------
    // Register two empty maps; iterate; visitor called twice.
    // -----------------------------------------------------------------
    RegisterStackRootMap(&s_EmptyMap1);
    RegisterStackRootMap(&s_EmptyMap2);
    Check(GetRegisteredStackRootMapCount() == 2u,
          "count != 2 after registering 2 maps");

    VisitCount = 0;
    bool SeenMap1 = false, SeenMap2 = false;
    ForEachStackRootMap(
        [&](const FStackRootMap* Map) noexcept
        {
            ++VisitCount;
            if (Map == &s_EmptyMap1)      SeenMap1 = true;
            else if (Map == &s_EmptyMap2) SeenMap2 = true;
            // Validate the map shape we registered: empty safepoints.
            if (Map->SafepointCount != 0u)
            {
                ++FailureCount;
                std::cerr << "FAIL: map had non-zero SafepointCount\n";
            }
            if (Map->Safepoints != nullptr)
            {
                ++FailureCount;
                std::cerr << "FAIL: map had non-null Safepoints\n";
            }
        });
    Check(VisitCount == 2, "ForEach visit count != 2");
    Check(SeenMap1, "Map1 not visited");
    Check(SeenMap2, "Map2 not visited");

    // -----------------------------------------------------------------
    // After Unregister, ForEach skips the removed map.
    // -----------------------------------------------------------------
    UnregisterStackRootMap(&s_EmptyMap1);
    VisitCount = 0;
    ForEachStackRootMap(
        [&](const FStackRootMap* Map) noexcept
        {
            ++VisitCount;
            Check(Map == &s_EmptyMap2,
                  "unregistered Map1 was visited");
        });
    Check(VisitCount == 1, "post-Unregister visit count != 1");

    // Cleanup.
    UnregisterStackRootMap(&s_EmptyMap2);
    __ResetStackRootRegistryForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStackRootScaffolding.EmptyMapHarness: PASS\n";
        return 0;
    }
    std::cerr << "XStackRootScaffolding.EmptyMapHarness: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
