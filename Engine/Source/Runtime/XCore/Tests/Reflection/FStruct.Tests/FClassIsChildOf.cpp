// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FClassIsChildOf.cpp -- FClass::IsChildOf via CastFlags
// fast path + SuperStruct chain walk (XCore-4b §7.3 + §13 gate C1).
// =====================================================================
//
// Constructs a 3-level FClass hierarchy with synthetic CastFlags and
// verifies:
//
//   1. Self-IsChildOf returns true (a class IS a child of itself).
//   2. Direct parent IsChildOf returns true.
//   3. Grandparent IsChildOf returns true (chain walk).
//   4. Sibling IsChildOf returns false.
//   5. nullptr IsChildOf returns false.
//   6. FStruct::IsChildOf base overload also resolves correctly.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
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
    // Build the synthetic hierarchy:
    //
    //   FClass_Root (no cast bit; SuperStruct = nullptr)
    //     - kFProperty bit reused as a synthetic placeholder for testing
    //   FClass_Mid (cast bit kFObjectProperty; SuperStruct = &Root)
    //   FClass_Leaf (cast bits kFObjectProperty + kFInt8Property;
    //                SuperStruct = &Mid)
    //
    // (We use existing EClassCastFlags bit positions as stand-ins for
    // class-hierarchy cast flags because the spec reserves bits 37-63
    // for future FField subclasses but Phase 4b.5 doesn't yet assign
    // them. The test verifies the AND mechanism works regardless of
    // bit choice.)
    // -----------------------------------------------------------------

    FClass Root(FName("Root"), nullptr,
                EClassFlags::CLASS_None,
                EClassCastFlags::kNone);

    FClass Mid(FName("Mid"), &Root,
               EClassFlags::CLASS_None,
               EClassCastFlags::kFObjectProperty);

    FClass Leaf(FName("Leaf"), &Mid,
                EClassFlags::CLASS_None,
                EClassCastFlags::kFObjectProperty | EClassCastFlags::kFInt8Property);

    FClass Sibling(FName("Sibling"), &Root,
                   EClassFlags::CLASS_None,
                   EClassCastFlags::kFFloatProperty);

    // -----------------------------------------------------------------
    // Self-IsChildOf.
    // -----------------------------------------------------------------
    Check(Leaf.IsChildOf(&Leaf), "Leaf NOT a child of itself");
    Check(Mid.IsChildOf(&Mid),   "Mid NOT a child of itself");
    Check(Root.IsChildOf(&Root), "Root NOT a child of itself");

    // -----------------------------------------------------------------
    // Direct parent.
    // -----------------------------------------------------------------
    Check(Leaf.IsChildOf(&Mid), "Leaf NOT a child of Mid (direct parent)");
    Check(Mid.IsChildOf(&Root), "Mid NOT a child of Root (direct parent)");

    // -----------------------------------------------------------------
    // Grandparent (chain walk).
    // -----------------------------------------------------------------
    Check(Leaf.IsChildOf(&Root), "Leaf NOT a child of Root (grandparent)");

    // -----------------------------------------------------------------
    // Negative: sibling.
    // -----------------------------------------------------------------
    Check(!Leaf.IsChildOf(&Sibling),    "Leaf erroneously IsChildOf Sibling");
    Check(!Sibling.IsChildOf(&Leaf),    "Sibling erroneously IsChildOf Leaf");
    Check(!Sibling.IsChildOf(&Mid),     "Sibling erroneously IsChildOf Mid");

    // -----------------------------------------------------------------
    // Reverse direction: parent IsChildOf child is false.
    // -----------------------------------------------------------------
    Check(!Root.IsChildOf(&Leaf), "Root erroneously IsChildOf Leaf");
    Check(!Mid.IsChildOf(&Leaf),  "Mid erroneously IsChildOf Leaf");

    // -----------------------------------------------------------------
    // nullptr.
    // -----------------------------------------------------------------
    Check(!Leaf.IsChildOf(static_cast<const FClass*>(nullptr)),
          "Leaf erroneously IsChildOf nullptr");

    // -----------------------------------------------------------------
    // FStruct::IsChildOf overload (calls the base implementation, which
    // walks SuperStruct without CastFlags fast path).
    // -----------------------------------------------------------------
    Check(Leaf.FStruct::IsChildOf(static_cast<const FStruct*>(&Mid)),
          "Leaf::FStruct::IsChildOf(Mid) failed (chain walk path)");
    Check(Leaf.FStruct::IsChildOf(static_cast<const FStruct*>(&Root)),
          "Leaf::FStruct::IsChildOf(Root) failed");
    Check(!Leaf.FStruct::IsChildOf(static_cast<const FStruct*>(&Sibling)),
          "Leaf::FStruct::IsChildOf(Sibling) erroneously true");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FClassIsChildOf: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FClassIsChildOf: PASS\n";
    return 0;
}
