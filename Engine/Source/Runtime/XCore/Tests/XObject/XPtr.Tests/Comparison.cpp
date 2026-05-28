// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XPtr.Tests/Comparison.cpp -- operator== / operator!= surface
// (XCoreXObject Rev 4 §6.1).
// =====================================================================
//
// XPtr equality compares the stored Ptr (pointer-equality). The
// comparison surface includes XPtr-vs-XPtr, XPtr-vs-T*, T*-vs-XPtr,
// XPtr-vs-nullptr, and nullptr-vs-XPtr.
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

    XObject A; A.ClassPrivate = s_FakeClassPtr;
    XObject B; B.ClassPrivate = s_FakeClassPtr;

    XPtr<XObject> PA(&A);
    XPtr<XObject> PA2(&A);
    XPtr<XObject> PB(&B);
    XPtr<XObject> PN;

    // XPtr-vs-XPtr.
    Check(PA == PA2,   "PA == PA2 false");
    Check(PA != PB,    "PA != PB false");
    Check(!(PA == PB), "PA == PB true");

    // XPtr-vs-T*.
    Check(PA == &A,    "PA == &A false");
    Check(PA != &B,    "PA != &B false");

    // T*-vs-XPtr (reverse comparison).
    Check(&A == PA,    "&A == PA false");
    Check(&B != PA,    "&B != PA false");

    // XPtr-vs-nullptr.
    Check(PN == nullptr,   "PN == nullptr false");
    Check(PA != nullptr,   "PA != nullptr false");
    Check(!(PA == nullptr), "PA == nullptr true");

    // nullptr-vs-XPtr.
    Check(nullptr == PN,   "nullptr == PN false");
    Check(nullptr != PA,   "nullptr != PA false");

    if (FailureCount == 0)
    {
        std::cout << "XPtr.Comparison: PASS\n";
        return 0;
    }
    std::cerr << "XPtr.Comparison: " << FailureCount << " FAIL(s)\n";
    return 1;
}
