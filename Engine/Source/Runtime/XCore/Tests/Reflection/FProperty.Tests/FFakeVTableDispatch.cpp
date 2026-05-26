// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/FFakeVTableDispatch.cpp -- FFakeVTable dispatch
// correctness (XCore-4b §5.4 + §5.4.1).
// =====================================================================
//
// Verifies:
//
//   1. CapabilityBit produces the correct single-bit mask for each
//      ESlot enumerator.
//   2. HasSlot returns true for populated slots and false for unpopulated.
//   3. GetSlot<T>() returns the function pointer for populated slots
//      and nullptr for unpopulated slots.
//   4. A constinit FFakeVTable in .rodata is dispatchable end-to-end.
//
// =====================================================================

#include "Reflection/FFakeVTable.h"

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

    // A test dispatch body.
    int g_LastCalledSlot = -1;
    int g_LastCallSum    = 0;

    void TestGetValueImpl(const void* /*Instance*/, ::int32 ElementIndex, void* OutValue)
    {
        g_LastCalledSlot = 0;
        g_LastCallSum    = ElementIndex + 100;
        *static_cast<int*>(OutValue) = 42;
    }

    void TestSetValueImpl(void* /*Instance*/, ::int32 ElementIndex, const void* InValue)
    {
        g_LastCalledSlot = 1;
        g_LastCallSum    = ElementIndex + *static_cast<const int*>(InValue);
    }

    void TestCopySingleValueImpl(void* /*Dest*/, const void* /*Src*/)
    {
        g_LastCalledSlot = 2;
    }
}

int main()
{
    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // (1) CapabilityBit -- mask-position correctness.
    // -----------------------------------------------------------------
    Check(CapabilityBit(ESlot::GetValue)         == 0x0001u,
          "CapabilityBit(GetValue) != 0x0001");
    Check(CapabilityBit(ESlot::SetValue)         == 0x0002u,
          "CapabilityBit(SetValue) != 0x0002");
    Check(CapabilityBit(ESlot::CopySingleValue)  == 0x0004u,
          "CapabilityBit(CopySingleValue) != 0x0004");
    Check(CapabilityBit(ESlot::Identical)        == 0x0040u,
          "CapabilityBit(Identical) != 0x0040");
    Check(CapabilityBit(ESlot::SerializeItem)    == 0x0080u,
          "CapabilityBit(SerializeItem) != 0x0080");
    Check(CapabilityBit(ESlot::ConvertFromType)  == 0x4000u,
          "CapabilityBit(ConvertFromType) != 0x4000");

    // -----------------------------------------------------------------
    // (2) Construct a runtime FFakeVTable with three populated slots.
    // -----------------------------------------------------------------
    FFakeVTable Vtable{};
    Vtable.Capabilities    = CapabilityBit(ESlot::GetValue)
                           | CapabilityBit(ESlot::SetValue)
                           | CapabilityBit(ESlot::CopySingleValue);
    Vtable._reservedHeader = 0;
    Vtable.Slots[static_cast<std::uint32_t>(ESlot::GetValue)]        =
        reinterpret_cast<void(*)(void)>(&TestGetValueImpl);
    Vtable.Slots[static_cast<std::uint32_t>(ESlot::SetValue)]        =
        reinterpret_cast<void(*)(void)>(&TestSetValueImpl);
    Vtable.Slots[static_cast<std::uint32_t>(ESlot::CopySingleValue)] =
        reinterpret_cast<void(*)(void)>(&TestCopySingleValueImpl);

    // HasSlot truth-table.
    Check( Vtable.HasSlot(ESlot::GetValue),
          "HasSlot(GetValue) returned false");
    Check( Vtable.HasSlot(ESlot::SetValue),
          "HasSlot(SetValue) returned false");
    Check( Vtable.HasSlot(ESlot::CopySingleValue),
          "HasSlot(CopySingleValue) returned false");
    Check(!Vtable.HasSlot(ESlot::Identical),
          "HasSlot(Identical) returned true (unpopulated slot)");
    Check(!Vtable.HasSlot(ESlot::ConvertFromType),
          "HasSlot(ConvertFromType) returned true (unpopulated slot)");

    // -----------------------------------------------------------------
    // (3) GetSlot<T>() typed-cast correctness.
    // -----------------------------------------------------------------
    using FGetValueFn = void(*)(const void*, ::int32, void*);
    using FSetValueFn = void(*)(void*, ::int32, const void*);
    using FCopyFn     = void(*)(void*, const void*);

    auto* GetFn = Vtable.GetSlot<FGetValueFn>(ESlot::GetValue);
    Check(GetFn != nullptr, "GetSlot<FGetValueFn>(GetValue) returned null");

    auto* SetFn = Vtable.GetSlot<FSetValueFn>(ESlot::SetValue);
    Check(SetFn != nullptr, "GetSlot<FSetValueFn>(SetValue) returned null");

    auto* IdenticalFn = Vtable.GetSlot<FCopyFn>(ESlot::Identical);
    Check(IdenticalFn == nullptr,
          "GetSlot<...>(Identical unpopulated) did not return null");

    // -----------------------------------------------------------------
    // (4) End-to-end dispatch.
    // -----------------------------------------------------------------
    int OutVal = 0;
    GetFn(nullptr, 7, &OutVal);
    Check(g_LastCalledSlot == 0,
          "GetSlot dispatch did not invoke GetValueImpl (LastCalledSlot != 0)");
    Check(g_LastCallSum    == 107,
          "GetValueImpl ElementIndex arg mismatch");
    Check(OutVal           == 42,
          "GetValueImpl OutValue mismatch");

    g_LastCalledSlot = -1;
    int InVal = 33;
    SetFn(nullptr, 5, &InVal);
    Check(g_LastCalledSlot == 1,
          "SetSlot dispatch did not invoke SetValueImpl (LastCalledSlot != 1)");
    Check(g_LastCallSum    == 38,
          "SetValueImpl arg-sum mismatch (5 + 33 = 38)");

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.FFakeVTableDispatch: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.FFakeVTableDispatch: PASS\n";
    return 0;
}
