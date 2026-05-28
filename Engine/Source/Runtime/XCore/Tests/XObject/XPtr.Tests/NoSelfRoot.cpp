// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XPtr.Tests/NoSelfRoot.cpp -- documents the non-self-rooting contract
// (XCoreXObject Rev 4 §6.1).
// =====================================================================
//
// Spec §6.1: XPtr does NOT register as a GC root. Rooting is the
// container's responsibility (XCLASS field -> property scan;
// TArray<XPtr<T>> with XGCRootSpan -> span scan; stack-local XPtr in
// XIL2CPP-emitted stack-map -> stack-map root).
//
// Constructing an XPtr does NOT bump the FXObjectArrayEntry::StateBits
// refcount field. The collector (when it ships at Phase 5.h+) treats
// the XPtr's pointee as reachable ONLY when one of the rooting paths
// above includes it.
//
// PHASE 5.c SCOPE: the collector body does not exist yet. The full
// "verify XPtr does not keep object alive across GC" test ships at
// Phase 5.g+ alongside the collector. The Phase 5.c test verifies the
// observable proxy: the FXObjectArrayEntry::StateBits refcount field
// does NOT change when an XPtr is constructed from a live XObject.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XPtr.h"

#include <cstdint>
#include <iostream>

namespace
{
    alignas(8) std::uint64_t s_FakeClassStorage = 0xDEADBEEFCAFEu;
    const ::XCore::Reflect::FClass* const s_FakeClassPtr =
        reinterpret_cast<const ::XCore::Reflect::FClass*>(&s_FakeClassStorage);
}

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::XPtr;

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

    // Allocate a slot + bind a stack XObject.
    XObject Obj;
    Obj.ClassPrivate = s_FakeClassPtr;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    // Baseline refcount: zero.
    const ::uint32 RefCountBefore = Array.GetRefCount(Idx);
    Check(RefCountBefore == 0u,
          "pre-XPtr: refcount != 0 (expected zero baseline)");

    // Construct XPtr; refcount must NOT change (XPtr is non-self-
    // rooting).
    {
        XPtr<XObject> P(&Obj);
        (void)P;
        const ::uint32 RefCountWithXPtr = Array.GetRefCount(Idx);
        Check(RefCountWithXPtr == 0u,
              "post-XPtr ctor: refcount != 0 (XPtr is self-rooting?)");

        // Copy the XPtr -- still no refcount activity.
        XPtr<XObject> P2(P);
        (void)P2;
        const ::uint32 RefCountAfterCopy = Array.GetRefCount(Idx);
        Check(RefCountAfterCopy == 0u,
              "post-XPtr copy: refcount != 0 (XPtr is self-rooting?)");
    }

    // XPtr destruction also leaves the refcount alone.
    const ::uint32 RefCountAfterDtor = Array.GetRefCount(Idx);
    Check(RefCountAfterDtor == 0u,
          "post-XPtr dtor: refcount != 0 (XPtr is self-rooting?)");

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XPtr.NoSelfRoot: PASS\n"
                  << "  NOTE: full collector-roundtrip verification "
                  << "deferred to Phase 5.g+ alongside FXObjectCollector "
                  << "(per spec §6.1; the Phase 5.c probe verifies the "
                  << "observable proxy via FXObjectArray refcount field).\n";
        return 0;
    }
    std::cerr << "XPtr.NoSelfRoot: " << FailureCount << " FAIL(s)\n";
    return 1;
}
