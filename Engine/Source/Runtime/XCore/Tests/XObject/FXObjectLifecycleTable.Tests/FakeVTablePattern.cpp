// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectLifecycleTable.Tests/FakeVTablePattern.cpp -- per spec §2.4
// FakeVTable dispatch.
// =====================================================================
//
// Populates a custom table with function pointers + dispatches via
// the typed GetSlot<T> accessor. Verifies the function-pointer
// round-trip + the per-slot signature cast.
//
// =====================================================================

#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/XObject.h"

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

    // Per-slot diagnostic counters. The test populates the table with
    // function pointers that increment these counters; the dispatch
    // path invokes them via the typed slot accessors.
    std::atomic<int> g_PostInitFireCount{0};
    std::atomic<int> g_BeginDestroyFireCount{0};
    std::atomic<int> g_IsReadyForFinishDestroyResult{0};

    void TestPostInit(::XCore::XObject* Self) noexcept
    {
        (void)Self;
        g_PostInitFireCount.fetch_add(1, std::memory_order_acq_rel);
    }

    void TestBeginDestroy(::XCore::XObject* Self) noexcept
    {
        (void)Self;
        g_BeginDestroyFireCount.fetch_add(1, std::memory_order_acq_rel);
    }

    bool TestIsReadyForFinishDestroy(const ::XCore::XObject* Self) noexcept
    {
        (void)Self;
        // Increment to count invocations; return alternating true/false
        // to exercise the bool-return path.
        const int Prev = g_IsReadyForFinishDestroyResult.fetch_add(1, std::memory_order_acq_rel);
        return (Prev % 2) == 0;
    }
}

int main()
{
    using ::XCore::FXObjectLifecycleTable;
    using ::XCore::FXObjectGenericFn;
    using ::XCore::EXObjectLifecycleCapability;
    using ::XCore::EXObjectLifecycleSlot;
    using ::XCore::ToUnderlying;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Construct a custom table with three slots populated.
    // -----------------------------------------------------------------
    FXObjectLifecycleTable Table{};
    Table.Capabilities =
        ToUnderlying(EXObjectLifecycleCapability::HasPostInitProperties) |
        ToUnderlying(EXObjectLifecycleCapability::HasBeginDestroy) |
        ToUnderlying(EXObjectLifecycleCapability::HasIsReadyForFinishDestroy);

    Table.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::PostInitProperties)] =
        reinterpret_cast<FXObjectGenericFn>(&TestPostInit);
    Table.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::BeginDestroy)] =
        reinterpret_cast<FXObjectGenericFn>(&TestBeginDestroy);
    Table.Slots[static_cast<std::size_t>(EXObjectLifecycleSlot::IsReadyForFinishDestroy)] =
        reinterpret_cast<FXObjectGenericFn>(&TestIsReadyForFinishDestroy);

    // -----------------------------------------------------------------
    // Construct a stack XObject for the dispatch target. The placement-
    // new sets a sentinel non-null Class for IsValidLowLevel passes.
    // -----------------------------------------------------------------
    ::XCore::XObject Stub;

    // -----------------------------------------------------------------
    // Dispatch via typed GetSlot accessors.
    // -----------------------------------------------------------------
    {
        const auto Fn = Table.GetSlot<EXObjectLifecycleSlot::PostInitProperties>();
        Check(Fn != nullptr, "GetSlot<PostInitProperties> returned nullptr");
        if (Fn != nullptr)
        {
            Fn(&Stub);
            Fn(&Stub);
            Fn(&Stub);
        }
        Check(g_PostInitFireCount.load() == 3,
              "PostInit fire count != 3 after 3 dispatches");
    }

    {
        const auto Fn = Table.GetSlot<EXObjectLifecycleSlot::BeginDestroy>();
        Check(Fn != nullptr, "GetSlot<BeginDestroy> returned nullptr");
        if (Fn != nullptr)
        {
            Fn(&Stub);
        }
        Check(g_BeginDestroyFireCount.load() == 1,
              "BeginDestroy fire count != 1 after 1 dispatch");
    }

    {
        const auto Fn = Table.GetSlot<EXObjectLifecycleSlot::IsReadyForFinishDestroy>();
        Check(Fn != nullptr, "GetSlot<IsReadyForFinishDestroy> returned nullptr");
        if (Fn != nullptr)
        {
            // Call twice; first call returns true (prev=0; 0%2==0), second
            // returns false (prev=1; 1%2!=0).
            const bool A = Fn(&Stub);
            const bool B = Fn(&Stub);
            Check(A,   "IsReadyForFinishDestroy first call returned false (expected true)");
            Check(!B,  "IsReadyForFinishDestroy second call returned true (expected false)");
        }
        Check(g_IsReadyForFinishDestroyResult.load() == 2,
              "IsReadyForFinishDestroy invocation count != 2");
    }

    // -----------------------------------------------------------------
    // Slot whose Capabilities bit is clear -- the GetSlot accessor
    // still returns the raw (nullptr) value. The dispatcher MUST check
    // HasCapability first (or check the returned pointer for null).
    // -----------------------------------------------------------------
    {
        const auto Fn = Table.GetSlot<EXObjectLifecycleSlot::Serialize>();
        Check(Fn == nullptr, "GetSlot<Serialize> != nullptr for unpopulated slot");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectLifecycleTable.FakeVTablePattern: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectLifecycleTable.FakeVTablePattern: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
