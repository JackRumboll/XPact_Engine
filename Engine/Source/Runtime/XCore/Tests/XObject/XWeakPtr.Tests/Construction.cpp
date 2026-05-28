// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XWeakPtr.Tests/Construction.cpp -- ctor invariants
// (XCoreXObject Rev 4 §6.2).
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XWeakPtr.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XWeakPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Default ctor: null sentinel.
    {
        XWeakPtr<XObject> W;
        Check(W.InternalIndex == 0,    "default: InternalIndex != 0");
        Check(W.SerialNumber  == 0u,   "default: SerialNumber  != 0");
        Check(W.IsExplicitlyNull(),    "default: IsExplicitlyNull() false");
        Check(W.IsNull(),              "default: IsNull() false");
        Check(W == nullptr,            "default: W != nullptr");
    }

    // nullptr_t ctor.
    {
        XWeakPtr<XObject> W(nullptr);
        Check(W.IsNull(), "nullptr ctor: IsNull() false");
    }

    // From-T*: captures {InternalIndex, SerialNumber} directly.
    {
        XObject Obj;
        Obj.InternalIndex = 13;
        Obj.SerialNumber  = 27u;

        XWeakPtr<XObject> W(&Obj);
        Check(W.InternalIndex == 13,  "from-T*: InternalIndex != 13");
        Check(W.SerialNumber  == 27u, "from-T*: SerialNumber  != 27");
        Check(!W.IsNull(),            "from-T*: IsNull() true");
    }

    // From nullptr T*.
    {
        XWeakPtr<XObject> W(static_cast<XObject*>(nullptr));
        Check(W.IsNull(), "from-nullptr T*: IsNull() false");
    }

    // From raw {int32, uint32}.
    {
        XWeakPtr<XObject> W(42, 100u);
        Check(W.InternalIndex == 42,    "raw ctor: InternalIndex != 42");
        Check(W.SerialNumber  == 100u,  "raw ctor: SerialNumber  != 100");
    }

    // Copy ctor + assignment.
    {
        XObject Obj; Obj.InternalIndex = 1; Obj.SerialNumber = 2u;
        XWeakPtr<XObject> W1(&Obj);
        XWeakPtr<XObject> W2(W1);
        Check(W1 == W2, "copy ctor: equality fail");
        XWeakPtr<XObject> W3;
        W3 = W1;
        Check(W1 == W3, "copy assign: equality fail");
    }

    // T* assignment.
    {
        XObject Obj; Obj.InternalIndex = 99; Obj.SerialNumber = 88u;
        XWeakPtr<XObject> W;
        W = &Obj;
        Check(W.InternalIndex == 99, "T* assign: InternalIndex != 99");
        Check(W.SerialNumber  == 88u, "T* assign: SerialNumber  != 88");

        W = nullptr;
        Check(W.IsNull(), "nullptr assign: IsNull() false");
    }

    if (FailureCount == 0)
    {
        std::cout << "XWeakPtr.Construction: PASS\n";
        return 0;
    }
    std::cerr << "XWeakPtr.Construction: " << FailureCount << " FAIL(s)\n";
    return 1;
}
