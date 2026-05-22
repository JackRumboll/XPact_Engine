// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// MallocOrAbort.cpp -- abort-on-null wrapper smoke test.
// =====================================================================
//
// XCore-4a Section 17.2 B8 (fix M-2): "TArray::Add and FString::Append
// under simulated OOM (allocator wedged to return null) abort cleanly
// via MallocOrAbort in the Dev configuration. The abort message names
// the offending tag and the requested size; no half-state container
// is left visible to the caller."
//
// This test does NOT trigger the OOM path directly (we cannot wedge
// the production allocator in-process without OS hooks). Instead, it
// verifies the happy path: MallocOrAbort returns a valid pointer when
// the underlying Malloc succeeds. The actual death-test for the abort
// path lives as a TODO(Phase 1c) -- the proper way to exercise it is
// an out-of-process fork + assert-exit-code fixture which requires
// the FPlatformProcess::CreateProc surface (Step 2 Platform HAL not
// shipped yet).
//
// The test also verifies that MallocOrAbort accepts any FMemTag from
// the engine slot range and that the returned pointer is alignable
// and freeable.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    FMemory::__Init();

    // Happy path: MallocOrAbort returns a non-null pointer for a
    // reasonable allocation.
    void* P1 = FMemory::MallocOrAbort(128, 16, FMemTag::Container);
    if (P1 == nullptr)
    {
        std::fprintf(stderr, "FAIL: MallocOrAbort returned null in non-OOM scenario\n");
        return 1;
    }
    FMemory::Free(P1);

    // Exercise each engine tag.
    const FMemTag Tags[] = {
        FMemTag::Container, FMemTag::Math, FMemTag::Platform,
        FMemTag::Threading, FMemTag::CVar, FMemTag::Stat,
        FMemTag::Localization
    };
    for (FMemTag Tag : Tags)
    {
        void* P = FMemory::MallocOrAbort(64, 8, Tag);
        if (P == nullptr)
        {
            std::fprintf(stderr, "FAIL: MallocOrAbort returned null for tag %u\n",
                         static_cast<unsigned>(Tag));
            return 1;
        }
        FMemory::Free(P);
    }

    // TODO(Phase 1c): out-of-process death test for the actual abort
    // path under FOOMPolicy::ReturnNull when the underlying Malloc
    // returns null. Requires FPlatformProcess::CreateProc (Step 2
    // Platform HAL Phase 1b).

    FMemory::__Shutdown();
    std::printf("MallocOrAbort: PASS\n");
    return 0;
}
