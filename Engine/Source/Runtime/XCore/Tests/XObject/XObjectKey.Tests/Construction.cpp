// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectKey.Tests/Construction.cpp -- ctor invariants
// (XCoreXObject Rev 4 §6.4).
// =====================================================================
//
// XObjectKey ships THREE construction paths:
//   1. Default ctor -> null sentinel ({0, 0}).
//   2. nullptr_t ctor -> null sentinel.
//   3. From-XObject* ctor -> captures {InternalIndex, SerialNumber}.
//
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XObjectKey.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XObjectKey;

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
    // Default ctor: null sentinel.
    // -----------------------------------------------------------------
    {
        XObjectKey K;
        Check(K.InternalIndex == 0,    "default ctor: InternalIndex != 0");
        Check(K.SerialNumber  == 0u,   "default ctor: SerialNumber  != 0");
        Check(K.IsNull(),              "default ctor: !IsNull()");
        Check(K == nullptr,            "default ctor: K != nullptr");
        Check(!(K != nullptr),         "default ctor: K != nullptr produced true (should be false)");
    }

    // -----------------------------------------------------------------
    // nullptr_t ctor: null sentinel.
    // -----------------------------------------------------------------
    {
        XObjectKey K(nullptr);
        Check(K.InternalIndex == 0,    "nullptr ctor: InternalIndex != 0");
        Check(K.SerialNumber  == 0u,   "nullptr ctor: SerialNumber  != 0");
        Check(K.IsNull(),              "nullptr ctor: !IsNull()");
    }

    // -----------------------------------------------------------------
    // From-XObject(nullptr): null sentinel symmetry.
    // -----------------------------------------------------------------
    {
        XObjectKey K(static_cast<XObject*>(nullptr));
        Check(K.InternalIndex == 0,    "from-XObject(nullptr): InternalIndex != 0");
        Check(K.SerialNumber  == 0u,   "from-XObject(nullptr): SerialNumber  != 0");
        Check(K.IsNull(),              "from-XObject(nullptr): !IsNull()");
    }

    // -----------------------------------------------------------------
    // From-XObject*: captures {InternalIndex, SerialNumber}.
    //
    // The XObject is a stack-local; its InternalIndex / SerialNumber
    // fields are public per spec §2.2 so we can populate them directly
    // without going through FXObjectArray::ReserveSlot (this test does
    // NOT exercise the array; it tests the capture mechanics only).
    // -----------------------------------------------------------------
    {
        XObject Obj;
        Obj.InternalIndex = 42;
        Obj.SerialNumber  = 7u;

        XObjectKey K(&Obj);
        Check(K.InternalIndex == 42,   "from-XObject: InternalIndex != 42");
        Check(K.SerialNumber  == 7u,   "from-XObject: SerialNumber  != 7");
        Check(!K.IsNull(),             "from-XObject: IsNull() returned true");
        Check(K != nullptr,            "from-XObject: K == nullptr");
    }

    // -----------------------------------------------------------------
    // Copy ctor + copy-assign: bitwise.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        Obj.InternalIndex = 123;
        Obj.SerialNumber  = 456u;

        XObjectKey K(&Obj);
        XObjectKey K2(K);            // copy
        XObjectKey K3;
        K3 = K;                       // assign
        Check(K2.InternalIndex == K.InternalIndex, "copy ctor: InternalIndex mismatch");
        Check(K2.SerialNumber  == K.SerialNumber,  "copy ctor: SerialNumber  mismatch");
        Check(K3.InternalIndex == K.InternalIndex, "copy assign: InternalIndex mismatch");
        Check(K3.SerialNumber  == K.SerialNumber,  "copy assign: SerialNumber  mismatch");
    }

    if (FailureCount == 0)
    {
        std::cout << "XObjectKey.Construction: PASS\n";
        return 0;
    }
    std::cerr << "XObjectKey.Construction: " << FailureCount << " FAIL(s)\n";
    return 1;
}
