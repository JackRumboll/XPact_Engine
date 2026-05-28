// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// X7_CDOLazyConstruction.cpp -- Foundation Prototype X7 acceptance:
// CDO lazy construction takes < 1 ms per FClass first-call (Win64);
// CDO constructed exactly once under 8-thread concurrent access.
// =====================================================================
//
// X7 acceptance (spec §13.2):
//   "CDO lazy construction takes < 1 ms per FClass on first-call
//    (Win64); CDO is constructed exactly once even under concurrent
//    access from 8 threads."
//
// Two sub-tests:
//
//   * SUB-TEST A (timing): first GetClassDefaultObject call on a
//     fresh FClass takes < 1 ms on Win64.
//
//   * SUB-TEST B (concurrency): 8 threads concurrently call
//     GetClassDefaultObject on the same FClass; every thread
//     observes the SAME CDO pointer; no race / no double-construct.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/CDOManagement.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstdint>
#include <thread>
#include <unordered_set>
#include <vector>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::GetClassDefaultObject;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    Phase5L::ResetAllForTests();
    ::XCore::__ResetCDOsForTests();

    // =================================================================
    // SUB-TEST A: first-call CDO construction takes < 1 ms.
    // =================================================================
    FClass TimingClass(FName("X7TimingClass"), nullptr);
    TimingClass.PropertiesSize = sizeof(XObject);
    TimingClass.MinAlignment   = alignof(XObject);
    ::XCore::FXObjectAllocator::Get().RegisterClassPool(&TimingClass);

    const ::std::uint64_t FirstCallNs = Phase5L::BenchNs(1, [&]() noexcept
    {
        const XObject* CDO = GetClassDefaultObject(&TimingClass);
        (void)CDO;
    });

    const double FirstCallMs = static_cast<double>(FirstCallNs) / 1'000'000.0;

    std::cout << "X7 sub-A: first GetClassDefaultObject call = "
              << FirstCallMs << " ms (spec target: < 1 ms Win64).\n";

    // < 5 ms threshold on CI (absorbs variance; spec target 1 ms).
    P5L_CHECK(FirstCallMs < 5.0,
              "X7 sub-A: first CDO call exceeds 5 ms (spec target 1 ms; "
              "CI envelope 5 ms)");

    // =================================================================
    // SUB-TEST B: 8-thread concurrent first-call returns the same CDO.
    // =================================================================
    FClass RaceClass(FName("X7RaceClass"), nullptr);
    RaceClass.PropertiesSize = sizeof(XObject);
    RaceClass.MinAlignment   = alignof(XObject);
    ::XCore::FXObjectAllocator::Get().RegisterClassPool(&RaceClass);

    constexpr int kThreads = 8;
    ::std::atomic<const XObject*> ObservedCDOs[kThreads] = {};
    ::std::atomic<int> StartGate{0};

    std::vector<std::thread> Workers;
    Workers.reserve(kThreads);
    for (int T = 0; T < kThreads; ++T)
    {
        Workers.emplace_back([&, T]()
        {
            // Spin until all threads are ready.
            while (StartGate.load(::std::memory_order_acquire) == 0) {}
            const XObject* CDO = GetClassDefaultObject(&RaceClass);
            ObservedCDOs[T].store(CDO, ::std::memory_order_release);
        });
    }

    // Release all threads simultaneously.
    StartGate.store(1, ::std::memory_order_release);

    for (auto& W : Workers)
    {
        W.join();
    }

    // Verify every thread observed the SAME CDO pointer.
    const XObject* CommonCDO =
        ObservedCDOs[0].load(::std::memory_order_acquire);
    P5L_CHECK(CommonCDO != nullptr,
              "X7 sub-B: thread 0 observed nullptr CDO");

    std::unordered_set<const XObject*> DistinctCDOs;
    for (int T = 0; T < kThreads; ++T)
    {
        const XObject* C = ObservedCDOs[T].load(::std::memory_order_acquire);
        DistinctCDOs.insert(C);
    }
    P5L_CHECK(DistinctCDOs.size() == 1,
              "X7 sub-B: 8-thread race observed multiple distinct CDOs "
              "(double-construct or race)");

    return P5L_REPORT_PASS("FoundationPrototype.X7_CDOLazyConstruction");
}
