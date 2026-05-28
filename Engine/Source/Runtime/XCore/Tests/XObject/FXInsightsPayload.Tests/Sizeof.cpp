// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXInsightsPayload.Tests/Sizeof.cpp -- payload value-type sanity
// (XCoreXObject Rev 4 §10.5 + Phase 5.k).
// =====================================================================
//
// Verifies:
//   * FXInsightsPayload default-construct + populate + destruct is
//     well-formed (no allocator surprises; the type is value-construct
//     ible from any phase that has FMemory live).
//   * Default-constructed payload has 0 entries.
//   * Adding entries bumps NumEntries.
//   * Copy / move are well-formed (the payload is value-typed).
//
// This test does NOT pin sizeof() because the payload's TArray-of-
// TPair-of-variant interior layout is implementation-detail (NOT in
// the Contract Rev 13.9 ABI lock set per spec §11.3).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXInsightsPayload.h"
#include "Reflection/FName.h"

#include <cstdio>
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
    using ::XCore::HAL::FXInsightsPayload;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Test 1: Default-construct + destruct.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Payload;
        Check(Payload.NumEntries() == 0,
              "Default-constructed payload should have 0 entries.");
    }

    // -----------------------------------------------------------------
    // Test 2: Add + NumEntries.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Payload;
        Payload.Add(FName("a"), static_cast<::std::int64_t>(42));
        Check(Payload.NumEntries() == 1,
              "After Add x1: NumEntries should be 1.");

        Payload.Add(FName("b"), 3.14);
        Check(Payload.NumEntries() == 2,
              "After Add x2: NumEntries should be 2.");
    }

    // -----------------------------------------------------------------
    // Test 3: Copy + move (value-type semantics).
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Source;
        Source.Add(FName("k1"), static_cast<::std::int64_t>(1));
        Source.Add(FName("k2"), static_cast<::std::int64_t>(2));

        FXInsightsPayload Copy = Source;
        Check(Copy.NumEntries() == 2,
              "Copy-constructed payload should preserve entry count.");
        Check(Source.NumEntries() == 2,
              "Source payload should be intact after copy.");

        FXInsightsPayload Moved = ::std::move(Source);
        Check(Moved.NumEntries() == 2,
              "Move-constructed payload should preserve entry count.");
    }

    // -----------------------------------------------------------------
    // Test 4: Type-traits + sanity.
    // -----------------------------------------------------------------
    {
        static_assert(::std::is_default_constructible_v<FXInsightsPayload>,
                      "FXInsightsPayload must be default-constructible.");
        static_assert(::std::is_copy_constructible_v<FXInsightsPayload>,
                      "FXInsightsPayload must be copy-constructible.");
        static_assert(::std::is_move_constructible_v<FXInsightsPayload>,
                      "FXInsightsPayload must be move-constructible.");
        static_assert(::std::is_destructible_v<FXInsightsPayload>,
                      "FXInsightsPayload must be destructible.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXInsightsPayload.Sizeof: PASS\n";
        return 0;
    }
    std::cerr << "FXInsightsPayload.Sizeof: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
