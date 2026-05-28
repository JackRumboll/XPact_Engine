// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XINITEAGER_EagerCDOOrdering.cpp -- Foundation Prototype X-INIT-EAGER
// acceptance: EagerCDO PostStaticInit ordering.
// =====================================================================
//
// X-INIT-EAGER acceptance (spec §13.2; Rev 3 added per FIX-H-R2-6):
//   "EagerCDO PostStaticInit ordering. Test that an EagerCDO-flagged
//    FClass's CDO is constructed exactly once, at PostStaticInit
//    boundary (after the heap is up, before any user code runs). Test
//    that g_PendingEagerCDOs queue is fully drained at the end of
//    XCoreXObject::__Init(). Pass criterion: bootstrap-ordering bug
//    from Rev 2 design is caught; CDO construction count matches
//    EagerCDO-flagged FClass count."
//
// This Phase 5.l acceptance wraps the Phase 5.d CDOManagement.Tests/
// DrainPendingEagerCDOs.cpp + EagerCDOEnqueue.cpp patterns + extends
// them to verify the drain-count-matches-flagged-count invariant.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/EClassFlags.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/CDOManagement.h"

#include <vector>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::DrainPendingEagerCDOs;
    using ::XCore::EnqueueEagerCDO;
    using ::XCore::GetClassDefaultObject;
    using ::XCore::GetPendingEagerCDOCount;
    using ::XCore::IsEagerCDO;
    using ::XCore::Reflect::EClassFlags;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    Phase5L::ResetAllForTests();
    ::XCore::__ResetCDOsForTests();

    // -----------------------------------------------------------------
    // Build 4 EagerCDO-flagged FClasses.
    // -----------------------------------------------------------------
    constexpr int kEagerCount = 4;
    std::vector<FClass*> EagerClasses;
    EagerClasses.reserve(kEagerCount);

    for (int i = 0; i < kEagerCount; ++i)
    {
        // Per FClass test pattern, construct on the heap so the
        // pointer stays stable across the test body (TArray growth
        // would relocate stack FClasses).
        char Buf[32];
        std::snprintf(Buf, sizeof(Buf), "XINITEAGERClass%d", i);
        FClass* C = new FClass(FName(Buf), nullptr);
        C->PropertiesSize  = sizeof(::XCore::XObject);
        C->MinAlignment    = alignof(::XCore::XObject);
        C->ClassFlags      = EClassFlags::CLASS_EagerCDO;

        ::XCore::FXObjectAllocator::Get().RegisterClassPool(C);

        P5L_CHECK(IsEagerCDO(C),
                  "X-INIT-EAGER: IsEagerCDO did not detect "
                  "CLASS_EagerCDO");

        EagerClasses.push_back(C);
    }

    // -----------------------------------------------------------------
    // Enqueue every EagerCDO class.
    // -----------------------------------------------------------------
    for (FClass* C : EagerClasses)
    {
        EnqueueEagerCDO(C);
    }

    // Queue count matches enrolment count.
    P5L_CHECK(GetPendingEagerCDOCount() == kEagerCount,
              "X-INIT-EAGER: queue count != enrolment count");

    // -----------------------------------------------------------------
    // Idempotency: a second Enqueue for the same class is a no-op.
    // -----------------------------------------------------------------
    for (FClass* C : EagerClasses)
    {
        EnqueueEagerCDO(C);
    }
    P5L_CHECK(GetPendingEagerCDOCount() == kEagerCount,
              "X-INIT-EAGER: queue count grew on idempotent re-enqueue");

    // -----------------------------------------------------------------
    // Drain: produces exactly kEagerCount CDOs; queue empties.
    // -----------------------------------------------------------------
    const ::int32 Drained = DrainPendingEagerCDOs();
    P5L_CHECK(Drained == kEagerCount,
              "X-INIT-EAGER: DrainPendingEagerCDOs() did not match "
              "the EagerCDO-flagged FClass count");
    P5L_CHECK(GetPendingEagerCDOCount() == 0,
              "X-INIT-EAGER: g_PendingEagerCDOs not empty after drain");

    // -----------------------------------------------------------------
    // Every drained class now has a non-null CDO accessible via
    // FClass::GetCDO. The CDO was constructed exactly once (lazy
    // construction's second-call semantics).
    // -----------------------------------------------------------------
    for (FClass* C : EagerClasses)
    {
        const ::XCore::XObject* CDO = GetClassDefaultObject(C);
        P5L_CHECK(CDO != nullptr,
                  "X-INIT-EAGER: GetClassDefaultObject returned nullptr "
                  "for a drained EagerCDO class");

        // Second call returns the SAME pointer (cached).
        const ::XCore::XObject* CDO2 = GetClassDefaultObject(C);
        P5L_CHECK(CDO == CDO2,
                  "X-INIT-EAGER: GetClassDefaultObject second call "
                  "returned a different pointer (CDO not cached)");
    }

    // Cleanup.
    for (FClass* C : EagerClasses)
    {
        delete C;
    }

    return P5L_REPORT_PASS("FoundationPrototype.XINITEAGER_EagerCDOOrdering");
}
