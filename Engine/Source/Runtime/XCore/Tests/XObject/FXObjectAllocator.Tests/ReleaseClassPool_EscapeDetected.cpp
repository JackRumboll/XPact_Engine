// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/ReleaseClassPool_EscapeDetected.cpp --
// scenario-boundary mark-region clearing escape-detection veto path
// (XCoreXObject Rev 4 §3.6 + Foundation Prototype X11 per FIX-A-MIN-53;
// Phase 5.i).
// =====================================================================
//
// X11 acceptance criterion (Rev 2 refined per FIX-A-MIN-53):
//   "scenario-boundary mark-region clearing reclaims 100% of scenario-
//    scoped XObjects on synthetic 1000-instance / 100-class scenarios;
//    falls back to GC-driven collection when escape detected. Test
//    surface: per-class sub-pool walk verifies no escape; on detected
//    escape, the sub-pool returns to free-list with O(1) accounting;
//    SerialNumber bumps for every released slot."
//
// This test exercises the ESCAPE-DETECTED veto path. We:
//
//   1. Set up TestClass + ExternalClass at the SAME size class so they
//      could share a slab (correctness across-slab does not change).
//
//   2. Allocate N instances of TestClass + 1 instance of ExternalClass.
//
//   3. Configure ExternalClass's RefSchema to contain ONE Object
//      opcode at a known offset within its instance bytes.
//
//   4. Plant the address of ONE TestClass instance at that offset
//      within the ExternalClass instance. This simulates the
//      "external reference into the sub-pool" pattern.
//
//   5. Call ReleaseClassPool(&TestClass). The escape-detection walk:
//      - Visits the ExternalClass instance.
//      - Reads its schema -> Object opcode at the planted offset.
//      - Reads the pointer at that offset -> the TestClass cell.
//      - Binary-searches the TestClass cell list -> hit.
//      - Sets bEscapeDetected = true.
//      - Returns Err(ReferenceFromOutsideSubpool).
//
//   6. Verify:
//      a. Returned Result is Err with kind ReferenceFromOutsideSubpool.
//      b. The TestClass instances are STILL LIVE (their SerialNumbers
//         did NOT bump; the array entries still resolve).
//      c. A subsequent ReleaseClassPool call after we clear the planted
//         ref STILL detects the issue (consistency).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "Reflection/FXObjectRefSchema.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/XObject.h"

#include <iostream>
#include <new>
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
    // ExternalInstance -- the layout we use for the schema-bearing
    // "external" XObject. The XObject header is at offset 0; a single
    // XObject* slot is at offset sizeof(XObject) (i.e., immediately
    // after the header). The Object opcode's Offset field points at
    // sizeof(XObject) + 0 = sizeof(XObject).
    //
    // NOTE: sizeof(XObject) is 56 bytes per spec §2.2 (Rev 2). We
    // reserve 64 bytes total for the instance (header + one ref slot
    // + padding) to fit into size class 1 (96 bytes) cleanly.
    // -----------------------------------------------------------------
    struct ExternalInstance
    {
        ::XCore::XObject Header;       //  0  +56
        ::XCore::XObject* RefSlot;     // 56  +8
    };
}

