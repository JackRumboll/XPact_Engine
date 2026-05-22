// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// DefaultAllocator.Tests/TagPropagation.cpp -- per-instance FMemTag carry.
// =====================================================================
//
// XCore-4a Section 5.1 / Section 5.5 row 2: DefaultAllocator carries
// a per-instance FMemTag so a TArray<int> can be tagged with
// FMemTag::Math (or any other tag) at construction:
//
//   TArray<int, DefaultAllocator> MyArr(DefaultAllocator{FMemTag::Math});
//
// This test verifies:
//   * Default-constructed DefaultAllocator uses FMemTag::Container.
//   * Tag-taking ctor carries the requested tag.
//   * GetTag() returns the carried tag.
//   * A TArray instantiated with DefaultAllocator{Math} produces
//     allocations attributed to FMemTag::Math (verified via
//     FMemory::GetAllocatedBytes).
//
// =====================================================================

#include "Containers/DefaultAllocator.h"
#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;
    using ::XCore::DefaultAllocator;

    // ---------------------------------------------------------------
    // 1. Default ctor uses FMemTag::Container.
    // ---------------------------------------------------------------
    {
        constexpr DefaultAllocator A;
        static_assert(A.GetTag() == FMemTag::Container,
                      "Default-constructed DefaultAllocator must carry FMemTag::Container");
    }

    // ---------------------------------------------------------------
    // 2. Tag-taking ctor carries the requested tag.
    // ---------------------------------------------------------------
    {
        constexpr DefaultAllocator AMath{FMemTag::Math};
        static_assert(AMath.GetTag() == FMemTag::Math,
                      "DefaultAllocator{Math} must carry FMemTag::Math");

        constexpr DefaultAllocator AStat{FMemTag::Stat};
        static_assert(AStat.GetTag() == FMemTag::Stat,
                      "DefaultAllocator{Stat} must carry FMemTag::Stat");
    }

    // ---------------------------------------------------------------
    // 3. TArray with custom tag routes allocations to that tag.
    // ---------------------------------------------------------------
    const ::uint64 MathBytesBefore = FMemory::GetAllocatedBytes(FMemTag::Math);

    {
        // Construct a TArray with the Math tag and force at least one
        // grow to trigger an allocation.
        ::XCore::TArray<::int32, DefaultAllocator> MathArr(DefaultAllocator{FMemTag::Math});

        // Force allocation by adding elements past the first-grow
        // threshold.
        for (::int32 I = 0; I < 32; ++I)
        {
            MathArr.Add(I);
        }

        const ::uint64 MathBytesLive = FMemory::GetAllocatedBytes(FMemTag::Math);
        if (MathBytesLive <= MathBytesBefore)
        {
            std::fprintf(stderr,
                "FAIL: Math-tagged TArray's allocation did NOT increment "
                "FMemTag::Math byte counter (before=%llu live=%llu)\n",
                static_cast<unsigned long long>(MathBytesBefore),
                static_cast<unsigned long long>(MathBytesLive));
            return 1;
        }
    }

    // Post-destruction: the Math counter returns to its pre-test value
    // (the TArray's destructor frees the buffer with the Math tag).
    const ::uint64 MathBytesAfter = FMemory::GetAllocatedBytes(FMemTag::Math);
    if (MathBytesAfter != MathBytesBefore)
    {
        std::fprintf(stderr,
            "FAIL: post-destruction Math bytes = %llu (expected %llu); "
            "allocation accounting mismatch\n",
            static_cast<unsigned long long>(MathBytesAfter),
            static_cast<unsigned long long>(MathBytesBefore));
        return 1;
    }

    // ---------------------------------------------------------------
    // 4. Sanity: default-tag TArray attributes to Container.
    // ---------------------------------------------------------------
    const ::uint64 ContainerBytesBefore = FMemory::GetAllocatedBytes(FMemTag::Container);

    {
        ::XCore::TArray<::int32, DefaultAllocator> DefaultArr;
        for (::int32 I = 0; I < 32; ++I)
        {
            DefaultArr.Add(I);
        }

        const ::uint64 ContainerBytesLive = FMemory::GetAllocatedBytes(FMemTag::Container);
        if (ContainerBytesLive <= ContainerBytesBefore)
        {
            std::fprintf(stderr,
                "FAIL: default-tagged TArray did NOT increment "
                "FMemTag::Container byte counter\n");
            return 1;
        }
    }

    const ::uint64 ContainerBytesAfter = FMemory::GetAllocatedBytes(FMemTag::Container);
    if (ContainerBytesAfter != ContainerBytesBefore)
    {
        std::fprintf(stderr,
            "FAIL: post-destruction Container bytes = %llu (expected %llu)\n",
            static_cast<unsigned long long>(ContainerBytesAfter),
            static_cast<unsigned long long>(ContainerBytesBefore));
        return 1;
    }

    // ---------------------------------------------------------------
    // 5. ABI sanity: sizeof(DefaultAllocator) is 2 (one FMemTag).
    // ---------------------------------------------------------------
    static_assert(sizeof(DefaultAllocator) == 2,
                  "DefaultAllocator must be exactly 2 bytes (one FMemTag)");

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("DefaultAllocator.TagPropagation: PASS\n");
    return 0;
}
