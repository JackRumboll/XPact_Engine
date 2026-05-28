// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XPtr.Tests/Resolution.cpp -- Get() returns the captured T*
// (XCoreXObject Rev 4 §6.1).
// =====================================================================
//
// Spec §6.1: XPtr's Get() is a RAW LOAD (no SerialNumber check); the
// pointee is alive by contract because the XPtr held a strong
// reference path through its container. This test confirms Get()
// returns the captured T* verbatim.
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

    XObject ObjA; ObjA.ClassPrivate = s_FakeClassPtr;
    XObject ObjB; ObjB.ClassPrivate = s_FakeClassPtr;

    // Get() returns the captured T*.
    {
        XPtr<XObject> P(&ObjA);
        Check(P.Get() == &ObjA, "Get(): pointer mismatch");
    }

    // Re-assigning the XPtr changes the captured T*.
    {
        XPtr<XObject> P(&ObjA);
        P = &ObjB;
        Check(P.Get() == &ObjB, "post-assign Get(): pointer mismatch");
    }

    // Null after assignment.
    {
        XPtr<XObject> P(&ObjA);
        P = nullptr;
        Check(P.Get() == nullptr, "null-assign Get(): pointer not nullptr");
    }

    // Get() on a default-constructed XPtr returns nullptr.
    {
        XPtr<XObject> P;
        Check(P.Get() == nullptr, "default Get(): pointer not nullptr");
    }

    if (FailureCount == 0)
    {
        std::cout << "XPtr.Resolution: PASS\n";
        return 0;
    }
    std::cerr << "XPtr.Resolution: " << FailureCount << " FAIL(s)\n";
    return 1;
}
