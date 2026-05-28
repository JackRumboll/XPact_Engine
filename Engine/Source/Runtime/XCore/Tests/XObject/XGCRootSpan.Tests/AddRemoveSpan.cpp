// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.Tests/AddRemoveSpan.cpp -- registry round-trip
// (XCoreXObject Rev 4 §5.3; Phase 5.e).
// =====================================================================
//
// AddSpan returns a non-negative handle; RemoveSpan unregisters it.
// Verifies:
//   * The first AddSpan returns handle 0 (bump-allocation start).
//   * Successive AddSpans return increasing handles.
//   * RemoveSpan reuses the slot (the next AddSpan returns the freed
//     handle, not the bump head).
//   * RemoveSpan on an invalid handle is a no-op.
//   * AddSpan with malformed shape (ElementStride != 8) returns the
//     sentinel invalid handle.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRootSpan.h"
#include "XObject/XObject.h"

#include <iostream>

int main()
{
    using ::XCore::EXGCRootSpanKind;
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRootSpan;
    using ::XCore::XGCRootSpanRegistry;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
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

    // -----------------------------------------------------------------
    // Build a small XObject* array as the backing storage for the
    // typed span. Values can be nullptr; AddSpan does not deref.
    // -----------------------------------------------------------------
    XObject* SlotArray[8] = { nullptr };

    XGCRootSpan Span;
    Span.BaseAddress   = SlotArray;
    Span.ByteLength    = sizeof(SlotArray);
    Span.ElementStride = sizeof(XObject*);
    Span.Kind          = EXGCRootSpanKind::kObject;

    // -----------------------------------------------------------------
    // First AddSpan: returns 0 (bump-allocation start).
    // -----------------------------------------------------------------
    const ::int32 H0 = Registry.AddSpan(Span);
    Check(H0 == 0, "first AddSpan handle != 0");
    Check(Registry.GetSpanCount() == 1u, "GetSpanCount after AddSpan #1 != 1");

    // -----------------------------------------------------------------
    // Second AddSpan: returns 1.
    // -----------------------------------------------------------------
    const ::int32 H1 = Registry.AddSpan(Span);
    Check(H1 == 1, "second AddSpan handle != 1");
    Check(Registry.GetSpanCount() == 2u, "GetSpanCount after AddSpan #2 != 2");

    // -----------------------------------------------------------------
    // RemoveSpan(H0) leaves H1 valid + reuses H0's slot for the next
    // AddSpan.
    // -----------------------------------------------------------------
    Registry.RemoveSpan(H0);
    Check(Registry.GetSpanCount() == 1u, "GetSpanCount after RemoveSpan != 1");
    const ::int32 H2 = Registry.AddSpan(Span);
    Check(H2 == H0, "reused-slot handle != H0 (free-list LIFO violated)");
    Check(Registry.GetSpanCount() == 2u, "GetSpanCount after reuse-AddSpan != 2");

    // -----------------------------------------------------------------
    // Idempotent RemoveSpan: re-removing an already-removed handle
    // is a no-op.
    // -----------------------------------------------------------------
    Registry.RemoveSpan(H1);
    Registry.RemoveSpan(H1);   // double-remove
    Registry.RemoveSpan(H1);   // triple-remove
    Check(Registry.GetSpanCount() == 1u,
          "GetSpanCount after triple-RemoveSpan != 1");

    // -----------------------------------------------------------------
    // RemoveSpan on negative / way-out-of-range handles: no-op.
    // -----------------------------------------------------------------
    Registry.RemoveSpan(-1);
    Registry.RemoveSpan(-42);
    Registry.RemoveSpan(99999);
    Check(Registry.GetSpanCount() == 1u,
          "GetSpanCount after invalid-handle RemoveSpan churn != 1");

    // -----------------------------------------------------------------
    // Malformed span: ElementStride != 8.
    // -----------------------------------------------------------------
    {
        XGCRootSpan BadSpan = Span;
        BadSpan.ElementStride = 16;  // non-8 stride; rejected at Phase 5.e
        const ::int32 BadHandle = Registry.AddSpan(BadSpan);
        Check(BadHandle == ::XCore::kXGCRootSpanInvalidHandle,
              "malformed-stride AddSpan did not return invalid sentinel");
        Check(Registry.GetSpanCount() == 1u,
              "GetSpanCount changed after rejected AddSpan");
    }

    // -----------------------------------------------------------------
    // Malformed: ByteLength % ElementStride != 0.
    // -----------------------------------------------------------------
    {
        XGCRootSpan BadSpan;
        BadSpan.BaseAddress   = SlotArray;
        BadSpan.ByteLength    = 17;  // not a multiple of 8
        BadSpan.ElementStride = 8;
        BadSpan.Kind          = EXGCRootSpanKind::kObject;
        const ::int32 BadHandle = Registry.AddSpan(BadSpan);
        Check(BadHandle == ::XCore::kXGCRootSpanInvalidHandle,
              "non-multiple ByteLength AddSpan did not return invalid sentinel");
    }

    // -----------------------------------------------------------------
    // Malformed: nullptr base with non-zero length.
    // -----------------------------------------------------------------
    {
        XGCRootSpan BadSpan;
        BadSpan.BaseAddress   = nullptr;
        BadSpan.ByteLength    = 16;
        BadSpan.ElementStride = 8;
        BadSpan.Kind          = EXGCRootSpanKind::kObject;
        const ::int32 BadHandle = Registry.AddSpan(BadSpan);
        Check(BadHandle == ::XCore::kXGCRootSpanInvalidHandle,
              "nullptr-base non-zero length AddSpan did not return invalid sentinel");
    }

    // -----------------------------------------------------------------
    // Valid empty span (length 0; nullptr base) succeeds.
    // -----------------------------------------------------------------
    {
        XGCRootSpan EmptySpan;
        EmptySpan.BaseAddress   = nullptr;
        EmptySpan.ByteLength    = 0;
        EmptySpan.ElementStride = 8;
        EmptySpan.Kind          = EXGCRootSpanKind::kObject;
        const ::int32 EmptyHandle = Registry.AddSpan(EmptySpan);
        Check(EmptyHandle >= 0,
              "valid empty span AddSpan returned invalid sentinel");
        if (EmptyHandle >= 0) Registry.RemoveSpan(EmptyHandle);
    }

    // Cleanup.
    Registry.RemoveSpan(H2);
    Registry.__ResetForTests();
    FXObjectArray::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRootSpan.AddRemoveSpan: PASS\n";
        return 0;
    }
    std::cerr << "XGCRootSpan.AddRemoveSpan: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
