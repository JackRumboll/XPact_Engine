// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/FormatFixedLocaleNeutral.cpp -- FormatFixed sim-path
// guarantee: locale-neutral output.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1.3 row "FormatFixed: Yes" sim-path
// safety:
//
//   "Uses std::format_to_n with the C locale; bit-exact across
//    platforms for the standard {:f}, {:e}, {:g} specifiers. The
//    'Fixed' suffix advertises the sim-path guarantee."
//
// Per the spec, FormatFixed-with-no-locale-specifier is invariant
// under locale changes. We verify this by setting the global locale
// to a non-C locale before formatting and asserting the output bytes
// match the C-locale-default output. The test is best-effort because
// the runtime's available locales depend on the OS; if a non-C
// locale cannot be set we accept the C-default output (we cannot
// "test" what is not available).
//
// =====================================================================

#include "Containers/FString.h"

#include <clocale>
#include <cstdio>
#include <cstring>
#include <locale>

namespace
{
    int RunFormatFixedLocaleNeutral()
    {
        // Capture the C-locale output as the reference.
        ::XCore::FString CRef = ::XCore::FString::FormatFixed(
            "pi={:.4f}, e={:.4f}", 3.14159265, 2.71828183);
        if (CRef.LenBytes() == 0)
        {
            std::fprintf(stderr,
                "FAIL: FormatFixed produced an empty string for valid input.\n");
            return 1;
        }

        // Try to switch to a locale that uses comma decimal-point
        // separator (most European locales). If the runtime refuses
        // (locale not installed), we silently skip the comparison.
        const char* TestLocales[] = {
            "de_DE.UTF-8",
            "de_DE",
            "fr_FR.UTF-8",
            "fr_FR",
            "Germany_Germany",
            "German",
            nullptr,
        };

        bool LocaleChanged = false;
        for (::SIZE_T I = 0; TestLocales[I] != nullptr; ++I)
        {
            if (std::setlocale(LC_ALL, TestLocales[I]) != nullptr)
            {
                LocaleChanged = true;
                break;
            }
        }

        if (!LocaleChanged)
        {
            std::printf("INFO: no non-C locale available; comparing FormatFixed "
                        "output against itself (trivial-pass).\n");
        }

        ::XCore::FString OtherLocaleOut = ::XCore::FString::FormatFixed(
            "pi={:.4f}, e={:.4f}", 3.14159265, 2.71828183);

        // The two outputs must be byte-identical: FormatFixed honours
        // the C locale regardless of the global locale setting.
        if (CRef.LenBytes() != OtherLocaleOut.LenBytes() ||
            std::memcmp(CRef.ToUtf8Ptr(), OtherLocaleOut.ToUtf8Ptr(),
                        static_cast<::SIZE_T>(CRef.LenBytes())) != 0)
        {
            std::fprintf(stderr,
                "FAIL: FormatFixed output diverged under locale change.\n"
                "  C locale: '%.*s'\n"
                "  Other:    '%.*s'\n",
                CRef.LenBytes(),         CRef.ToUtf8Ptr(),
                OtherLocaleOut.LenBytes(), OtherLocaleOut.ToUtf8Ptr());

            // Restore C locale before exit.
            (void)std::setlocale(LC_ALL, "C");
            return 1;
        }

        // Restore.
        (void)std::setlocale(LC_ALL, "C");

        std::printf("PASS: FString::FormatFixed locale-neutral output\n");
        return 0;
    }
}

int main()
{
    return RunFormatFixedLocaleNeutral();
}
