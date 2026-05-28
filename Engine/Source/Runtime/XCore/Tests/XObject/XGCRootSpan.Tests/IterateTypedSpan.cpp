// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.Tests/IterateTypedSpan.cpp -- typed kObject span walk
// (XCoreXObject Rev 4 §5.3; Phase 5.e).
// =====================================================================
//
// ForEachValidObjectInSpans visits every non-null XObject* slot in a
// typed span. Nullptr slots are skipped. Each XObject is visited
// exactly once per span (no dedup within a span; the GC mark phase
// handles cross-span dedup via the rotating reachability flag).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRootSpan.h"
#include "XObject/XObject.h"

#include <cstring>
#include <iostream>

int main()
{
    using ::XCore::EXGCRootSpanKind;
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRootSpan;
    using ::XCore::XGCRootSpanRegistry;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();
    XGCRootSpanRegistry& Registry = XGCRootSpanRegistry::Get();
    Registry.__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Register 16 XObjects so they have valid InternalIndex bindings;
    // their addresses go into the typed span.
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

    // Build the typed span: every even slot points to an Obj, every
    // odd slot is nullptr.
    XObject* SlotArray[kN];
    for (int I = 0; I < kN; ++I)
    {
        SlotArray[I] = ((I % 2) == 0) ? &Objs[I] : nullptr;
    }

    XGCRootSpan Span;
    Span.BaseAddress   = SlotArray;
    Span.ByteLength    = sizeof(SlotArray);
    Span.ElementStride = sizeof(XObject*);
    Span.Kind          = EXGCRootSpanKind::kObject;
    const ::int32 H = Registry.AddSpan(Span);
    Check(H >= 0, "AddSpan returned invalid handle");

    // Walk; record which Objs were visited.
    bool Visited[kN];
    std::memset(Visited, 0, sizeof(Visited));
    int VisitCount = 0;
    Registry.ForEachValidObjectInSpans(
        [&](XObject* Object) noexcept
        {
            for (int I = 0; I < kN; ++I)
            {
                if (Object == &Objs[I])
                {
                    if (Visited[I])
                    {
                        ++FailureCount;
                        std::cerr << "FAIL: object " << I << " visited twice\n";
                    }
                    Visited[I] = true;
                    ++VisitCount;
                    return;
                }
            }
            ++FailureCount;
            std::cerr << "FAIL: typed span yielded unknown XObject*\n";
        });

    Check(VisitCount == kN / 2,
          "typed span visit count != kN/2");
    for (int I = 0; I < kN; ++I)
    {
        if ((I % 2) == 0)
        {
            Check(Visited[I],
                  "even slot's XObject not visited in typed span");
        }
        else
        {
            Check(!Visited[I],
                  "odd slot's nullptr produced a spurious visit");
        }
    }

    // Cleanup.
    Registry.RemoveSpan(H);
    for (int I = 0; I < kN; ++I)
    {
        Array.FreeEntry(Indices[I]);
    }
    Registry.__ResetForTests();
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRootSpan.IterateTypedSpan: PASS\n";
        return 0;
    }
    std::cerr << "XGCRootSpan.IterateTypedSpan: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
