// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/PostInitFires.cpp -- X-INIT-OBJ acceptance gate.
// =====================================================================
//
// XCoreXObject Rev 4 §3.5 + §8.4 (X-INIT-OBJ): PostInitProperties
// fires exactly once per NewObject after the constructor body has
// completed. The FXObjectInitializer's destructor at NewObject scope
// exit is the canonical trigger.
//
// =====================================================================

#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/NewObject.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <atomic>
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

    std::atomic<int> g_PostInitFireCount{0};
    std::atomic<::XCore::XObject*> g_LastSelf{nullptr};

    void TestPostInit(::XCore::XObject* Self) noexcept
    {
        g_PostInitFireCount.fetch_add(1, std::memory_order_acq_rel);
        g_LastSelf.store(Self, std::memory_order_release);
    }
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectLifecycleTable;
    using ::XCore::FXObjectGenericFn;
    using ::XCore::EXObjectLifecycleCapability;
    using ::XCore::EXObjectLifecycleSlot;
    using ::XCore::ToUnderlying;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    FXObjectLifecycleTable Table{};
    Table.Capabilities = ToUnderlying(EXObjectLifecycleCapability::HasPostInitProperties);
    Table.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::PostInitProperties)] =
        reinterpret_cast<FXObjectGenericFn>(&TestPostInit);

    FClass TestClass(FName("XPostInit"), nullptr);
    TestClass.LifecycleTable = &Table;

    g_PostInitFireCount.store(0);
    g_LastSelf.store(nullptr);

    XObject* const Obj = ::XCore::NewObjectImpl(
        &TestClass, nullptr, FName("Inst"), EObjectFlags::None, nullptr);

    Check(Obj != nullptr, "NewObjectImpl returned nullptr");
    Check(g_PostInitFireCount.load() == 1,
          "PostInit fire count != 1 after NewObject");
    Check(g_LastSelf.load() == Obj,
          "PostInit Self pointer != NewObject return");

    // Multiple NewObject calls each fire PostInit once.
    g_PostInitFireCount.store(0);
    const FName BaseName("Inst");
    for (int i = 0; i < 5; ++i)
    {
        // FName::WithNumber sets a SerialNumber suffix; the same
        // intern entry is shared across the 5 names.
        ::XCore::NewObjectImpl(&TestClass, nullptr,
                               FName::WithNumber(BaseName, static_cast<::uint32>(i + 1)),
                               EObjectFlags::None, nullptr);
    }
    Check(g_PostInitFireCount.load() == 5,
          "PostInit fire count != 5 after 5 NewObjects");

    // RF_NeedLoad bypasses PostInit dispatch (per Rev 3 FIX-M-R2-4 +
    // spec §10.6.1).
    g_PostInitFireCount.store(0);
    XObject* const LoaderObj = ::XCore::NewObjectImpl(
        &TestClass, nullptr, FName("LoaderInst"),
        EObjectFlags::NeedLoad, nullptr);
    (void)LoaderObj;
    Check(g_PostInitFireCount.load() == 0,
          "PostInit fired despite RF_NeedLoad bypass");

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.PostInitFires: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.PostInitFires: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
