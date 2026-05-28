// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.Tests/SizeofAndAlignof.cpp -- ABI lock verification
// (XCoreXObject Rev 4 §5.3; Phase 5.e XPACT_XGC_ROOTSPAN_LAYOUT_TAG).
// =====================================================================
//
// Pins the 32-byte XGCRootSpan ABI:
//   * sizeof  == 32
//   * alignof == 8
//   * offsetof BaseAddress   ==  0
//   * offsetof ByteLength    ==  8
//   * offsetof ElementStride == 16
//   * offsetof Kind          == 24
//   * offsetof _pad          == 25
//   * sizeof Kind            ==  1
//   * trivially-copyable
//   * trivially-destructible
//
// The static_asserts in XGCRootSpan.h cover the byte layout at compile
// time; this test makes the ABI lock visible at the test-link site so
// the test binary build itself proves the layout has not drifted.
//
// =====================================================================

#include "XObject/XGCRootSpan.h"

#include <cstddef>
#include <cstdint>
#include <iostream>
#include <type_traits>

int main()
{
    using ::XCore::XGCRootSpan;
    using ::XCore::EXGCRootSpanKind;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    Check(sizeof(XGCRootSpan) == 32u,
          "sizeof XGCRootSpan != 32");
    Check(alignof(XGCRootSpan) == 8u,
          "alignof XGCRootSpan != 8");
    Check(offsetof(XGCRootSpan, BaseAddress) == 0u,
          "offsetof BaseAddress != 0");
    Check(offsetof(XGCRootSpan, ByteLength) == 8u,
          "offsetof ByteLength != 8");
    Check(offsetof(XGCRootSpan, ElementStride) == 16u,
          "offsetof ElementStride != 16");
    Check(offsetof(XGCRootSpan, Kind) == 24u,
          "offsetof Kind != 24");
    Check(offsetof(XGCRootSpan, _pad) == 25u,
          "offsetof _pad != 25");

    Check(sizeof(XGCRootSpan::Kind) == 1u,
          "sizeof Kind != 1 (EXGCRootSpanKind backing type drift)");
    Check(static_cast<::std::uint8_t>(EXGCRootSpanKind::kObject) == 0u,
          "EXGCRootSpanKind::kObject != 0");
    Check(static_cast<::std::uint8_t>(EXGCRootSpanKind::kConservative) == 1u,
          "EXGCRootSpanKind::kConservative != 1");

    Check(::std::is_trivially_copyable_v<XGCRootSpan>,
          "XGCRootSpan must be trivially copyable");
    Check(::std::is_trivially_destructible_v<XGCRootSpan>,
          "XGCRootSpan must be trivially destructible");

    if (FailureCount == 0)
    {
        std::cout << "XGCRootSpan.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XGCRootSpan.SizeofAndAlignof: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
