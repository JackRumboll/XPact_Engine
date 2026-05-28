// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXInsightsPayload.Tests/MultipleTypes.cpp -- variant round-trip
// (XCoreXObject Rev 4 §10.5 + Phase 5.k).
// =====================================================================
//
// Verifies all 4 variant alternatives round-trip through the payload:
//   * int64_t
//   * double
//   * FString  (XCore::FString)
//   * FName    (XCore::Reflect::FName; Phase 5.k extension)
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Containers/FString.h"
#include "Reflection/FName.h"
#include "XObject/FXInsightsPayload.h"

#include <cstdint>
#include <cstring>
#include <iostream>
#include <variant>

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
    using ::XCore::FString;
    using ::XCore::HAL::FXInsightsPayload;
    using ::XCore::HAL::FInsightsValue;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXInsightsPayload Payload;

    // -----------------------------------------------------------------
    // Add one of each variant type.
    // -----------------------------------------------------------------
    Payload.Add(FName("Int64Key"),  static_cast<::std::int64_t>(0xDEADBEEF));
    Payload.Add(FName("DoubleKey"), 3.1415926535);
    Payload.Add(FName("StringKey"), FString("hello-from-payload"));
    Payload.Add(FName("FNameKey"),  FName("FNamePayloadValue"));

    Check(Payload.NumEntries() == 4,
          "Expected 4 entries after adding one of each type.");

    // -----------------------------------------------------------------
    // Extract via variant::index() + std::get / std::get_if.
    // -----------------------------------------------------------------
    const auto& Entry0 = Payload.GetEntry(0);
    Check(Entry0.Key == FName("Int64Key"),
          "Entry 0 key should be 'Int64Key'.");
    Check(Entry0.Value.index() == 0,
          "Entry 0 value should be variant index 0 (int64).");
    {
        const auto* V = ::std::get_if<::std::int64_t>(&Entry0.Value);
        Check(V != nullptr && *V == static_cast<::std::int64_t>(0xDEADBEEF),
              "Entry 0 int64 value should be 0xDEADBEEF.");
    }

    const auto& Entry1 = Payload.GetEntry(1);
    Check(Entry1.Key == FName("DoubleKey"),
          "Entry 1 key should be 'DoubleKey'.");
    Check(Entry1.Value.index() == 1,
          "Entry 1 value should be variant index 1 (double).");
    {
        const auto* V = ::std::get_if<double>(&Entry1.Value);
        Check(V != nullptr && *V == 3.1415926535,
              "Entry 1 double value should be 3.1415926535.");
    }

    const auto& Entry2 = Payload.GetEntry(2);
    Check(Entry2.Key == FName("StringKey"),
          "Entry 2 key should be 'StringKey'.");
    Check(Entry2.Value.index() == 2,
          "Entry 2 value should be variant index 2 (FString).");
    {
        const auto* V = ::std::get_if<FString>(&Entry2.Value);
        Check(V != nullptr,
              "Entry 2 should hold FString alternative.");
        if (V != nullptr)
        {
            const char* Bytes = V->ToUtf8Ptr();
            Check(Bytes != nullptr
                  && ::std::strcmp(Bytes, "hello-from-payload") == 0,
                  "Entry 2 FString value should equal 'hello-from-payload'.");
        }
    }

    const auto& Entry3 = Payload.GetEntry(3);
    Check(Entry3.Key == FName("FNameKey"),
          "Entry 3 key should be 'FNameKey'.");
    Check(Entry3.Value.index() == 3,
          "Entry 3 value should be variant index 3 (FName).");
    {
        const auto* V = ::std::get_if<FName>(&Entry3.Value);
        Check(V != nullptr && *V == FName("FNamePayloadValue"),
              "Entry 3 FName value should equal 'FNamePayloadValue'.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXInsightsPayload.MultipleTypes: PASS\n";
        return 0;
    }
    std::cerr << "FXInsightsPayload.MultipleTypes: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
