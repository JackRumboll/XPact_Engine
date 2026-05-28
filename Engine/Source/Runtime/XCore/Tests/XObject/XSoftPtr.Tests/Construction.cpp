// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XSoftPtr.Tests/Construction.cpp -- ctor invariants
// (XCoreXObject Rev 4 §6.3).
// =====================================================================

#include "XObject/XObject.h"          // brings in XPactMacros.h transitively
#include "XObject/XSoftPtr.h"
#include "Reflection/FSoftObjectPath.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XSoftPtr;
    using ::XCore::Reflect::FSoftObjectPath;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Default ctor: null path + null cache.
    {
        XSoftPtr<XObject> S;
        Check(S.GetPath().IsNull(),     "default: Path not null");
        Check(S.IsNull(),               "default: IsNull() false");
    }

    // nullptr_t ctor.
    {
        XSoftPtr<XObject> S(nullptr);
        Check(S.IsNull(), "nullptr: IsNull() false");
    }

    // From FSoftObjectPath ctor.
    {
        FSoftObjectPath Path;
        Path.Storage = 0xCAFEBABEu;

        XSoftPtr<XObject> S(Path);
        Check(S.GetPath() == Path, "from-Path: GetPath() != Path");
        Check(!S.GetPath().IsNull(), "from-Path: GetPath().IsNull() true");

        // CachedRef remains null until first Get().
        Check(S.CachedRef.IsNull(), "from-Path: CachedRef pre-populated");
    }

    // From T* ctor: CachedRef captures the weak handle; Path remains
    // null at Phase 5.c (the path-from-XObject materialisation lands
    // at Layer 9).
    {
        XObject Obj;
        Obj.InternalIndex = 7;
        Obj.SerialNumber  = 14u;

        XSoftPtr<XObject> S(&Obj);
        Check(S.GetPath().IsNull(),
              "from-T*: Path not null (Phase 5.c: Path-from-XObject "
              "materialisation deferred to Layer 9)");
        Check(S.CachedRef.InternalIndex == 7,
              "from-T*: CachedRef.InternalIndex != 7");
        Check(S.CachedRef.SerialNumber  == 14u,
              "from-T*: CachedRef.SerialNumber  != 14");
        // IsNull is `Path.IsNull() && CachedRef.IsNull()`; with
        // CachedRef populated, IsNull must be false.
        Check(!S.IsNull(),
              "from-T*: IsNull() true (CachedRef populated)");
    }

    if (FailureCount == 0)
    {
        std::cout << "XSoftPtr.Construction: PASS\n";
        return 0;
    }
    std::cerr << "XSoftPtr.Construction: " << FailureCount << " FAIL(s)\n";
    return 1;
}
