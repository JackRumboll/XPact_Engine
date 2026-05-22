// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// MallocAlignments.cpp -- Malloc with each alignment in {1, 8, 16, 32, 64}.
// =====================================================================
//
// XCore-4a Section 4.6 Unit test: "Malloc/Free/Realloc per align in
// {1, 8, 16, 32, 64}". Asserts the returned pointer is correctly
// aligned and freeable.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <cstdint>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    FMemory::__Init();

    // The spec asks for align in {1, 8, 16, 32, 64}. The allocator
    // normalises align < 8 to 8 (Section 4.6 + impl in
    // FMallocBinnedX::Malloc). So align=1 effectively returns an
    // 8-aligned pointer; we test that explicitly.
    const ::SIZE_T Aligns[] = { 1, 8, 16, 32, 64 };

    for (::SIZE_T Align : Aligns)
    {
        // Test a small + large allocation per alignment.
        const ::SIZE_T Sizes[] = { 64, 256, 4096, 17000 };
        for (::SIZE_T Size : Sizes)
        {
            void* P = FMemory::Malloc(Size, Align, FMemTag::Container);
            if (P == nullptr)
            {
                std::fprintf(stderr, "FAIL: Malloc returned null (size=%llu align=%llu)\n",
                             static_cast<unsigned long long>(Size),
                             static_cast<unsigned long long>(Align));
                return 1;
            }

            // The smallest effective alignment is 8 (the allocator's
            // floor; Section 4.6 spec body).
            const ::SIZE_T EffectiveAlign = (Align < 8) ? 8 : Align;
            const ::uintptr_t Addr = reinterpret_cast<::uintptr_t>(P);
            if ((Addr & (EffectiveAlign - 1)) != 0)
            {
                std::fprintf(stderr,
                             "FAIL: pointer 0x%llx not aligned to %llu (size=%llu)\n",
                             static_cast<unsigned long long>(Addr),
                             static_cast<unsigned long long>(EffectiveAlign),
                             static_cast<unsigned long long>(Size));
                return 1;
            }

            FMemory::Free(P);
        }
    }

    // Round-trip accounting.
    if (FMemory::GetAllocatedBytes(FMemTag::Container) != 0)
    {
        std::fprintf(stderr, "FAIL: GetAllocatedBytes(Container) non-zero after all frees\n");
        return 1;
    }

    FMemory::__Shutdown();
    std::printf("MallocAlignments: PASS\n");
    return 0;
}
