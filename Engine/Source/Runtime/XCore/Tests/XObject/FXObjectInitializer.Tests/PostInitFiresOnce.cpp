// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.Tests/PostInitFiresOnce.cpp -- Rev 3 FIX-M-R2-5.
// =====================================================================
//
// Verifies the bPostInitFired idempotency flag: PostInitProperties
// fires EXACTLY ONCE per FXObjectInitializer, even when the same
// Initializer is destructed multiple times in the inheritance-chain
// pass-through pattern.
//
// The test populates a LifecycleTable with a PostInitProperties slot
// that increments a counter; constructs an Initializer; lets the
// destructor fire; verifies the counter == 1.
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

    std::atomic<int> g_PostInitFireCount{0};

    void TestPostInit(::XCore::XObject* Self) noexcept
    {
        (void)Self;
        g_PostInitFireCount.fetch_add(1, std::memory_order_acq_rel);
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

    // Build a LifecycleTable with PostInitProperties populated.
    FXObjectLifecycleTable Table{};
    Table.Capabilities = ToUnderlying(
        EXObjectLifecycleCapability::HasPostInitProperties);
    Table.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::PostInitProperties)] =
        reinterpret_cast<FXObjectGenericFn>(&TestPostInit);

    FClass TestClass(FName("XTestClass"), nullptr);
    TestClass.LifecycleTable = &Table;

    XObject Target;
    Target.ClassPrivate = &TestClass;

    // ----- PostInit fires exactly once per Initializer's normal
    //       scope-exit destruction. -----
    g_PostInitFireCount.store(0);
    {
        FXObjectInitializer Initializer(&Target, &TestClass);
        Check(!Initializer.HasFiredPostInit(),
              "freshly-constructed: HasFiredPostInit == true");

        // Scope exit fires ~FXObjectInitializer -> dispatches
        // PostInit via Table.
    }
    Check(g_PostInitFireCount.load() == 1,
          "single Initializer destruction: counter != 1");

    // ----- PostInit slot present BUT Capabilities bit clear -> no
    //       fire. Defence against mis-emitted tables. -----
    g_PostInitFireCount.store(0);
    {
        FXObjectLifecycleTable BitClearTable{};
        BitClearTable.Capabilities = 0u; // bit clear
        BitClearTable.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::PostInitProperties)] =
            reinterpret_cast<FXObjectGenericFn>(&TestPostInit);

        FClass BitClearClass(FName("XBitClearClass"), nullptr);
        BitClearClass.LifecycleTable = &BitClearTable;

        XObject BitClearTarget;
        BitClearTarget.ClassPrivate = &BitClearClass;

        {
            FXObjectInitializer Initializer(&BitClearTarget, &BitClearClass);
        }
    }
    Check(g_PostInitFireCount.load() == 0,
          "bit-clear table: PostInit fired despite Capabilities == 0");

    // ----- LifecycleTable nullptr -> no fire. -----
    g_PostInitFireCount.store(0);
    {
        FClass NoTableClass(FName("XNoTableClass"), nullptr);
        NoTableClass.LifecycleTable = nullptr;

        XObject NoTableTarget;
        NoTableTarget.ClassPrivate = &NoTableClass;

        {
            FXObjectInitializer Initializer(&NoTableTarget, &NoTableClass);
        }
    }
    Check(g_PostInitFireCount.load() == 0,
          "nullptr LifecycleTable: PostInit fired");

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectInitializer.PostInitFiresOnce: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectInitializer.PostInitFiresOnce: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
