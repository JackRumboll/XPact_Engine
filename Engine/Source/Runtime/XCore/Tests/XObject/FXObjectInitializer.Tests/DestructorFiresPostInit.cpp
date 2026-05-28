// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.Tests/DestructorFiresPostInit.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §8.4: ~FXObjectInitializer triggers
// LifecycleTable->PostInitPropertiesFn with the Target as the
// argument. Verifies the Self-pointer threading + the dispatch-via-
// FakeVTable mechanism.
//
// =====================================================================

#include "XObject/FXObjectInitializer.h"
#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"

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

    // The PostInit hook captures the Self pointer it was invoked with.
    std::atomic<::XCore::XObject*> g_CapturedSelf{nullptr};

    void TestPostInit(::XCore::XObject* Self) noexcept
    {
        g_CapturedSelf.store(Self, std::memory_order_release);
    }
}

int main()
{
    using ::XCore::FXObjectInitializer;
    using ::XCore::FXObjectLifecycleTable;
    using ::XCore::FXObjectGenericFn;
    using ::XCore::EXObjectLifecycleCapability;
    using ::XCore::EXObjectLifecycleSlot;
    using ::XCore::ToUnderlying;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectLifecycleTable Table{};
    Table.Capabilities = ToUnderlying(EXObjectLifecycleCapability::HasPostInitProperties);
    Table.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::PostInitProperties)] =
        reinterpret_cast<FXObjectGenericFn>(&TestPostInit);

    FClass TestClass(FName("XTarget"), nullptr);
    TestClass.LifecycleTable = &Table;

    XObject Target;
    Target.ClassPrivate = &TestClass;

    g_CapturedSelf.store(nullptr);

    {
        FXObjectInitializer Initializer(&Target, &TestClass);
        // Pre-destruction: g_CapturedSelf still nullptr.
        Check(g_CapturedSelf.load() == nullptr,
              "PostInit fired before scope exit");
    }
    // Post-destruction: g_CapturedSelf == &Target.
    Check(g_CapturedSelf.load() == &Target,
          "captured Self != Target after dtor fired");

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectInitializer.DestructorFiresPostInit: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectInitializer.DestructorFiresPostInit: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
