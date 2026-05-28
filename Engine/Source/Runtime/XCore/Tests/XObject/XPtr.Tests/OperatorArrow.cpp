// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XPtr.Tests/OperatorArrow.cpp -- operator-> and operator*
// (XCoreXObject Rev 4 §6.1).
// =====================================================================
//
// Spec §6.1: operator-> + operator* are the ergonomic deref surface.
// Dev XPACT_CHECK fires on nullptr deref; in Shipping the deref is
// raw. The test confirms the deref returns the pointee.
//
// =====================================================================

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

    XObject Obj;
    Obj.ClassPrivate  = s_FakeClassPtr;
    Obj.InternalIndex = 99;
    Obj.SerialNumber  = 0u;  // explicit set to avoid sim-path-guard issues on read

    XPtr<XObject> P(&Obj);

    // operator->: reads InternalIndex via the accessor.
    {
        const ::int32 ReadIdx = P->GetInternalIndex();
        Check(ReadIdx == 99, "operator->: GetInternalIndex() != 99");
    }

    // operator*: returns reference to pointee.
    {
        XObject& Ref = *P;
        Check(&Ref == &Obj, "operator*: address mismatch");
    }

    // GetClass via operator->.
    {
        Check(P->GetClass() == s_FakeClassPtr,
              "operator->: GetClass() did not return fake ptr");
    }

    if (FailureCount == 0)
    {
        std::cout << "XPtr.OperatorArrow: PASS\n";
        return 0;
    }
    std::cerr << "XPtr.OperatorArrow: " << FailureCount << " FAIL(s)\n";
    return 1;
}
