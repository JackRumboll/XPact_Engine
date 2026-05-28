// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.Tests/GetRootedCount.cpp -- pinned-object counter
// (XCoreXObject Rev 4 §5.2; Phase 5.e linear-scan baseline).
// =====================================================================
//
// GetRootedCount walks FXObjectArray under SHARED lock counting entries
// with kRootPinnedBit set. The Phase 5.e implementation is an O(N)
// linear scan; this test validates the count tracks AddRoot /
// RemoveRoot transitions correctly.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRoot.h"
#include "XObject/XObject.h"

#include <iostream>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRoot;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Baseline: empty -> 0 pinned.
    Check(XGCRoot::GetRootedCount() == 0u,
          "baseline: GetRootedCount != 0");

    // Register 16 stack XObjects.
    constexpr int kN = 16;
    XObject Objs[kN];
    ::int32 Indices[kN];
    for (int I = 0; I < kN; ++I)
    {
        ::uint32 Serial = 0;
        Indices[I] = Array.ReserveSlot(&Serial);
        Objs[I].InternalIndex = Indices[I];
        Objs[I].SerialNumber  = Serial;
        Array.BindObject(Indices[I], &Objs[I]);
    }

    // No pins yet: count == 0.
    Check(XGCRoot::GetRootedCount() == 0u,
          "post-register: GetRootedCount != 0 (no AddRoot yet)");

    // Pin half of them.
    for (int I = 0; I < kN; I += 2)
    {
        (void)XGCRoot::AddRoot(&Objs[I]);
    }
    Check(XGCRoot::GetRootedCount() == static_cast<::std::size_t>(kN / 2),
          "post-half-pin: GetRootedCount != kN/2");

    // Pin the rest.
    for (int I = 1; I < kN; I += 2)
    {
        (void)XGCRoot::AddRoot(&Objs[I]);
    }
    Check(XGCRoot::GetRootedCount() == static_cast<::std::size_t>(kN),
          "post-full-pin: GetRootedCount != kN");

    // Unpin a few.
    (void)XGCRoot::RemoveRoot(&Objs[0]);
    (void)XGCRoot::RemoveRoot(&Objs[5]);
    (void)XGCRoot::RemoveRoot(&Objs[15]);
    Check(XGCRoot::GetRootedCount() == static_cast<::std::size_t>(kN - 3),
          "post-three-unpin: GetRootedCount != kN-3");

    // Idempotent unpins do NOT change count.
    (void)XGCRoot::RemoveRoot(&Objs[0]);
    (void)XGCRoot::RemoveRoot(&Objs[5]);
    Check(XGCRoot::GetRootedCount() == static_cast<::std::size_t>(kN - 3),
          "post-redundant-unpin: GetRootedCount != kN-3");

    // Unpin all the rest.
    for (int I = 0; I < kN; ++I)
    {
        (void)XGCRoot::RemoveRoot(&Objs[I]);
    }
    Check(XGCRoot::GetRootedCount() == 0u,
          "post-unpin-all: GetRootedCount != 0");

    // Cleanup.
    for (int I = 0; I < kN; ++I)
    {
        Array.FreeEntry(Indices[I]);
    }
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRoot.GetRootedCount: PASS\n";
        return 0;
    }
    std::cerr << "XGCRoot.GetRootedCount: " << FailureCount << " FAIL(s)\n";
    return 1;
}
