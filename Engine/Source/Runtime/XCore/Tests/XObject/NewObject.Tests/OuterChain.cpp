// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/OuterChain.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §3.5: NewObject's Outer linkage walks correctly.
// Constructs A -> B (Outer=A) -> C (Outer=B); verifies the chain.
//
// =====================================================================

#include "XObject/FXObjectArray.h"
#include "XObject/NewObject.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <iostream>

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
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    FClass TestClass(FName("XOuterTest"), nullptr);

    XObject* const A = ::XCore::NewObjectImpl(
        &TestClass, nullptr, FName("A"), EObjectFlags::None, nullptr);
    XObject* const B = ::XCore::NewObjectImpl(
        &TestClass, A, FName("B"), EObjectFlags::None, nullptr);
    XObject* const C = ::XCore::NewObjectImpl(
        &TestClass, B, FName("C"), EObjectFlags::None, nullptr);

    Check(A != nullptr, "A NewObject returned nullptr");
    Check(B != nullptr, "B NewObject returned nullptr");
    Check(C != nullptr, "C NewObject returned nullptr");

    Check(A->GetOuter() == nullptr, "A Outer != nullptr");
    Check(B->GetOuter() == A,       "B Outer != A");
    Check(C->GetOuter() == B,       "C Outer != B");

    // Walk the chain from C back to A.
    XObject* Walker = C;
    int Depth = 0;
    constexpr int kCap = 16;
    while (Walker != nullptr && Depth < kCap)
    {
        ++Depth;
        Walker = Walker->GetOuter();
    }
    Check(Depth == 3, "Outer chain depth != 3 from C");

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.OuterChain: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.OuterChain: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
