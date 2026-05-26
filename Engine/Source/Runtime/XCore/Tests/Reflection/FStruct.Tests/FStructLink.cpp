// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FStructLink.cpp -- FStruct::Link rebuilds PropertyLink
// / DestructorLink chains (XCore-4b §7.1).
// =====================================================================
//
// Constructs a synthetic FStruct with 5 FProperty children threaded
// through the FField::Next chain, calls FStruct::Link, and verifies:
//
//   1. PropertyLink head is non-null.
//   2. Walking PropertyLink visits exactly 5 properties.
//   3. DestructorLink walks the same 5 properties (conservative-all
//      Phase 4b.5 posture per FStruct::Link comment).
//   4. Every property's PropertyLinkNext + DestructorLinkNext chain is
//      consistent (no cycles, terminates at nullptr after 5 hops).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FField.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"
#include "Reflection/FStruct.h"

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
    using namespace ::XCore::Reflect;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Construct a synthetic FStruct + 5 FIntProperty children.
    //
    // Properties are placed on the stack; their Next pointers thread
    // them into the struct's ChildProperties list head-first.
    // -----------------------------------------------------------------
    FStruct Struct(FName("TestStruct"), nullptr);

    FIntProperty P0(FFieldVariant{}, FName("P0"));
    FIntProperty P1(FFieldVariant{}, FName("P1"));
    FIntProperty P2(FFieldVariant{}, FName("P2"));
    FIntProperty P3(FFieldVariant{}, FName("P3"));
    FIntProperty P4(FFieldVariant{}, FName("P4"));

    // Thread: ChildProperties -> P0 -> P1 -> P2 -> P3 -> P4 -> nullptr.
    Struct.ChildProperties = &P0;
    P0.SetNext(&P1);
    P1.SetNext(&P2);
    P2.SetNext(&P3);
    P3.SetNext(&P4);
    P4.SetNext(nullptr);

    // Before Link: chains should be empty.
    Check(Struct.GetPropertyLink()       == nullptr, "PropertyLink not nullptr pre-Link");
    Check(Struct.GetDestructorLink()     == nullptr, "DestructorLink not nullptr pre-Link");
    Check(Struct.GetPostConstructLink()  == nullptr, "PostConstructLink not nullptr pre-Link");
    Check(Struct.GetObjectRefProperties().Num() == 0, "ObjectRefProperties not empty pre-Link");

    // -----------------------------------------------------------------
    // Link.
    // -----------------------------------------------------------------
    Struct.Link();

    // -----------------------------------------------------------------
    // PropertyLink walk: must visit exactly 5 entries.
    // -----------------------------------------------------------------
    int PropertyLinkCount = 0;
    for (FProperty* W = Struct.GetPropertyLink(); W != nullptr; W = W->PropertyLinkNext)
    {
        ++PropertyLinkCount;
        if (PropertyLinkCount > 10) break;  // cycle guard
    }
    Check(PropertyLinkCount == 5, "PropertyLink count != 5");

    // -----------------------------------------------------------------
    // DestructorLink walk: same expectation (Phase 4b.5 conservative-
    // all posture; every property threads in).
    // -----------------------------------------------------------------
    int DestructorLinkCount = 0;
    for (FProperty* W = Struct.GetDestructorLink(); W != nullptr; W = W->DestructorLinkNext)
    {
        ++DestructorLinkCount;
        if (DestructorLinkCount > 10) break;
    }
    Check(DestructorLinkCount == 5, "DestructorLink count != 5");

    // -----------------------------------------------------------------
    // PostConstructLink walk: per Phase 4b.5 comments in FStruct.cpp,
    // PostConstructLink mirrors PropertyLink (every property threads
    // in until CPF_NeedCtorLink lands at Phase 4b.6).
    // -----------------------------------------------------------------
    Check(Struct.GetPostConstructLink() == Struct.GetPropertyLink(),
          "PostConstructLink != PropertyLink (Phase 4b.5 mirror posture)");

    // -----------------------------------------------------------------
    // ObjectRefProperties: FIntProperty does NOT contain object refs
    // (its ContainsObjectReference slot returns false / is null), so
    // ObjectRefProperties should be empty after Link.
    // -----------------------------------------------------------------
    Check(Struct.GetObjectRefProperties().Num() == 0,
          "ObjectRefProperties not empty post-Link (FIntProperty should not be a ref)");

    // -----------------------------------------------------------------
    // Idempotency: Link a second time -> chains stay the same length.
    // -----------------------------------------------------------------
    Struct.Link();
    PropertyLinkCount = 0;
    for (FProperty* W = Struct.GetPropertyLink(); W != nullptr; W = W->PropertyLinkNext)
    {
        ++PropertyLinkCount;
        if (PropertyLinkCount > 10) break;
    }
    Check(PropertyLinkCount == 5, "PropertyLink count != 5 after second Link");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FStructLink: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FStructLink: PASS\n";
    return 0;
}
