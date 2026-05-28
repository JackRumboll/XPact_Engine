// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XSoftPtr.Tests/LazyResolve.cpp -- Get() + cache populate
// (XCoreXObject Rev 4 §6.3).
// =====================================================================
//
// Phase 5.c posture: ResolveSyncToXObject_Internal is a stub returning
// nullptr (Layer 9 will ship the asset-registry resolver). Get() in
// the from-T* construction path SHOULD return the pointee via the
// CachedRef fast path; Get() in the from-Path-only path returns
// nullptr until Layer 9 ships.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XSoftPtr.h"
#include "Reflection/FSoftObjectPath.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::XSoftPtr;
    using ::XCore::Reflect::FSoftObjectPath;

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

    // Cache-fast-path: from-T* ctor populates CachedRef; Get()
    // returns the pointee via the cache.
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XSoftPtr<XObject> S(&Obj);
        Check(S.Get() == &Obj, "cache hit: Get() != &Obj");

        Array.FreeEntry(Idx);
    }

    // Path-only (Phase 5.c stub): Get() returns nullptr because the
    // resolver is a stub.
    {
        FSoftObjectPath Path;
        Path.Storage = 0xABCDEFu;

        XSoftPtr<XObject> S(Path);
        Check(S.Get() == nullptr,
              "Path-only: Get() != nullptr (Phase 5.c resolve stub "
              "should produce nullptr)");

        // CachedRef stays empty after a failed resolve.
        Check(S.CachedRef.IsNull(),
              "Path-only: CachedRef populated despite resolve stub "
              "returning nullptr");
    }

    // Default soft-ptr returns nullptr from Get().
    {
        XSoftPtr<XObject> S;
        Check(S.Get() == nullptr, "default: Get() != nullptr");
    }

    // Cache MISS after the underlying object is freed: the cached
    // weak ref's SerialNumber check fails; the stub fall-through
    // returns nullptr.
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XSoftPtr<XObject> S(&Obj);
        Check(S.Get() == &Obj, "pre-free: Get() != &Obj");

        Array.FreeEntry(Idx);

        Check(S.Get() == nullptr,
              "post-free: Get() != nullptr (cache invalid; stub returns "
              "nullptr; net result must be nullptr)");
    }

    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XSoftPtr.LazyResolve: PASS\n"
                  << "  NOTE: full path-resolve semantics deferred to "
                  << "Layer 9 (asset registry).\n";
        return 0;
    }
    std::cerr << "XSoftPtr.LazyResolve: " << FailureCount << " FAIL(s)\n";
    return 1;
}
