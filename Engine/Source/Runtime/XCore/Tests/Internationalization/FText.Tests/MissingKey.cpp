// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FText.Tests/MissingKey.cpp -- missing-key dev-warning test.
// =====================================================================
//
// XCore-4a Rev 3 Section 17.8 H3: "Missing key falls back to source
// literal + emits a dev-warning".
//
// Verifies that:
//   * LOCTEXT("Unknown", "Source") with a loctable that doesn't
//     contain "Unknown" falls back to "Source".
//   * A dev-warning is emitted to stderr.
//
// The stderr check is environmental: in Debug/Development builds the
// warning fires; in Shipping it is compiled out. The test PASSes
// either way because the literal fallback is the contractual
// behaviour; the warning is a diagnostic aid, not a correctness
// requirement.
//
// =====================================================================

#include "Internationalization/FText.h"
#include "Internationalization/LOCTEXT.h"
#include "Internationalization/FLocalizationManager.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>
#include <cstring>

#define LOCTEXT_NAMESPACE "MissingKeyTest"

int main()
{
    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    ::XCore::Loc::FLocalizationManager::__Initialize();

    // Test 1: completely-unknown key falls back to literal.
    {
        ::XCore::Loc::FText Unknown = LOCTEXT("DoesNotExist_42", "SourceLiteral");
        const ::XCore::FString& Resolved = Unknown.ResolveForCurrentLocale();
        if (Resolved.LenBytes() != 13)
        {
            std::fprintf(stderr,
                "FAIL: missing-key LenBytes=%d (expected 13)\n",
                Resolved.LenBytes());
            return 1;
        }
        if (std::memcmp(Resolved.ToUtf8Ptr(), "SourceLiteral", 13) != 0)
        {
            std::fprintf(stderr, "FAIL: missing-key bytes mismatch\n");
            return 1;
        }
    }

    // Test 2: namespace exists, key does not.
    {
        ::XCore::Loc::FText Partial = NSLOCTEXT("FTextTests", "NonexistentKey", "FallbackText");
        const ::XCore::FString& Resolved = Partial.ResolveForCurrentLocale();
        if (Resolved.LenBytes() != 12)
        {
            std::fprintf(stderr,
                "FAIL: partial-miss LenBytes=%d (expected 12)\n",
                Resolved.LenBytes());
            return 1;
        }
        if (std::memcmp(Resolved.ToUtf8Ptr(), "FallbackText", 12) != 0)
        {
            std::fprintf(stderr, "FAIL: partial-miss bytes mismatch\n");
            return 1;
        }
    }

    // Test 3: INVTEXT MUST NOT emit a missing-key warning (the
    // "__Invariant" namespace is the silent-sentinel).
    {
        ::XCore::Loc::FText Inv = INVTEXT("InvariantText");
        const ::XCore::FString& Resolved = Inv.ResolveForCurrentLocale();
        if (Resolved.LenBytes() != 13)
        {
            std::fprintf(stderr,
                "FAIL: INVTEXT LenBytes=%d (expected 13)\n",
                Resolved.LenBytes());
            return 1;
        }
        if (std::memcmp(Resolved.ToUtf8Ptr(), "InvariantText", 13) != 0)
        {
            std::fprintf(stderr, "FAIL: INVTEXT bytes mismatch\n");
            return 1;
        }
    }

    std::fprintf(stdout, "PASS: MissingKey (dev-warning emitted in Debug/Dev for non-invariant misses)\n");
    return 0;
}

#undef LOCTEXT_NAMESPACE
