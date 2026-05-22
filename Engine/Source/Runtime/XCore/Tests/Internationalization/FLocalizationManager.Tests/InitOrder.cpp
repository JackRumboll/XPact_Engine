// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLocalizationManager.Tests/InitOrder.cpp -- init-phase contract test.
// =====================================================================
//
// XCore-4a Rev 3 Section 1.5 phase ladder + Section 11.2 (en-US
// fallback at PostStaticInit).
//
// Verifies that:
//   * Pre-PostStaticInit Lookup returns nullptr (correct source-
//     fallback behaviour per Section 17.8 H1).
//   * Post-__Initialize: GetCurrentGeneration is >= 1.
//   * Post-__Initialize: GetCurrentLocale is "en-US".
//   * Calling __Initialize twice is idempotent (no second bump).
//
// =====================================================================

#include "Internationalization/FText.h"
#include "Internationalization/FLocalizationManager.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>
#include <cstring>

int main()
{
    using namespace ::XCore;
    using namespace ::XCore::Loc;

    ::XCore::HAL::FMemory::__Init();

    // ----- Phase A: PreStaticInit -- Lookup must return nullptr -----
    {
        const FString* Hit = FLocalizationManager::Lookup("AnyNs", "AnyKey");
        if (Hit != nullptr)
        {
            std::fprintf(stderr,
                "[InitOrder] FAIL: pre-init Lookup returned non-null\n");
            return 1;
        }

        const ::std::uint32_t Gen = FLocalizationManager::GetCurrentGeneration();
        if (Gen != 0u)
        {
            std::fprintf(stderr,
                "[InitOrder] FAIL: pre-init generation should be 0, got %u\n", Gen);
            return 1;
        }
    }

    // ----- Phase B: advance to PostStaticInit + __Initialize -----
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FLocalizationManager::__Initialize();

    // ----- Phase C: post-init checks -----
    {
        const ::std::uint32_t Gen1 = FLocalizationManager::GetCurrentGeneration();
        if (Gen1 < 1u)
        {
            std::fprintf(stderr,
                "[InitOrder] FAIL: post-init generation should be >= 1, got %u\n", Gen1);
            return 1;
        }

        const FString& Locale = FLocalizationManager::GetCurrentLocale();
        if (Locale.LenBytes() != 5 || std::memcmp(Locale.ToUtf8Ptr(), "en-US", 5) != 0)
        {
            std::fprintf(stderr,
                "[InitOrder] FAIL: post-init locale should be en-US\n");
            return 1;
        }

        // Lookup on a key that doesn't exist: still nullptr (no
        // loctable loaded; en-US is the source-fallback locale).
        const FString* Hit = FLocalizationManager::Lookup("NoNs", "NoKey");
        if (Hit != nullptr)
        {
            std::fprintf(stderr,
                "[InitOrder] FAIL: post-init Lookup returned non-null on missing entry\n");
            return 1;
        }
    }

    // ----- Phase D: idempotency of __Initialize -----
    {
        const ::std::uint32_t Gen1 = FLocalizationManager::GetCurrentGeneration();
        FLocalizationManager::__Initialize();
        const ::std::uint32_t Gen2 = FLocalizationManager::GetCurrentGeneration();
        if (Gen1 != Gen2)
        {
            std::fprintf(stderr,
                "[InitOrder] FAIL: __Initialize is not idempotent (Gen1=%u Gen2=%u)\n",
                Gen1, Gen2);
            return 1;
        }
    }

    std::fprintf(stdout, "PASS: InitOrder\n");
    return 0;
}