int main()
{
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FScenarioBoundaryError;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FXObjectRefSchema;
    using ::XCore::Reflect::FXObjectRefSchemaOp;
    using ::XCore::Reflect::EXObjectRefSchemaOp;
    using ::XCore::Reflect::kFXObjectRefSchemaCurrentVersion;

    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    FXObjectArray&     Array     = FXObjectArray::Get();
    FXObjectCollector& Collector = FXObjectCollector::Get();

    Allocator.__ResetForTests();
    Array.__ResetForTests();
    Collector.__ResetForTests();

    // -----------------------------------------------------------------
    // Setup TestClass (instances of this are the scenario-scoped
    // sub-pool we'll attempt to release).
    // -----------------------------------------------------------------
    FClass TestClass(FName("EscapeTestClass"), nullptr);
    TestClass.PropertiesSize = sizeof(XObject);
    TestClass.MinAlignment   = alignof(XObject);

    Allocator.RegisterClassPool(&TestClass);

    // -----------------------------------------------------------------
    // Setup ExternalClass with a single-Object-opcode schema. The
    // schema is statically-storage-duration (matches XHT-emit's .rodata
    // posture). The Object opcode's Offset is the byte distance from
    // the instance base to the ref slot.
    //
    // NOTE: we use a DIFFERENT XObject layout for the external class
    // (ExternalInstance above). Its size = 64 bytes (sizeof(XObject) +
    // sizeof(XObject*) = 56 + 8 = 64) which routes to size class 0
    // (64 bytes). TestClass at 56 bytes ALSO routes to size class 0
    // per the 1.25x fit rule (56 * 4 = 224 <= 64 * 5 = 320), so the
    // two classes share a size-class pool which exercises the
    // shared-slab discipline of ReleaseClassPool.
    // -----------------------------------------------------------------
    static const FXObjectRefSchemaOp kExternalOps[2] = {
        // Object opcode: single ref at offset sizeof(XObject).
        {
            /*Op=*/         EXObjectRefSchemaOp::Object,
            /*_padOp=*/     0,
            /*ArrayDim=*/   1,
            /*Offset=*/     static_cast<::std::int32_t>(sizeof(XObject)),
            /*StrideBytes=*/0,
            /*_padAlign=*/  0,
            /*NestedSchema=*/nullptr,
        },
        // Terminator
        {
            /*Op=*/         EXObjectRefSchemaOp::Terminator,
            /*_padOp=*/     0,
            /*ArrayDim=*/   0,
            /*Offset=*/     0,
            /*StrideBytes=*/0,
            /*_padAlign=*/  0,
            /*NestedSchema=*/nullptr,
        },
    };
    static const FXObjectRefSchema kExternalSchema = {
        /*NumOps=*/   2u,
        /*Version=*/  kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/      &kExternalOps[0],
        /*_padTail=*/ 0,
    };

    FClass ExternalClass(FName("EscapeTestExternal"), nullptr);
    ExternalClass.PropertiesSize = sizeof(ExternalInstance);
    ExternalClass.MinAlignment   = alignof(ExternalInstance);
    ExternalClass.RefSchema      = &kExternalSchema;
    Allocator.RegisterClassPool(&ExternalClass);

    // -----------------------------------------------------------------
    // Allocate 100 instances of TestClass + 1 ExternalInstance.
    // -----------------------------------------------------------------
    constexpr int kInstanceCount = 100;

    struct FInstanceRecord
    {
        XObject*  Obj;
        ::int32   InternalIndex;
        ::uint32  SerialNumber;
    };
    std::vector<FInstanceRecord> Records;
    Records.reserve(kInstanceCount);

    for (int i = 0; i < kInstanceCount; ++i)
    {
        void* Cell = Allocator.AllocateRaw(
            sizeof(XObject), alignof(XObject), &TestClass);
        Check(Cell != nullptr, "Setup: TestClass AllocateRaw nullptr");

        ::uint32 Serial = 0;
        const ::int32 Index = Array.ReserveSlot(&Serial);

        XObject* Obj = ::new (Cell) XObject();
        Obj->ClassPrivate  = &TestClass;
        Obj->InternalIndex = Index;
        Obj->SerialNumber  = Serial;

        Array.BindObject(Index, Obj);
        Records.push_back(FInstanceRecord{Obj, Index, Serial});
    }

    // Allocate the external instance.
    void* ExtCell = Allocator.AllocateRaw(
        sizeof(ExternalInstance),
        alignof(ExternalInstance),
        &ExternalClass);
    Check(ExtCell != nullptr, "Setup: ExternalClass AllocateRaw nullptr");

    ::uint32 ExtSerial = 0;
    const ::int32 ExtIndex = Array.ReserveSlot(&ExtSerial);

    ExternalInstance* Ext = ::new (ExtCell) ExternalInstance();
    Ext->Header.ClassPrivate  = &ExternalClass;
    Ext->Header.InternalIndex = ExtIndex;
    Ext->Header.SerialNumber  = ExtSerial;

    Array.BindObject(ExtIndex, &Ext->Header);

    // -----------------------------------------------------------------
    // Plant an escape reference: point Ext->RefSlot at the FIRST
    // TestClass instance. This is the "1 escape reference from
    // outside" condition X11 names.
    // -----------------------------------------------------------------
    Ext->RefSlot = Records[0].Obj;

    // -----------------------------------------------------------------
    // Attempt to release the TestClass sub-pool. Expect Err with
    // ReferenceFromOutsideSubpool kind.
    // -----------------------------------------------------------------
    {
        const auto Result = Allocator.ReleaseClassPool(&TestClass);
        Check(!Result.has_value(),
              "ReleaseClassPool succeeded despite a planted escape ref");
        if (!Result.has_value())
        {
            Check(Result.error() ==
                      FScenarioBoundaryError::ReferenceFromOutsideSubpool,
                  "ReleaseClassPool error kind is not "
                  "ReferenceFromOutsideSubpool");
        }
    }

    // -----------------------------------------------------------------
    // Verify the sub-pool is INTACT (the veto preserved the cells).
    // Every TestClass record's array entry still resolves to its
    // original Obj at its original SerialNumber.
    // -----------------------------------------------------------------
    int IntactCount = 0;
    for (const auto& R : Records)
    {
        XObject* Looked =
            Array.GetObjectAtIndex(R.InternalIndex, R.SerialNumber);
        if (Looked == R.Obj)
        {
            ++IntactCount;
        }
    }
    Check(IntactCount == kInstanceCount,
          "Escape-detected veto did not preserve the sub-pool "
          "(some entries lost their original SerialNumber binding)");

    // -----------------------------------------------------------------
    // Clear the escape reference; ReleaseClassPool should now succeed.
    // This verifies the escape detection is correctly stateful: it
    // depends ONLY on the planted ref, not on residual state.
    // -----------------------------------------------------------------
    Ext->RefSlot = nullptr;

    {
        const auto Result = Allocator.ReleaseClassPool(&TestClass);
        Check(Result.has_value(),
              "ReleaseClassPool failed after clearing the escape ref");
    }

    // Verify SerialNumbers bumped now (post-success).
    int BumpedCount = 0;
    for (const auto& R : Records)
    {
        XObject* Stale =
            Array.GetObjectAtIndex(R.InternalIndex, R.SerialNumber);
        if (Stale == nullptr)
        {
            ++BumpedCount;
        }
    }
    Check(BumpedCount == kInstanceCount,
          "Post-clear ReleaseClassPool: SerialNumber bumps incomplete");

    // Teardown: release the external instance.
    Array.ReleaseSlot(Ext->Header.InternalIndex);

    Allocator.__ResetForTests();
    Array.__ResetForTests();
    Collector.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.ReleaseClassPool_EscapeDetected: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.ReleaseClassPool_EscapeDetected: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
