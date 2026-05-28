// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInsightsBridge.Tests/EmitWithMockStrongSymbol.cpp -- strong-symbol
// override verification (XCoreXObject Rev 4 §10.5 + Phase 5.k).
// =====================================================================
//
// VERIFIES the strong-symbol-replacement mechanism:
//   * A test-defined strong `XCore_XInsights_Emit` increments a
//     counter on each call.
//   * Emit calls through XInsightsBridge::Emit dispatch to the
//     test's override (NOT the default weak stub).
//
// PLATFORM POSTURE (per Prime Directive divergence justification):
//
//   The Phase 5.k bridge uses [[gnu::weak]] on Clang/GCC (the weak
//   attribute lets the test's strong definition supplant the stub at
//   link time). On MSVC the weak attribute does not exist; the stub's
//   body ships as ordinary strong-linkage in XCore.lib. A
//   duplicate-strong-symbol definition in this TU would produce
//   LNK2005.
//
//   The PRINCIPLED approach for MSVC is the compile-time gate header
//   (XInsightsBridgeStrongSymbolGate.h) -- but the gate fires only
//   when XInsights ships its strong provider AND the gate header is
//   included into XCore's stub TU at build time. That requires a
//   build-system arrangement (XBT propagating the include) that is
//   out of scope for Phase 5.k.
//
//   Phase 5.k TEST POSTURE: this test source uses the [[gnu::weak]]
//   pattern on Clang/GCC and SKIPS the link-time verification on MSVC.
//   The test always PASSES on MSVC (the no-op-no-crash baseline is
//   exercised by EmitDefaultIsNoOp.cpp); on Clang/GCC it additionally
//   verifies the strong-symbol override.
//
//   When XInsights ships in a future phase, that subsystem's
//   build-system wiring will add the gate header to the stub TU's
//   compile and the MSVC path will exercise the same override
//   mechanism in production.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FName.h"
#include "XObject/FXInsightsPayload.h"
#include "XObject/XInsightsBridge.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <iostream>

// ---------------------------------------------------------------------
// MOCK STRONG-SYMBOL OVERRIDE.
//
// On Clang/GCC the test defines `XCore_XInsights_Emit` as ordinary
// strong-linkage. The bridge's default stub is [[gnu::weak]] so the
// linker picks the test's definition for every call from
// XInsightsBridge::Emit.
//
// On MSVC the override is COMPILED OUT (gate by `defined(_MSC_VER)`)
// because supplying a strong override would collide with the stub's
// strong-linkage default in XCore.lib (LNK2005). The test instead
// verifies the no-op-no-crash baseline on MSVC, with a stdout marker
// to surface the skip.
//
// The MOCK_EMIT_COUNTER is incremented by every call to the mock.
// The test reads it after each Emit to verify dispatch.
// ---------------------------------------------------------------------

#if !defined(_MSC_VER)

static ::std::atomic<int> g_MockEmitCounter{0};
static const char* g_LastCategory = nullptr;
static const char* g_LastEvent    = nullptr;
static const ::XCore::HAL::FXInsightsPayload* g_LastPayload = nullptr;

extern "C"
{
    // Strong definition: supplants the [[gnu::weak]] stub on Clang/GCC.
    void XCore_XInsights_Emit(
        const char*                                       Category,
        const char*                                       Event,
        const ::XCore::HAL::FXInsightsPayload*            Payload) noexcept
    {
        g_LastCategory = Category;
        g_LastEvent    = Event;
        g_LastPayload  = Payload;
        g_MockEmitCounter.fetch_add(1, ::std::memory_order_relaxed);
    }
}

#endif // !_MSC_VER

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
    using ::XCore::Reflect::FName;
    namespace Bridge = ::XCore::HAL::XInsightsBridge;

    ::XCore::HAL::FMemory::__Init();

