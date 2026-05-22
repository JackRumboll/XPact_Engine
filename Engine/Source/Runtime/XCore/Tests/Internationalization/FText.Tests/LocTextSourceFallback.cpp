// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FText.Tests/LocTextSourceFallback.cpp -- LOCTEXT source-fallback test.
// =====================================================================
//
// XCore-4a Rev 3 Section 17.8 H1: "LOCTEXT resolves to source-fallback
// (en-US) when no loctable is loaded".
//
// Verifies that:
//   * LOCTEXT("Hello", "Hello World") returns an FText whose
//     ResolveForCurrentLocale() yields the literal "Hello World"
//     when no loctable has been loaded.
//   * Same for NSLOCTEXT and INVTEXT.
//
// =====================================================================

#include "Internationalization/FText.h"
#include "Internationalization/LOCTEXT.h"
#include "Internationalization/FLocalizationManager.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>
#include <cstring>

#define LOCTEXT_NAMESPACE "FTextTests"

int main()
{
    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    ::XCore::Loc::FLocalizationManager::__Initialize();

    // Test 1: LOCTEXT with file-scope namespace resolves to literal.
    {
        ::XCore::Loc::FText Greeting = LOCTEXT("Hello", "Hello World");
        const ::XCore::FString& Resolved = Greeting.ResolveForCurrentLocale();
        if (Resolved.LenBytes() != 11)
        {
            std::fprintf(stderr, "FAIL: LOCTEXT Hello LenBytes=%d (expected 11)\n",
                         Resolved.LenBytes());
            return 1;
        }
        if (std::memcmp(Resolved.ToUtf8Ptr(), "Hello World", 11) != 0)
        {
            std::fprintf(stderr, "FAIL: LOCTEXT Hello bytes mismatch\n");
            return 1;
        }
    }

    // Test 2: NSLOCTEXT with explicit namespace resolves to literal.
    {
        ::XCore::Loc::FText Greeting = NSLOCTEXT("AnotherNs", "Bye", "Goodbye");
        const ::XCore::FString& Resolved = Greeting.ResolveForCurrentLocale();
        if (Resolved.LenBytes() != 7)
        {
            std::fprintf(stderr, "FAIL: NSLOCTEXT Bye LenBytes=%d (expected 7)\n",
                         Resolved.LenBytes());
            return 1;
        }
        if (std::memcmp(Resolved.ToUtf8Ptr(), "Goodbye", 7) != 0)
        {
            std::fprintf(stderr, "FAIL: NSLOCTEXT Bye bytes mismatch\n");
            return 1;
        }
    }

    // Test 3: INVTEXT resolves to literal (invariant text, no loctable lookup).
    {
        ::XCore::Loc::FText Inv = INVTEXT("Player1");
        const ::XCore::FString& Resolved = Inv.ResolveForCurrentLocale();
        if (Resolved.LenBytes() != 7)
        {
            std::fprintf(stderr, "FAIL: INVTEXT Player1 LenBytes=%d (expected 7)\n",
                         Resolved.LenBytes());
            return 1;
        }
        if (std::memcmp(Resolved.ToUtf8Ptr(), "Player1", 7) != 0)
        {
            std::fprintf(stderr, "FAIL: INVTEXT Player1 bytes mismatch\n");
            return 1;
        }
    }

    // Test 4: Second resolve hits the cache (same byte content).
    {
        ::XCore::Loc::FText Greeting = LOCTEXT("CacheTest", "Hello!");
        const ::XCore::FString& First  = Greeting.ResolveForCurrentLocale();
        const ::XCore::FString& Second = Greeting.ResolveForCurrentLocale();
        // Same reference (cache hit returns the same FString instance).
        if (&First != &Second)
        {
            std::fprintf(stderr, "FAIL: cache hit should return same reference\n");
            return 1;
        }
        if (First.LenBytes() != 6 || std::memcmp(First.ToUtf8Ptr(), "Hello!", 6) != 0)
        {
            std::fprintf(stderr, "FAIL: cache content wrong\n");
            return 1;
        }
    }

    // Test 5: IsEmpty checks.
    {
        ::XCore::Loc::FText Empty;
        if (!Empty.IsEmpty())
        {
            std::fprintf(stderr, "FAIL: default-ctor FText should be empty\n");
            return 1;
        }
        ::XCore::Loc::FText Nonempty = LOCTEXT("NotEmpty", "x");
        if (Nonempty.IsEmpty())
        {
            std::fprintf(stderr, "FAIL: nonempty FText should not be empty\n");
            return 1;
        }
    }

    std::fprintf(stdout, "PASS: LocTextSourceFallback\n");
    return 0;
}

#undef LOCTEXT_NAMESPACE
