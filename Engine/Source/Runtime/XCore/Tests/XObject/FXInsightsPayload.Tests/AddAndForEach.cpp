// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXInsightsPayload.Tests/AddAndForEach.cpp -- ordered visit
// (XCoreXObject Rev 4 §10.5 + Phase 5.k).
// =====================================================================
//
// Verifies:
//   * Add 3 entries; ForEach visits all 3 in insertion order.
//   * The visitor receives the correct key + value type for each.
//   * GetEntry(i) returns the same key + value (random access).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXInsightsPayload.h"
#include "Reflection/FName.h"

#include <cstdint>
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
    using ::XCore::HAL::FXInsightsPayload;
    using ::XCore::HAL::FInsightsValue;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Build a 3-entry payload with deterministic keys.
    // -----------------------------------------------------------------
    FXInsightsPayload Payload;
    const FName KeyA("alpha");
    const FName KeyB("beta");
    const FName KeyC("gamma");

    Payload.Add(KeyA, static_cast<::std::int64_t>(100));
    Payload.Add(KeyB, 2.5);
    Payload.Add(KeyC, static_cast<::std::int64_t>(300));

    Check(Payload.NumEntries() == 3,
          "Expected 3 entries after 3 Add calls.");

    // -----------------------------------------------------------------
    // Walk via ForEach and record observed (Key.Index, type-index).
    // -----------------------------------------------------------------
    int    VisitCount = 0;
    FName  ObservedKeys[3]   = { FName(), FName(), FName() };
    size_t ObservedTypes[3]  = { 0, 0, 0 };

    Payload.ForEach([&](FName Key, const FInsightsValue& Value)
    {
        if (VisitCount < 3)
        {
            ObservedKeys[VisitCount]  = Key;
            ObservedTypes[VisitCount] = Value.index();
        }
        ++VisitCount;
    });

    Check(VisitCount == 3,
          "ForEach should visit every entry (3).");

    // Verify insertion order preserved.
    Check(ObservedKeys[0] == KeyA, "ForEach entry 0 should be 'alpha'.");
    Check(ObservedKeys[1] == KeyB, "ForEach entry 1 should be 'beta'.");
    Check(ObservedKeys[2] == KeyC, "ForEach entry 2 should be 'gamma'.");

    // Verify variant indices match the types added:
    //   int64 -> index 0
    //   double -> index 1
    Check(ObservedTypes[0] == 0,
          "alpha's value should be variant index 0 (int64).");
    Check(ObservedTypes[1] == 1,
          "beta's value should be variant index 1 (double).");
    Check(ObservedTypes[2] == 0,
          "gamma's value should be variant index 0 (int64).");

    // -----------------------------------------------------------------
    // Random-access via GetEntry.
    // -----------------------------------------------------------------
    {
        const auto& E0 = Payload.GetEntry(0);
        Check(E0.Key == KeyA, "GetEntry(0).Key should be 'alpha'.");
        const auto* AsInt = ::std::get_if<::std::int64_t>(&E0.Value);
        Check(AsInt != nullptr && *AsInt == 100,
              "GetEntry(0).Value should be int64 == 100.");

        const auto& E1 = Payload.GetEntry(1);
        Check(E1.Key == KeyB, "GetEntry(1).Key should be 'beta'.");
        const auto* AsDouble = ::std::get_if<double>(&E1.Value);
        Check(AsDouble != nullptr && *AsDouble == 2.5,
              "GetEntry(1).Value should be double == 2.5.");

        const auto& E2 = Payload.GetEntry(2);
        Check(E2.Key == KeyC, "GetEntry(2).Key should be 'gamma'.");
        const auto* AsInt2 = ::std::get_if<::std::int64_t>(&E2.Value);
        Check(AsInt2 != nullptr && *AsInt2 == 300,
              "GetEntry(2).Value should be int64 == 300.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXInsightsPayload.AddAndForEach: PASS\n";
        return 0;
    }
    std::cerr << "FXInsightsPayload.AddAndForEach: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
