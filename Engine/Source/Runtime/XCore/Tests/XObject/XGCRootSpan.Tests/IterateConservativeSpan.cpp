// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.Tests/IterateConservativeSpan.cpp -- conservative walk
// (XCoreXObject Rev 4 §5.3 + Rev 2 FIX-A-MED-35; Phase 5.e).
// =====================================================================
//
// Conservative spans validate each candidate via the four-gate order
// (heap-range -> FXObjectArray index -> entry-bind -> SerialNumber
// match). The walker visits ONLY validated candidates; garbage / stale
// / non-heap pointers are silently filtered.
//
// Test scenario: a span backed by an array mixing
//   * Real XObject* addresses (live, allocated via FXObjectAllocator),
//   * Stack-XObject addresses (NOT heap-allocated; fail gate 1),
//   * Garbage non-heap pointers (fail gate 1),
//   * Nullptr (fail at zero-check).
//
// The walker MUST visit only the heap-allocated XObjects.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRootSpan.h"
#include "XObject/XObject.h"

#include <cstring>
#include <iostream>

namespace
{
    // Sentinel FClass pointer for the allocator's NoClass path. The
    // allocator accepts nullptr for the FClass parameter (bootstrap
    // path) so we pass nullptr.
    constexpr const ::XCore::Reflect::FClass* kNoClass = nullptr;
}

int main()
{
    using ::XCore::EXGCRootSpanKind;
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRootSpan;
    using ::XCore::XGCRootSpanRegistry;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();
    XGCRootSpanRegistry& Registry = XGCRootSpanRegistry::Get();
    Registry.__ResetForTests();
    // Reset allocator state so IsHeapAddress reflects only this test's
    // allocations.
    FXObjectAllocator::Get().__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // -----------------------------------------------------------------
    // Allocate 4 heap XObjects via the allocator; register each with
    // FXObjectArray so the four-gate validation passes.
    // -----------------------------------------------------------------
    constexpr int kHeap = 4;
    XObject* HeapObjs[kHeap];
    for (int I = 0; I < kHeap; ++I)
    {
        void* Storage = FXObjectAllocator::Get().AllocateRaw(
            sizeof(XObject), alignof(XObject), kNoClass);
        Check(Storage != nullptr, "allocator returned nullptr");
        HeapObjs[I] = new (Storage) XObject();

        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        HeapObjs[I]->InternalIndex = Idx;
        HeapObjs[I]->SerialNumber  = Serial;
        Array.BindObject(Idx, HeapObjs[I]);
    }

    // -----------------------------------------------------------------
    // Build a Conservative span: alternate real heap pointer + garbage.
    // -----------------------------------------------------------------
    // A stack-local XObject is NOT in the heap range; its address fails
    // gate 1.
    XObject StackObj;
    ::uint32 StackSerial = 0;
    const ::int32 StackIdx = Array.ReserveSlot(&StackSerial);
    StackObj.InternalIndex = StackIdx;
    StackObj.SerialNumber  = StackSerial;
    Array.BindObject(StackIdx, &StackObj);

    // A garbage non-heap pointer (a non-null but non-heap address).
    XObject* GarbagePtr = reinterpret_cast<XObject*>(static_cast<::uintptr_t>(0x1234));

    constexpr int kSlots = 8;
    XObject* SlotArray[kSlots];
    SlotArray[0] = HeapObjs[0];     // valid; visit
    SlotArray[1] = nullptr;         // filtered
    SlotArray[2] = HeapObjs[1];     // valid; visit
    SlotArray[3] = &StackObj;       // fails gate 1 (not in heap); skip
    SlotArray[4] = HeapObjs[2];     // valid; visit
    SlotArray[5] = GarbagePtr;      // fails gate 1; skip
    SlotArray[6] = HeapObjs[3];     // valid; visit
    SlotArray[7] = nullptr;         // filtered

    XGCRootSpan Span;
    Span.BaseAddress   = SlotArray;
    Span.ByteLength    = sizeof(SlotArray);
    Span.ElementStride = sizeof(XObject*);
    Span.Kind          = EXGCRootSpanKind::kConservative;
    const ::int32 H = Registry.AddSpan(Span);
    Check(H >= 0, "Conservative AddSpan failed");
    Check(Registry.GetConservativeSpanCount() == 1u,
          "GetConservativeSpanCount != 1 after adding kConservative span");

    // Walk; record visits.
    bool Visited[kHeap];
    std::memset(Visited, 0, sizeof(Visited));
    int VisitCount = 0;
    Registry.ForEachValidObjectInSpans(
        [&](XObject* Object) noexcept
        {
            // Identify which HeapObjs slot this is.
            for (int I = 0; I < kHeap; ++I)
            {
                if (Object == HeapObjs[I])
                {
                    if (Visited[I])
                    {
                        ++FailureCount;
                        std::cerr << "FAIL: heap object " << I
                                  << " visited twice\n";
                    }
                    Visited[I] = true;
                    ++VisitCount;
                    return;
                }
            }
            ++FailureCount;
            if (Object == &StackObj)
            {
                std::cerr << "FAIL: stack XObject visited (gate 1 failed)\n";
            }
            else
            {
                std::cerr << "FAIL: unknown candidate produced visit\n";
            }
        });

    Check(VisitCount == kHeap,
          "Conservative scan visit count != kHeap");
    for (int I = 0; I < kHeap; ++I)
    {
        Check(Visited[I], "heap-allocated XObject not visited");
    }

    // Cleanup.
    Registry.RemoveSpan(H);
    Array.FreeEntry(StackIdx);
    for (int I = 0; I < kHeap; ++I)
    {
        const ::int32 Idx = HeapObjs[I]->InternalIndex;
        // Call dtor before deallocating the slab cell.
        HeapObjs[I]->~XObject();
        FXObjectAllocator::Get().Deallocate(HeapObjs[I]);
        Array.FreeEntry(Idx);
    }
    Registry.__ResetForTests();
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRootSpan.IterateConservativeSpan: PASS\n";
        return 0;
    }
    std::cerr << "XGCRootSpan.IterateConservativeSpan: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
