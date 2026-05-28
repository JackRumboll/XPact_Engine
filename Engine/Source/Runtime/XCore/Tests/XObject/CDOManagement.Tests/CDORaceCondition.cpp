// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.Tests/CDORaceCondition.cpp -- X7 acceptance gate.
// =====================================================================
//
// XCoreXObject Rev 4 §8.2 + X7: concurrent GetClassDefaultObject calls
// from N threads MUST produce exactly one CDO; every thread MUST see
// the same pointer; no CDO is leaked through the CAS-collision path
// observable post-test.
//
// =====================================================================

#include "XObject/CDOManagement.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <atomic>
#include <iostream>
#include <thread>
#include <vector>

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
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();
    ::XCore::__ResetCDOsForTests();

    FClass TestClass(FName("XRace"), nullptr);

    // Spawn 8 threads each calling GetClassDefaultObject simultaneously.
    // Use a barrier-style start signal so all 8 hit the racy first-call
    // window together.
    constexpr int kThreadCount = 8;
    std::atomic<int> StartCounter{0};
    std::vector<const XObject*> Results(kThreadCount, nullptr);
    std::vector<std::thread> Threads;
    Threads.reserve(kThreadCount);

    for (int i = 0; i < kThreadCount; ++i)
    {
        Threads.emplace_back([&, i]() {
            // Wait for everyone to be ready.
            StartCounter.fetch_add(1, std::memory_order_acq_rel);
            while (StartCounter.load(std::memory_order_acquire) < kThreadCount)
            {
                // Busy-wait; the test is bounded by the CDO construct +
                // CAS race window.
            }

            Results[i] = ::XCore::GetClassDefaultObject(&TestClass);
        });
    }

    for (auto& T : Threads)
    {
        T.join();
    }

    // All 8 results MUST be the same non-null pointer.
    Check(Results[0] != nullptr, "thread 0 got nullptr");
    for (int i = 1; i < kThreadCount; ++i)
    {
        if (Results[i] != Results[0])
        {
            std::cerr << "FAIL: thread " << i << " got different CDO than thread 0\n";
            ++g_FailureCount;
        }
    }

    // The published CDO matches the FClass slot. GetCDO returns
    // `const FObject*` (opaque forward-decl on FClass); cast through
    // `const void*` for the pointer equality comparison.
    Check(static_cast<const void*>(TestClass.GetCDO()) ==
              static_cast<const void*>(Results[0]),
          "FClass::GetCDO != thread-observed CDO");

    if (g_FailureCount == 0)
    {
        std::cout << "CDOManagement.CDORaceCondition: PASS\n";
        return 0;
    }
    std::cerr << "CDOManagement.CDORaceCondition: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
