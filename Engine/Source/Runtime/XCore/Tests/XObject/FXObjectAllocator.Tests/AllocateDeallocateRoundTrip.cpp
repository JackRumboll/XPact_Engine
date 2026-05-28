// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/AllocateDeallocateRoundTrip.cpp -- basic
// allocate / write / deallocate round-trip (XCoreXObject Rev 4 §3.5).
// =====================================================================
//
// Verifies the fundamental allocator contract:
//
//   1. AllocateRaw returns a non-null cell of at least Size bytes.
//   2. The cell is uninitialised but writable through its full width.
//   3. Deallocate releases the cell back to the per-FClass free-list.
//   4. A subsequent AllocateRaw against the same Class hands back the
//      SAME pointer (cells reused by the same class come from the
//      class-specific free-list head, LIFO; per spec §3.4 "type-
//      segregated within size class" + §3.5 "fast path: pop from
//      class-specific free-list").
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"

#include <cstdint>
#include <cstring>
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
    using ::XCore::FXObjectAllocator;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    // FName intern table requires FMemory live (matches the Phase 5.a
    // test discipline).
    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    Allocator.__ResetForTests();

    // -----------------------------------------------------------------
    // Test 1: Allocate without an FClass (nullptr ClassDescriptor;
    // routes through the no-class default sub-pool per the allocator
    // header's "bootstrap path" note).
    //
    // The cell should be writable across its full 64 bytes (size class 0)
    // even though we requested only 48 bytes -- the size-class fit
    // rule (1.25x bound) maps 48 -> 64.
    // -----------------------------------------------------------------
    {
        void* Cell = Allocator.AllocateRaw(
            48,
            alignof(::std::max_align_t),
            nullptr);
        Check(Cell != nullptr, "AllocateRaw(48,nullcls) returned nullptr");

        // Write a recognisable byte pattern through the full 64-byte
        // cell width (the size-class 0 cell holds at least 64 bytes).
        ::std::memset(Cell, 0xAB, 64);

        // Read it back to confirm the storage is valid.
        const ::std::uint8_t* Bytes = static_cast<const ::std::uint8_t*>(Cell);
        for (int I = 0; I < 64; ++I)
        {
            Check(Bytes[I] == 0xAB, "Cell byte mismatch after write");
        }

        // Deallocate.
        Allocator.Deallocate(Cell);
    }

    // -----------------------------------------------------------------
    // Test 2: Allocate + Deallocate + Allocate against the SAME class
    // returns the SAME pointer (the freed cell is on the class's free-
    // list, which is a LIFO; the next pop returns it).
    // -----------------------------------------------------------------
    {
        FClass TestClass(FName("Test1Class"), nullptr);
        // PropertiesSize=80 lands in size class 1 (96 bytes; 80*1.25=100 > 96
        // so 80 actually lands in class 2 (128). Let's pick a size that
        // unambiguously maps to a class: PropertiesSize=120 lands in
        // class 2 (128 bytes; 120*1.25=150 > 128 fails; class 3 (192);
        // 120*1.25=150 <= 192 yes). So 120 -> class 3.
        TestClass.PropertiesSize = 120;
        TestClass.MinAlignment   = 8;

        Allocator.RegisterClassPool(&TestClass);

        void* CellA = Allocator.AllocateRaw(120, 8, &TestClass);
        Check(CellA != nullptr, "First AllocateRaw against TestClass returned nullptr");

        Allocator.Deallocate(CellA);

        void* CellB = Allocator.AllocateRaw(120, 8, &TestClass);
        Check(CellB != nullptr, "Second AllocateRaw against TestClass returned nullptr");

        Check(CellA == CellB,
              "Pop-from-class-free-list contract violated: same-class "
              "re-allocate did not return the freed cell");

        Allocator.Deallocate(CellB);
    }

    // -----------------------------------------------------------------
    // Test 3: Distinct allocations against the SAME class return
    // distinct pointers (the free-list is exhausted; the second
    // allocation steals a fresh cell from the slab's unassigned chain).
    // -----------------------------------------------------------------
    {
        FClass TestClass(FName("Test2Class"), nullptr);
        TestClass.PropertiesSize = 100;
        TestClass.MinAlignment   = 8;

        Allocator.RegisterClassPool(&TestClass);

        void* CellA = Allocator.AllocateRaw(100, 8, &TestClass);
        void* CellB = Allocator.AllocateRaw(100, 8, &TestClass);

        Check(CellA != nullptr && CellB != nullptr,
              "Distinct AllocateRaw calls returned nullptr");
        Check(CellA != CellB,
              "Two simultaneous live allocations returned the same pointer");

        Allocator.Deallocate(CellA);
        Allocator.Deallocate(CellB);
    }

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.AllocateDeallocateRoundTrip: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.AllocateDeallocateRoundTrip: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
