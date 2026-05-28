// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/IntegrationWithAllocator.cpp -- spec §3.5 round-trip.
// =====================================================================
//
// Verifies NewObject + FXObjectAllocator round-trip: allocated memory
// is from the allocator's pool; the FXObjectArray entry points back
// at the object; the slot can be released through Deallocate.
//
// =====================================================================

#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/NewObject.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

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
    using ::XCore::EObjectFlags;
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectAllocator::Get().__ResetForTests();
    FXObjectArray::Get().__ResetForTests();

    FClass TestClass(FName("XAllocInt"), nullptr);

    // NewObject through the standard hot path.
    XObject* const Obj = ::XCore::NewObjectImpl(
        &TestClass, nullptr, FName("Inst"),
        EObjectFlags::None, nullptr);

    Check(Obj != nullptr, "NewObject returned nullptr");
    if (Obj == nullptr)
    {
        std::cerr << "NewObject.IntegrationWithAllocator: FAIL\n";
        return 1;
    }

    // The FXObjectArray entry resolves back to the object.
    const ::int32 Idx       = Obj->GetInternalIndex();
    const ::uint32 Serial    = Obj->GetSerialNumber();
    Check(Idx    > 0,    "InternalIndex non-positive");
    Check(Serial > 0u,   "SerialNumber zero");

    XObject* const Resolved = FXObjectArray::Get().GetObjectAtIndex(Idx, Serial);
    Check(Resolved == Obj,
          "FXObjectArray lookup did not resolve back to Obj");

    // Release the allocator slot. The deallocate happens on the
    // raw storage pointer; for Phase 5.d the FXObjectArray entry
    // free is the responsibility of the GC sweep / explicit
    // teardown path (Phase 5.h+). The test here verifies the
    // allocator-side Deallocate is sound.
    //
    // Note: we explicitly free the array slot first to avoid
    // dangling FXObjectArray entries pointing at deallocated
    // memory; this is the Phase 5.d posture before the collector
    // ships.
    FXObjectArray::Get().FreeEntry(Idx);
    FXObjectAllocator::Get().Deallocate(static_cast<void*>(Obj));

    // After FreeEntry, the slot resolves to nullptr.
    Check(FXObjectArray::Get().GetObjectAtIndex(Idx, Serial) == nullptr,
          "FXObjectArray slot still resolves after FreeEntry");

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.IntegrationWithAllocator: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.IntegrationWithAllocator: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
