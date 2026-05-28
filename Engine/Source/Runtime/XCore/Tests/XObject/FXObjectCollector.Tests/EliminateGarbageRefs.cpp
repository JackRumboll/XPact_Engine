// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/EliminateGarbageRefs.cpp -- Phase 5.h sweep
// pass that nulls references to MarkedAsGarbage targets (FIX-A-HIGH-19).
// =====================================================================
//
// Verifies:
//
//   * A reachable Container XObject holds a reference to a Target
//     XObject via a single-slot Object schema opcode at known offset.
//   * The Target is MarkedAsGarbage (via XObject::MarkAsGarbage).
//   * The Container is pinned as a root so it survives sweep.
//   * Running CollectGarbage(kEliminateGarbageRefs) runs the
//     EliminateGarbageRefsPass which walks the Container's schema and
//     nulls the slot pointing at Target.
//   * Post-cycle: the slot reads as nullptr.
//   * GetLastGarbageRefsClearedCount reports >= 1.
//
// The Container's FClass has a hand-crafted FXObjectRefSchema with a
// single Object opcode pointing at the offset of TargetSlot.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "Reflection/FXObjectRefSchema.h"
#include "XObject/FXDeferredDestructionQueue.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXSweepCandidateQueue.h"
#include "XObject/XGCRoot.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <iostream>
#include <vector>

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

    // -----------------------------------------------------------------
    // FContainer -- XObject with a single XObject* slot at offset 56
    // (immediately after the XObject header).
    //
    // We use an aggregate that places the XObject base + the single
    // slot at known offsets. The FClass's RefSchema points at a single
    // Object opcode targeting offset 56.
    // -----------------------------------------------------------------
    struct FContainer
    {
        ::XCore::XObject Base;       // offset 0..55 (56 bytes)
        ::XCore::XObject* TargetSlot; // offset 56..63 (8 bytes)
    };

    static_assert(sizeof(FContainer) == 64,
                  "FContainer layout assumption: XObject@0..55 + slot@56");
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::EXGCOptions;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXSweepCandidateQueue;
    using ::XCore::FXDeferredDestructionQueue;
    using ::XCore::XGCRoot;
    using ::XCore::XObject;
    using ::XCore::Reflect::EXObjectRefSchemaOp;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FXObjectRefSchema;
    using ::XCore::Reflect::FXObjectRefSchemaOp;
    using ::XCore::Reflect::kFXObjectRefSchemaCurrentVersion;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    FXDeferredDestructionQueue::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();

    constexpr ::std::size_t kHeapBytes = 4 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(Heap.data(), kHeapBytes);

    FXObjectArray& Array = FXObjectArray::Get();
    FXObjectCollector& Coll = FXObjectCollector::Get();

    // -----------------------------------------------------------------
    // Build a hand-crafted schema with a single Object opcode at
    // offset 56 (the FContainer's TargetSlot field).
    //
    // The schema and ops array are static so they outlive any cycle
    // (matches the .rodata constinit invariant XHT enforces in
    // production).
    // -----------------------------------------------------------------
    static FXObjectRefSchemaOp s_Ops[] =
    {
        // Object opcode at offset 56.
        { EXObjectRefSchemaOp::Object,    /*_padOp*/0, /*ArrayDim*/0,
          /*Offset*/56, /*StrideBytes*/0, /*_padAlign*/0,
          /*NestedSchema*/nullptr },
        // Terminator.
        { EXObjectRefSchemaOp::Terminator, /*_padOp*/0, /*ArrayDim*/0,
          /*Offset*/0, /*StrideBytes*/0, /*_padAlign*/0,
          /*NestedSchema*/nullptr },
    };
    static FXObjectRefSchema s_Schema =
    {
        /*NumOps*/  static_cast<::std::uint32_t>(
            sizeof(s_Ops) / sizeof(s_Ops[0])),
        /*Version*/ kFXObjectRefSchemaCurrentVersion,
        /*Ops*/     s_Ops,
        /*_padTail*/0,
    };

    // -----------------------------------------------------------------
    // Construct the Container's FClass with the hand-crafted schema.
    // -----------------------------------------------------------------
    FClass ContainerClass(FName("FContainerTestClass"), nullptr);
    ContainerClass.PropertiesSize = static_cast<::int32>(sizeof(FContainer));
    ContainerClass.MinAlignment   = alignof(FContainer);
    ContainerClass.RefSchema      = &s_Schema;

    // -----------------------------------------------------------------
    // Allocate XObjects on the stack (matches Phase 5.g test pattern).
    //
    // Target: NOT root-pinned; we will mark it as garbage but ALSO pin
    // it indirectly via the Container's slot (keeps it alive past the
    // mark phase) -- wait, actually for this test we want to verify
    // the SLOT NULLING, not the BeginDestroy dispatch. The
    // EliminateGarbageRefs pass nulls slots regardless of whether the
    // target is reachable; we'll keep Target NOT root-pinned so the
    // sweep DOES enqueue it on the deferred queue, but the pass nulls
    // the Container's slot BEFORE the sweep's candidate enumeration
    // (per EnterSweep's body: Eliminate runs first, then Drain).
    //
    // To make this test deterministic, we ALSO pin the Target as a
    // root so it survives the sweep -- but flagged as MarkedAsGarbage.
    // The test asserts the slot transitions to nullptr; whether the
    // Target is reclaimed is orthogonal.
    // -----------------------------------------------------------------
    FContainer Container;
    // XObject is non-copyable + non-movable; the default-constructed
    // Base is already in the freshly-zeroed state we want. We mutate
    // the ClassPrivate field directly (matching the Phase 5.g test
    // pattern's stack-XObject construction).
    Container.Base.ClassPrivate = &ContainerClass;

    XObject Target;
    Target.ClassPrivate = nullptr;  // no schema; not a GC concern

    // Register both with the array.
    {
        ::uint32 ContSerial = 0;
        const ::int32 ContIdx = Array.ReserveSlot(&ContSerial);
        Container.Base.InternalIndex = ContIdx;
        Container.Base.SerialNumber  = ContSerial;
        Array.BindObject(ContIdx, &Container.Base);

        ::uint32 TgtSerial = 0;
        const ::int32 TgtIdx = Array.ReserveSlot(&TgtSerial);
        Target.InternalIndex = TgtIdx;
        Target.SerialNumber  = TgtSerial;
        Array.BindObject(TgtIdx, &Target);

        // Container holds a reference to Target.
        Container.TargetSlot = &Target;

        // Pin both as roots so neither is reclaimed (we're testing
        // the slot-nulling pass, not the sweep dispatch).
        Check(XGCRoot::AddRoot(&Container.Base),
              "AddRoot(Container) returned false");
        Check(XGCRoot::AddRoot(&Target),
              "AddRoot(Target) returned false");

        // Mark Target as garbage so the EliminateGarbageRefs pass
        // sees its EObjectFlags::MarkedAsGarbage bit and nulls the
        // Container's slot.
        Target.SetFlags(EObjectFlags::MarkedAsGarbage);
    }

    // Pre-cycle sanity: the slot points at Target.
    Check(Container.TargetSlot == &Target,
          "Pre-cycle: Container.TargetSlot is not &Target");

    // -----------------------------------------------------------------
    // Run a synchronous cycle with kEliminateGarbageRefs set.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kEliminateGarbageRefs);

    // -----------------------------------------------------------------
    // Post-cycle:
    //   * Container.TargetSlot must be nullptr (the pass nulled it).
    //   * GetLastGarbageRefsClearedCount must be >= 1.
    // -----------------------------------------------------------------
    Check(Container.TargetSlot == nullptr,
          "Post-cycle: Container.TargetSlot was not nulled by EliminateGarbageRefsPass");
    Check(Coll.GetLastGarbageRefsClearedCount() >= 1,
          "Post-cycle: GetLastGarbageRefsClearedCount < 1");

    // -----------------------------------------------------------------
    // Cleanup.
    // -----------------------------------------------------------------
    (void)XGCRoot::RemoveRoot(&Container.Base);
    (void)XGCRoot::RemoveRoot(&Target);
    Array.FreeEntry(Container.Base.InternalIndex);
    Array.FreeEntry(Target.InternalIndex);
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    FXDeferredDestructionQueue::Get().__ResetForTests();
    Coll.__ResetForTests();
    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.EliminateGarbageRefs: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.EliminateGarbageRefs: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
