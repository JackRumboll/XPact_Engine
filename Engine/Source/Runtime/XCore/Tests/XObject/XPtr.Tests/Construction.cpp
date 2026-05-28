// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XPtr.Tests/Construction.cpp -- ctor + assignment invariants
// (XCoreXObject Rev 4 §6.1).
// =====================================================================
//
// XPtr ships THREE construction paths:
//   1. Default ctor -> null pointer.
//   2. nullptr_t ctor -> null pointer.
//   3. From-T* ctor -> stores the pointer (Dev-checks IsValidLowLevel).
//
// To pass the Dev IsValidLowLevel check, the test fixture sets
// XObject.ClassPrivate to a non-null sentinel value (the spec §6.1
// contract is "valid live XObject"; ClassPrivate non-null is the
// load-bearing predicate per spec §2.5 IsValidLowLevel).
//
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XPtr.h"

#include <cstdint>
#include <iostream>

namespace
{
    // A bogus non-null FClass*. We never dereference it; XPtr's ctor
    // only checks ClassPrivate != nullptr via IsValidLowLevel.
    alignas(8) std::uint64_t s_FakeClassStorage = 0xDEADBEEFCAFEu;
    const ::XCore::Reflect::FClass* const s_FakeClassPtr =
        reinterpret_cast<const ::XCore::Reflect::FClass*>(&s_FakeClassStorage);
}

int main()
{
    using ::XCore::XObject;
    using ::XCore::XPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Default ctor: null.
    {
        XPtr<XObject> P;
        Check(P.Get() == nullptr,    "default ctor: Get() != nullptr");
        Check(P.IsNull(),            "default ctor: IsNull() false");
        Check(!P.IsValid(),          "default ctor: IsValid() true");
        Check(!static_cast<bool>(P), "default ctor: operator bool true");
    }

    // nullptr_t ctor.
    {
        XPtr<XObject> P(nullptr);
        Check(P.Get() == nullptr, "nullptr ctor: Get() != nullptr");
        Check(P.IsNull(),         "nullptr ctor: IsNull() false");
    }

    // From-nullptr T*.
    {
        XPtr<XObject> P(static_cast<XObject*>(nullptr));
        Check(P.Get() == nullptr, "from-nullptr T*: Get() != nullptr");
    }

    // From-T* with a valid (passes IsValidLowLevel) XObject.
    {
        XObject Obj;
        Obj.ClassPrivate = s_FakeClassPtr; // satisfy IsValidLowLevel

        XPtr<XObject> P(&Obj);
        Check(P.Get() == &Obj,       "from-T*: Get() != &Obj");
        Check(!P.IsNull(),           "from-T*: IsNull() true");
        Check(P.IsValid(),           "from-T*: IsValid() false");
        Check(static_cast<bool>(P),  "from-T*: operator bool false");
    }

    // Assignment from raw T*.
    {
        XObject Obj;
        Obj.ClassPrivate = s_FakeClassPtr;

        XPtr<XObject> P;
        P = &Obj;
        Check(P.Get() == &Obj, "assign T*: Get() != &Obj");
        P = nullptr;
        Check(P.Get() == nullptr, "assign nullptr_t: Get() != nullptr");
    }

    // Copy ctor + copy-assign: bitwise (trivially-copyable).
    {
        XObject Obj;
        Obj.ClassPrivate = s_FakeClassPtr;

        XPtr<XObject> P1(&Obj);
        XPtr<XObject> P2(P1);
        Check(P2.Get() == P1.Get(), "copy ctor: Get() mismatch");

        XPtr<XObject> P3;
        P3 = P1;
        Check(P3.Get() == P1.Get(), "copy assign: Get() mismatch");
    }

    // Move ctor + move-assign: also bitwise (trivially-copyable; no
    // ownership transfer needed). The source pointer is preserved
    // (XPtr is NOT exclusive-ownership).
    {
        XObject Obj;
        Obj.ClassPrivate = s_FakeClassPtr;

        XPtr<XObject> P1(&Obj);
        XPtr<XObject> P2(std::move(P1));
        Check(P2.Get() == &Obj, "move ctor: target Get() != &Obj");

        XPtr<XObject> P3;
        P3 = std::move(P2);
        Check(P3.Get() == &Obj, "move assign: target Get() != &Obj");
    }

    if (FailureCount == 0)
    {
        std::cout << "XPtr.Construction: PASS\n";
        return 0;
    }
    std::cerr << "XPtr.Construction: " << FailureCount << " FAIL(s)\n";
    return 1;
}
