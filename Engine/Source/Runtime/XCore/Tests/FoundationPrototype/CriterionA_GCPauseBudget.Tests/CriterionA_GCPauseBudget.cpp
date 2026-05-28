// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CriterionA_GCPauseBudget.cpp -- Foundation Prototype criterion (a):
// GC pause < 5 ms Quest 3 typical.
// =====================================================================
//
// Criterion (a) spec wording (spec §13.1):
//   "10k allocations/sec for 60 s; measure pause durations; p99 must
//    be < 5 ms."
//
// JUDGEMENT CALL (Phase 5.l criterion-a CI envelope). The 60-second
// 10k-alloc/sec workload runs for ~60 seconds and is hardware-required
// (Quest 3 typical pause is the gate target; Win64 desktop ALWAYS
// produces sub-millisecond pauses + a Win64 PASS would be useless).
//
// Phase 5.l CI ships a SCALED-DOWN 5-second / 1k-alloc-per-tick
// workload that exercises the GC pause measurement infrastructure
// without taking 60s of CI budget. The CI gate is "no p99 pause >
// 50 ms" (a regression detection threshold; the spec's 5ms target
// is the Quest 3 acceptance).
//
// The full 60-second / 10k-alloc/sec Quest 3 workload runs at the
// Quest 3 hardware acceptance step; this file's body builds + executes
// on every platform but only ASSERTS the Quest 3 hard target when
// XPACT_PLATFORM_ANDROID is 1.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/NewObject.h"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <thread>
#include <vector>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::EXGCOptions;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::NewObjectImpl;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    Phase5L::ResetAllForTests();

    // -----------------------------------------------------------------
    // Initialise the card table with a generous heap range so the
    // write barrier and sweep have a card-table-covered region to
    // dirty. We do NOT integrate with the live FXObjectAllocator's
    // slab range (the allocator-card-table integration is a Phase 5.f'
    // / 5.h deferred concern).
    // -----------------------------------------------------------------
    constexpr ::std::size_t kHeapBytes = 4 * 1024 * 1024;
    std::vector<::std::uint8_t> CardHeap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(CardHeap.data(), kHeapBytes);

    FClass TestClass(FName("CriterionATestClass"), nullptr);
    TestClass.PropertiesSize = sizeof(XObject);
    TestClass.MinAlignment   = alignof(XObject);
    ::XCore::FXObjectAllocator::Get().RegisterClassPool(&TestClass);

    // -----------------------------------------------------------------
    // Workload schedule:
    //   CI default: 5 seconds; 1k allocations per 100ms tick = ~10k/s.
    //   Hardware:   60 seconds; 1k allocations per 100ms tick = ~10k/s.
    // -----------------------------------------------------------------
    const auto WallDeadline =
        XPACT_PLATFORM_ANDROID
            ? ::std::chrono::seconds(60)
            : ::std::chrono::seconds(5);

    constexpr ::std::size_t kAllocPerTick = 1000;
    const auto TickInterval = ::std::chrono::milliseconds(100);
    const auto StartTime = ::std::chrono::steady_clock::now();

    // Pause histogram (collected across GC cycles during the run).
    std::vector<::std::uint64_t> PauseDurationsNs;
    PauseDurationsNs.reserve(1024);

    // Bounded retention ring so the heap doesn't grow without bound;
    // this keeps the "10k allocations/sec" pattern realistic without
    // forcing a hard OOM.
    constexpr ::std::size_t kRingSize = 50000;
    std::vector<XObject*> Ring(kRingSize, nullptr);
    ::std::size_t RingHead = 0;

    auto NextTick = StartTime;
    while (::std::chrono::steady_clock::now() - StartTime < WallDeadline)
    {
        NextTick += TickInterval;

        // Allocate kAllocPerTick objects.
        for (::std::size_t I = 0; I < kAllocPerTick; ++I)
        {
            XObject* Obj = NewObjectImpl(
                &TestClass, nullptr, FName(), EObjectFlags::None, nullptr);

            // Release the prior occupant of this ring slot so the
            // retained set stays at ~kRingSize objects.
            if (Ring[RingHead] != nullptr)
            {
                Phase5L::ReleaseAndDeallocate(Ring[RingHead]);
            }
            Ring[RingHead] = Obj;
            RingHead = (RingHead + 1) % kRingSize;
        }

        // Force a synchronous GC cycle + measure the pause.
        const auto GCBegin = ::std::chrono::steady_clock::now();
        FXObjectCollector::Get().CollectGarbage(EXGCOptions::kDefault);
        const auto GCEnd = ::std::chrono::steady_clock::now();

        const ::std::uint64_t PauseNs = static_cast<::std::uint64_t>(
            ::std::chrono::duration_cast<::std::chrono::nanoseconds>(
                GCEnd - GCBegin).count());
        PauseDurationsNs.push_back(PauseNs);

        // Sleep until next tick boundary.
        if (NextTick > ::std::chrono::steady_clock::now())
        {
            ::std::this_thread::sleep_until(NextTick);
        }
    }

    // Drain the ring.
    for (XObject*& Obj : Ring)
    {
        if (Obj != nullptr)
        {
            Phase5L::ReleaseAndDeallocate(Obj);
            Obj = nullptr;
        }
    }

    // -----------------------------------------------------------------
    // Compute p50 / p99.
    // -----------------------------------------------------------------
    P5L_CHECK(!PauseDurationsNs.empty(),
              "Criterion (a): no pause samples collected");
    if (PauseDurationsNs.empty())
    {
        return P5L_REPORT_PASS("FoundationPrototype.CriterionA_GCPauseBudget");
    }

    ::std::sort(PauseDurationsNs.begin(), PauseDurationsNs.end());
    const ::std::size_t P50Idx = PauseDurationsNs.size() * 50 / 100;
    const ::std::size_t P99Idx = ::std::min(
        PauseDurationsNs.size() * 99 / 100,
        PauseDurationsNs.size() - 1);

    const double P50Ms = static_cast<double>(PauseDurationsNs[P50Idx])
                       / 1'000'000.0;
    const double P99Ms = static_cast<double>(PauseDurationsNs[P99Idx])
                       / 1'000'000.0;

    std::cout << "Criterion (a): " << PauseDurationsNs.size()
              << " GC pauses sampled; p50 = " << P50Ms
              << " ms, p99 = " << P99Ms << " ms.\n";

    // -----------------------------------------------------------------
    // Acceptance gate:
    //   Quest 3 (hardware): p99 < 5 ms (spec).
    //   Win64 (CI):         p99 < 50 ms (regression detection).
    // -----------------------------------------------------------------
    if (XPACT_PLATFORM_ANDROID)
    {
        P5L_CHECK(P99Ms < 5.0,
                  "Criterion (a): p99 GC pause exceeds 5 ms on Quest 3 "
                  "(spec acceptance violation)");
    }
    else
    {
        P5L_CHECK(P99Ms < 50.0,
                  "Criterion (a): p99 GC pause exceeds 50 ms on Win64 "
                  "(regression detection threshold)");
    }

    return P5L_REPORT_PASS("FoundationPrototype.CriterionA_GCPauseBudget");
}
