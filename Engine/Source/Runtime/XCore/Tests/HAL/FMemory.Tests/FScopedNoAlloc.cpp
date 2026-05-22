// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FScopedNoAlloc.cpp -- sim-path alloc-free scope contract test.
// =====================================================================
//
// XCore-4a Section 4.3 / fix A-MIN4: in Debug/Dev, an attempted Malloc
// inside a FScopedNoAlloc scope aborts. Outside the scope, the allocator
// behaves normally.
//
// Phase 1b verifies:
//   * The scope's IsNoAllocScopeActive() returns true while live, false
//     after destruction.
//   * Nested scopes increment/decrement correctly.
//   * Malloc outside the scope succeeds.
//
// The actual abort-on-Malloc-inside-scope test is an out-of-process
// death test (the abort terminates the test runner). TODO(Phase 1c)
// once FPlatformProcess::CreateProc lands; for Phase 1b we exercise
// the scope's bookkeeping directly.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#include <cstdio>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    FMemory::__Init();

    // Outside scope: false.
    if (FMemory::IsNoAllocScopeActive())
    {
        std::fprintf(stderr, "FAIL: IsNoAllocScopeActive true before scope entered\n");
        return 1;
    }

    {
        FMemory::FScopedNoAlloc Guard;
#if XPACT_DEBUG || XPACT_DEVELOPMENT
        if (!FMemory::IsNoAllocScopeActive())
        {
            std::fprintf(stderr, "FAIL: IsNoAllocScopeActive false inside scope (Debug/Dev)\n");
            return 1;
        }
#else
        // In Shipping/Test, FScopedNoAlloc is a no-op; the active
        // check stays false.
        if (FMemory::IsNoAllocScopeActive())
        {
            std::fprintf(stderr, "FAIL: IsNoAllocScopeActive true inside scope (Shipping/Test)\n");
            return 1;
        }
#endif

        // Nested scope.
        {
            FMemory::FScopedNoAlloc InnerGuard;
#if XPACT_DEBUG || XPACT_DEVELOPMENT
            if (!FMemory::IsNoAllocScopeActive())
            {
                std::fprintf(stderr, "FAIL: IsNoAllocScopeActive false in nested scope\n");
                return 1;
            }
#endif
        }

#if XPACT_DEBUG || XPACT_DEVELOPMENT
        // After inner destructs, outer is still active.
        if (!FMemory::IsNoAllocScopeActive())
        {
            std::fprintf(stderr, "FAIL: IsNoAllocScopeActive false after inner destructed\n");
            return 1;
        }
#endif
    }

    // After outer destructs, inactive.
    if (FMemory::IsNoAllocScopeActive())
    {
        std::fprintf(stderr, "FAIL: IsNoAllocScopeActive true after all scopes destructed\n");
        return 1;
    }

    // Malloc outside scope works.
    void* P = FMemory::Malloc(64, 16, FMemTag::Container);
    if (P == nullptr)
    {
        std::fprintf(stderr, "FAIL: Malloc outside scope returned null\n");
        return 1;
    }
    FMemory::Free(P);

    // TODO(Phase 1c): out-of-process death test for the abort path:
    // {
    //     FMemory::FScopedNoAlloc Guard;
    //     FMemory::Malloc(64, 16, FMemTag::Container);  // must abort
    // }

    FMemory::__Shutdown();
    std::printf("FScopedNoAlloc: PASS\n");
    return 0;
}
