// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/FormatCompileTimeCheck.cpp -- compile-time-format-string
// check is enforced.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 fix Rev 3 M4: the FormatString<Args...>
// parameter on FString::Format is std::format_string<Args...>, which
// performs the compile-time format-string check. A malformed format
// string (e.g., unterminated brace, type mismatch) must fail at the
// call site -- NOT at runtime.
//
// This test is structured so the well-formed path compiles + runs;
// the malformed-path test is provided as a commented-out block that
// the developer can uncomment to verify the compile error fires. We
// do not generate a "expect-fail TU" automatically because XBT does
// not yet support expect-fail compilation tests as a first-class
// fixture type.
//
// =====================================================================

#include "Containers/FString.h"

#include <cstdio>

int main()
{
    // Well-formed: this compiles and runs.
    ::XCore::FString R = ::XCore::FString::Format("Sum = {}", 100);
    if (R.LenBytes() != 9)
    {
        std::fprintf(stderr,
            "FAIL: Format('Sum = {}', 100) length = %d, expected 9.\n",
            R.LenBytes());
        return 1;
    }

    // Compile-error verification.
    //
    // Uncomment any of the following lines and the build should FAIL
    // at the FString::Format call site with a constraint diagnostic.
    // The compile-time check originates in std::format_string's
    // consteval validator; it scans the format string at parse time
    // and rejects malformed templates before the call ever runs.
    //
    //   // (a) Unterminated brace.
    //   ::XCore::FString::Format("missing close: {", 1);
    //
    //   // (b) Wrong argument count (excess placeholders).
    //   ::XCore::FString::Format("two args: {} {}", 1);
    //
    //   // (c) Wrong specifier for the argument type.
    //   ::XCore::FString::Format("{:s}", 42);  // {:s} expects string

    std::printf("PASS: FString::Format compile-time-check surface\n");
    return 0;
}
