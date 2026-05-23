// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// UserPointerEqualsBlockStart.cpp -- Phase 1g Round 2 invariant.
// =====================================================================
//
// XCore-4a Rev 3 Section 4.5 row 1 (PoolIndexFromPtr swap): after the
// FBlockHeader removal, the user pointer returned by Malloc is the
// block start itself, not block + kUserOffset. The invariant is:
//
//   1. The returned pointer is aligned to the bin's full size (not
//      bin-size + 16 or any header-offset variant).
//   2. The returned pointer lies within the bin's VM range, in the
//      user-data area beyond the FPoolMetadata + side-table header.
//   3. The returned pointer is reachable via FMallocBinnedX::
//      GetPoolMetadata + BlockIndex arithmetic.
//
// This test exercises invariant 1 directly (the test surface most
// likely to regress) and invariant 3 indirectly via Malloc/Free
// round-trip correctness (a wrong user pointer would corrupt the
// side-table indexing and the round-trip would leak bytes).
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

    // Bin-size-alignment invariant. For each bin, allocate one block
    // and verify the user pointer is aligned to the bin's size. The
    // bin sizes are all multiples of 16, so this also implies
    // 16-alignment (which is the legacy contract).
    //
    // We test a representative selection of bin sizes spanning the
    // table (small, mid, large).
    struct FCase
    {
        ::SIZE_T RequestedSize;  // user request
        ::SIZE_T ExpectedBinSize; // bin size the request rounds to
        ::SIZE_T AlignRequest;   // align argument
    };

    // The kBinSizeTable progression: 32, 48, 64, 80, ..., 16384.
    // We pick boundary cases and a few mid-points.
    const FCase Cases[] = {
        // RequestedSize, ExpectedBinSize, AlignRequest
        {   1,    32,  8 },   // smallest bin
        {  32,    32,  8 },   // exact match
        {  33,    48,  8 },   // overflow to next bin
        {  64,    64, 16 },   // 16-align in the small-bin path
        { 100,   112,  8 },   // mid-table
        { 256,   256, 16 },   // exact, popular size
        { 1024, 1024, 16 },   // exact, container hot path
        { 4097, 4608,  8 },   // boundary just past a bin
    };

    for (const FCase& C : Cases)
    {
        void* P = FMemory::Malloc(C.RequestedSize, C.AlignRequest, FMemTag::Container);
        if (P == nullptr)
        {
            std::fprintf(stderr,
                         "FAIL: Malloc returned null for size=%llu align=%llu\n",
                         static_cast<unsigned long long>(C.RequestedSize),
                         static_cast<unsigned long long>(C.AlignRequest));
            return 1;
        }

        const ::uintptr_t Addr = reinterpret_cast<::uintptr_t>(P);

        // Phase 1g Round 2 invariant: user pointer is aligned to the
        // bin's full size (== block start). Under the legacy layout,
        // the user pointer would be (block + 16), which is NOT
        // bin-size-aligned for bins > 16 -- so this check is the
        // load-bearing one that catches a regression.
        if ((Addr % C.ExpectedBinSize) != 0)
        {
            std::fprintf(stderr,
                         "FAIL: user pointer 0x%llx not aligned to bin size %llu "
                         "(size=%llu align=%llu)\n",
                         static_cast<unsigned long long>(Addr),
                         static_cast<unsigned long long>(C.ExpectedBinSize),
                         static_cast<unsigned long long>(C.RequestedSize),
                         static_cast<unsigned long long>(C.AlignRequest));
            FMemory::Free(P);
            FMemory::__Shutdown();
            return 1;
        }

        // Honour the alignment request too (subset of bin-size alignment
        // for align <= bin size, but verify explicitly).
        if ((Addr & (C.AlignRequest - 1)) != 0)
        {
            std::fprintf(stderr,
                         "FAIL: user pointer 0x%llx not aligned to requested %llu "
                         "(size=%llu)\n",
                         static_cast<unsigned long long>(Addr),
                         static_cast<unsigned long long>(C.AlignRequest),
                         static_cast<unsigned long long>(C.RequestedSize));
            FMemory::Free(P);
            FMemory::__Shutdown();
            return 1;
        }

        FMemory::Free(P);
    }

    // Round-trip accounting check: every alloc was freed; per-tag bytes
    // must be 0. If the user-pointer-as-block-start invariant fails,
    // the side-table indexing would corrupt and the round-trip would
    // be non-zero.
    const ::uint64 Final = FMemory::GetAllocatedBytes(FMemTag::Container);
    if (Final != 0)
    {
        std::fprintf(stderr,
                     "FAIL: GetAllocatedBytes(Container) = %llu after all frees; "
                     "side-table indexing is likely corrupt\n",
                     static_cast<unsigned long long>(Final));
        FMemory::__Shutdown();
        return 1;
    }

    FMemory::__Shutdown();
    std::printf("UserPointerEqualsBlockStart: PASS\n");
    return 0;
}
