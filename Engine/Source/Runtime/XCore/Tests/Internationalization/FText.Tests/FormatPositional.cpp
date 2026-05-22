// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FText.Tests/FormatPositional.cpp -- FText::Format positional substitution.
// =====================================================================
//
// XCore-4a Rev 3 Section 11.2 + Section 17.8: FText::Format substitutes
// positional {0} / {1} placeholders against the resolved-locale string.
// Phase 1g lands the positional surface; named arguments ({PlayerName})
// are a Phase 2 enhancement (C++26 std::format named-arg support).
//
// =====================================================================

#include "Internationalization/FText.h"
#include "Internationalization/LOCTEXT.h"
#include "Internationalization/FLocalizationManager.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>
#include <cstring>

#define LOCTEXT_NAMESPACE "FTextFormatTests"

namespace
{
    int CheckEquals(const ::XCore::FString& Actual,
                    const char* Expected,
                    const char* Tag)
    {
        const ::int32 ExpectedLen =
            static_cast<::int32>(std::strlen(Expected));
        if (Actual.LenBytes() != ExpectedLen ||
            std::memcmp(Actual.ToUtf8Ptr(), Expected,
                        static_cast<::SIZE_T>(ExpectedLen)) != 0)
        {
            std::fprintf(stderr,
                "FAIL [%s]: actual='%.*s' (len=%d), expected='%s' (len=%d)\n",
                Tag,
                Actual.LenBytes(), Actual.ToUtf8Ptr(), Actual.LenBytes(),
                Expected, ExpectedLen);
            return 1;
        }
        return 0;
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    ::XCore::Loc::FLocalizationManager::__Initialize();

    // Source FText with a positional template ({} or {0}).
    ::XCore::Loc::FText Template = LOCTEXT(
        "ScoreAnnounce", "Player {0} scored {1} points!");

    // Format with two arguments.
    ::XCore::Loc::FText Filled = Template.Format("Alice", 42);
    const ::XCore::FString& Resolved = Filled.ResolveForCurrentLocale();

    if (CheckEquals(Resolved, "Player Alice scored 42 points!", "two-arg"))
    {
        return 1;
    }

    // Re-resolving the formatted FText must NOT re-substitute against
    // the source template; it must return the cached formatted bytes.
    const ::XCore::FString& Resolved2 = Filled.ResolveForCurrentLocale();
    if (Resolved2.LenBytes() != Resolved.LenBytes() ||
        std::memcmp(Resolved2.ToUtf8Ptr(), Resolved.ToUtf8Ptr(),
                    static_cast<::SIZE_T>(Resolved.LenBytes())) != 0)
    {
        std::fprintf(stderr,
            "FAIL: re-resolving formatted FText changed the bytes.\n");
        return 1;
    }

    // Empty FText.Format -- no arguments, no placeholders.
    ::XCore::Loc::FText Plain = LOCTEXT("Plain", "Just text.");
    ::XCore::Loc::FText PlainOut = Plain.Format();
    if (CheckEquals(PlainOut.ResolveForCurrentLocale(),
                    "Just text.", "no-arg"))
    {
        return 1;
    }

    std::printf("PASS: FText::Format positional substitution\n");
    return 0;
}
