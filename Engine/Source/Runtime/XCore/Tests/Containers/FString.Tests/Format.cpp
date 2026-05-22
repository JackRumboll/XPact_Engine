// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/Format.cpp -- FString::Format / FormatFixed correctness.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 fix Rev 3 M4 + Section 11.1.3 sim-path
// safety. Phase 1g landing of the Format / FormatFixed surface.
//
// Scope:
//   * Format with no arguments returns the format string verbatim.
//   * Format with one positional argument substitutes correctly.
//   * Format with multiple positional arguments respects argument
//     order and the {0}/{1} positional specifiers.
//   * FormatFixed produces the same output as Format for the
//     plain-ASCII / no-locale-specifier inputs we test.
//
// The compile-time format-string check is enforced via the
// std::format_string<Args...> template parameter on the FString::Format
// signature; the FormatCompileTimeCheck test covers that path
// separately (it intentionally would not compile if you uncommented
// the malformed call site).
//
// =====================================================================

#include "Containers/FString.h"

#include <cstdio>
#include <cstring>

namespace
{
    int CheckEquals(const ::XCore::FString& Actual,
                    const char* Expected,
                    const char* Tag)
    {
        const ::int32 ExpectedLen = static_cast<::int32>(std::strlen(Expected));
        if (Actual.LenBytes() != ExpectedLen)
        {
            std::fprintf(stderr,
                "FAIL [%s]: LenBytes=%d, expected %d (actual='%.*s', "
                "expected='%s').\n",
                Tag, Actual.LenBytes(), ExpectedLen,
                Actual.LenBytes(), Actual.ToUtf8Ptr(), Expected);
            return 1;
        }
        if (std::memcmp(Actual.ToUtf8Ptr(), Expected,
                        static_cast<::SIZE_T>(ExpectedLen)) != 0)
        {
            std::fprintf(stderr,
                "FAIL [%s]: byte mismatch (actual='%.*s', expected='%s').\n",
                Tag,
                Actual.LenBytes(), Actual.ToUtf8Ptr(), Expected);
            return 1;
        }
        return 0;
    }

    int RunFormatBasic()
    {
        // No-arg path.
        {
            ::XCore::FString R = ::XCore::FString::Format("Hello, world!");
            if (CheckEquals(R, "Hello, world!", "no-arg")) return 1;
        }

        // One-arg, integer.
        {
            ::XCore::FString R = ::XCore::FString::Format("{}", 42);
            if (CheckEquals(R, "42", "one-arg-int")) return 1;
        }

        // Two-arg integer addition format.
        {
            ::XCore::FString R = ::XCore::FString::Format("{}+{}", 1, 2);
            if (CheckEquals(R, "1+2", "two-arg-int")) return 1;
        }

        // Positional indices.
        {
            ::XCore::FString R = ::XCore::FString::Format("{1}-{0}", "A", "B");
            if (CheckEquals(R, "B-A", "positional")) return 1;
        }

        // Mixed types.
        {
            ::XCore::FString R = ::XCore::FString::Format(
                "{} is {} years old", "Alice", 30);
            if (CheckEquals(R, "Alice is 30 years old", "mixed")) return 1;
        }

        // FormatFixed parity.
        {
            ::XCore::FString A = ::XCore::FString::Format("hex={:x}", 255);
            ::XCore::FString B = ::XCore::FString::FormatFixed("hex={:x}", 255);
            if (CheckEquals(A, "hex=ff", "format-hex")) return 1;
            if (CheckEquals(B, "hex=ff", "formatfixed-hex")) return 1;
        }

        std::printf("PASS: FString::Format basic correctness\n");
        return 0;
    }
}

int main()
{
    return RunFormatBasic();
}
