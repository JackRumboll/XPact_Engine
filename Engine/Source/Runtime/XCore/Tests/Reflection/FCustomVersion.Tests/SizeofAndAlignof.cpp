// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.Tests/SizeofAndAlignof.cpp -- ABI lock verification.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.1 + Section 11.5 (Stage B addendum
// XPACT_FCUSTOMVERSION_LAYOUT_TAG).
//
// Verifies at COMPILE TIME via static_assert and at RUNTIME via the
// returned exit code that:
//
//   * sizeof(FGuid)             == 16
//   * alignof(FGuid)            ==  4
//   * sizeof(FCustomVersion)    == 32
//   * alignof(FCustomVersion)   ==  8
//   * offsetof(FCustomVersion, Key)         ==  0
//   * offsetof(FCustomVersion, Version)     == 16
//   * offsetof(FCustomVersion, _Reserved)   == 20
//   * offsetof(FCustomVersion, FriendlyName)== 24
//
// The static_asserts in the headers themselves already pin these; this
// test runs them as a separate TU to catch any toolchain divergence
// (e.g., MSVC vs Clang struct-layout differences that one header alone
// would not surface).
//
// =====================================================================

#include "Reflection/FCustomVersion.h"
#include "Reflection/FGuid.h"

#include <cstddef>
#include <iostream>
#include <type_traits>

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
    using ::XCore::Reflect::FCustomVersion;
    using ::XCore::Reflect::FGuid;

    // FGuid layout.
    Check(sizeof(FGuid) == 16, "sizeof(FGuid) != 16");
    Check(alignof(FGuid) == 4, "alignof(FGuid) != 4");

    // FCustomVersion layout.
    Check(sizeof(FCustomVersion) == 32, "sizeof(FCustomVersion) != 32");
    Check(alignof(FCustomVersion) == 8, "alignof(FCustomVersion) != 8");

    Check(offsetof(FCustomVersion, Key)          ==  0,
          "offsetof(FCustomVersion, Key) != 0");
    Check(offsetof(FCustomVersion, Version)      == 16,
          "offsetof(FCustomVersion, Version) != 16");
    Check(offsetof(FCustomVersion, _Reserved)    == 20,
          "offsetof(FCustomVersion, _Reserved) != 20");
    Check(offsetof(FCustomVersion, FriendlyName) == 24,
          "offsetof(FCustomVersion, FriendlyName) != 24");

    // Serialized size constant matches sizeof.
    Check(FCustomVersion::kSerializedSize == 32,
          "FCustomVersion::kSerializedSize != 32");

    // POD-ness traits (catches a future accidental ctor / dtor that
    // would break the trivial-copy assumption every byte-buffer
    // helper relies on).
    Check(std::is_standard_layout_v<FCustomVersion>,
          "FCustomVersion is no longer standard-layout");
    Check(std::is_trivially_copyable_v<FCustomVersion>,
          "FCustomVersion is no longer trivially copyable");
    Check(std::is_trivially_destructible_v<FCustomVersion>,
          "FCustomVersion is no longer trivially destructible");

    if (g_FailureCount > 0)
    {
        std::cerr << "FCustomVersion.SizeofAndAlignof: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FCustomVersion.SizeofAndAlignof: PASS\n";
    return 0;
}