#if defined(_MSC_VER)
    // On MSVC the strong override would LNK2005 against the stub's
    // default body in XCore.lib. The test exercises the no-op-no-
    // crash baseline; the strong-override mechanism is verified on
    // Clang/GCC in this same test file.
    std::cout << "XInsightsBridge.EmitWithMockStrongSymbol: "
              << "MSVC -- skipping strong-override verification "
              << "(requires gate-header wired into stub TU compile; "
              << "out of scope for Phase 5.k). "
              << "No-op baseline exercised by EmitDefaultIsNoOp.cpp.\n";

    // Still exercise the wrapper to validate the no-op path here:
    {
        const FName Cat("MSVCBaseCat");
        const FName Evt("MSVCBaseEvt");
        Bridge::Emit(Cat, Evt);
        Check(true, "MSVC no-op baseline did not crash.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XInsightsBridge.EmitWithMockStrongSymbol: PASS (MSVC baseline only)\n";
        return 0;
    }
    std::cerr << "XInsightsBridge.EmitWithMockStrongSymbol: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
#else
    // -----------------------------------------------------------------
    // Test 1: emit through wrapper -> mock receives the call.
    // -----------------------------------------------------------------
    {
        const int BeforeCount = g_MockEmitCounter.load();

        const FName Cat("Category1");
        const FName Evt("Event1");
        Bridge::Emit(Cat, Evt);

        const int AfterCount = g_MockEmitCounter.load();
        Check(AfterCount == BeforeCount + 1,
              "Mock counter should increment by 1 after Emit.");
    }

    // -----------------------------------------------------------------
    // Test 2: mock receives the correct category + event strings.
    // -----------------------------------------------------------------
    {
        g_LastCategory = nullptr;
        g_LastEvent    = nullptr;
        g_LastPayload  = nullptr;

        const FName Cat("MyCat");
        const FName Evt("MyEvt");
        Bridge::Emit(Cat, Evt);

        Check(g_LastCategory != nullptr,
              "Mock should observe a non-null Category pointer.");
        Check(g_LastEvent != nullptr,
              "Mock should observe a non-null Event pointer.");
        if (g_LastCategory != nullptr)
        {
            Check(::std::strcmp(g_LastCategory, "MyCat") == 0,
                  "Mock should observe category 'MyCat'.");
        }
        if (g_LastEvent != nullptr)
        {
            Check(::std::strcmp(g_LastEvent, "MyEvt") == 0,
                  "Mock should observe event 'MyEvt'.");
        }
        Check(g_LastPayload != nullptr,
              "Mock should observe a non-null Payload pointer.");
    }

    // -----------------------------------------------------------------
    // Test 3: emit with populated payload -> mock sees entry count.
    // -----------------------------------------------------------------
    {
        FXInsightsPayload Payload;
        Payload.Add(FName("k1"), static_cast<::std::int64_t>(7));
        Payload.Add(FName("k2"), static_cast<::std::int64_t>(8));
        Payload.Add(FName("k3"), static_cast<::std::int64_t>(9));

        const int BeforeCount = g_MockEmitCounter.load();
        Bridge::Emit(FName("PayloadCat"), FName("PayloadEvt"), Payload);
        const int AfterCount = g_MockEmitCounter.load();

        Check(AfterCount == BeforeCount + 1,
              "Mock counter should increment after payload Emit.");
        Check(g_LastPayload != nullptr,
              "Mock should observe a non-null payload pointer.");
        if (g_LastPayload != nullptr)
        {
            Check(g_LastPayload->NumEntries() == 3,
                  "Mock should observe 3 entries in the payload.");
        }
    }

    // -----------------------------------------------------------------
    // Test 4: many sequential Emits -> counter matches.
    // -----------------------------------------------------------------
    {
        const int BeforeCount = g_MockEmitCounter.load();
        const int kIterations = 100;

        for (int i = 0; i < kIterations; ++i)
        {
            Bridge::Emit(FName("BatchCat"), FName("BatchEvt"));
        }

        const int AfterCount = g_MockEmitCounter.load();
        Check(AfterCount == BeforeCount + kIterations,
              "Mock counter should increment by exactly kIterations.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XInsightsBridge.EmitWithMockStrongSymbol: PASS\n";
        return 0;
    }
    std::cerr << "XInsightsBridge.EmitWithMockStrongSymbol: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
#endif // !_MSC_VER
}
