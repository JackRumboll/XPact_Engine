// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.Tests/EagerCDOEnqueue.cpp -- Rev 3 FIX-H-R2-6.
// =====================================================================
//
// Verifies EnqueueEagerCDO appends the FClass to the pending queue +
// GetPendingEagerCDOCount reflects the count.
//
// Idempotency: a second EnqueueEagerCDO for the same Class is a no-op.
// Non-EagerCDO classes (no CLASS_EagerCDO flag) are rejected.
//
// =====================================================================

#include "XObject/CDOManagement.h"
#include "Reflection/EClassFlags.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"

#include <iostream>

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
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::EClassFlags;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::__ResetCDOsForTests();

    // Start empty.
    Check(::XCore::GetPendingEagerCDOCount() == 0,
          "fresh queue count != 0");

    // Build 3 eager classes + 1 non-eager.
    FClass EagerA(FName("XEagerA"), nullptr, EClassFlags::CLASS_EagerCDO);
    FClass EagerB(FName("XEagerB"), nullptr, EClassFlags::CLASS_EagerCDO);
    FClass EagerC(FName("XEagerC"), nullptr, EClassFlags::CLASS_EagerCDO);
    FClass NonEager(FName("XNonEager"), nullptr, EClassFlags::CLASS_None);

    // IsEagerCDO predicate.
    Check(::XCore::IsEagerCDO(&EagerA),    "IsEagerCDO(EagerA) == false");
    Check(::XCore::IsEagerCDO(&EagerB),    "IsEagerCDO(EagerB) == false");
    Check(::XCore::IsEagerCDO(&EagerC),    "IsEagerCDO(EagerC) == false");
    Check(!::XCore::IsEagerCDO(&NonEager), "IsEagerCDO(NonEager) == true");
    Check(!::XCore::IsEagerCDO(nullptr),   "IsEagerCDO(nullptr) == true");

    // Enqueue all 3 eager classes.
    ::XCore::EnqueueEagerCDO(&EagerA);
    Check(::XCore::GetPendingEagerCDOCount() == 1, "after 1 enqueue: count != 1");

    ::XCore::EnqueueEagerCDO(&EagerB);
    Check(::XCore::GetPendingEagerCDOCount() == 2, "after 2 enqueue: count != 2");

    ::XCore::EnqueueEagerCDO(&EagerC);
    Check(::XCore::GetPendingEagerCDOCount() == 3, "after 3 enqueue: count != 3");

    // Idempotency: re-enqueue is a no-op.
    ::XCore::EnqueueEagerCDO(&EagerA);
    Check(::XCore::GetPendingEagerCDOCount() == 3,
          "re-enqueue of EagerA: count changed (expected idempotency)");

    // Non-EagerCDO class: reject.
    ::XCore::EnqueueEagerCDO(&NonEager);
    Check(::XCore::GetPendingEagerCDOCount() == 3,
          "non-eager enqueue: count changed (expected reject)");

    // nullptr: reject.
    ::XCore::EnqueueEagerCDO(nullptr);
    Check(::XCore::GetPendingEagerCDOCount() == 3,
          "nullptr enqueue: count changed");

    // Clean up for any subsequent tests.
    ::XCore::__ResetCDOsForTests();
    Check(::XCore::GetPendingEagerCDOCount() == 0,
          "after reset: count != 0");

    if (g_FailureCount == 0)
    {
        std::cout << "CDOManagement.EagerCDOEnqueue: PASS\n";
        return 0;
    }
    std::cerr << "CDOManagement.EagerCDOEnqueue: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
