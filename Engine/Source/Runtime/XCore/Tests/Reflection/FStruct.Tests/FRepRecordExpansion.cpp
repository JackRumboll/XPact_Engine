// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FRepRecordExpansion.cpp -- XPROPERTY(Replicated)
// ArrayDim>1 produces one FRepRecord per element (XCore-4b §6.5 +
// FIX-7).
// =====================================================================
//
// Builds an FClass containing a replicated property with ArrayDim = 4,
// calls FClass::Link, and verifies:
//
//   1. ClassReps has 4 entries (one per element).
//   2. Each entry references the same FProperty but a distinct Index
//      (0, 1, 2, 3).
//   3. FirstOwnedClassRep + ClassRepCount reflect the class's
//      contribution.
//   4. Non-replicated properties don't appear in ClassReps.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EPropertyFlags.h"
#include "Reflection/FClass.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"
#include "Reflection/FRepRecord.h"
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
    // Build an FClass with two properties:
    //
    //   Stamina  (FFloatProperty, ArrayDim=4, CPF_Net)   - replicated
    //   HitPoints (FIntProperty,   ArrayDim=1, CPF_None)  - NOT replicated
    //
    // Per FIX-7, Stamina contributes 4 FRepRecord entries; HitPoints
    // contributes none.
    // -----------------------------------------------------------------
    FClass Cls(FName("ATestActor"), nullptr);

    FFloatProperty Stamina(FFieldVariant{}, FName("Stamina"));
    Stamina.ArrayDim       = 4;
    Stamina.PropertyFlags  = EPropertyFlags::CPF_Net;

    FIntProperty HitPoints(FFieldVariant{}, FName("HitPoints"));
    HitPoints.ArrayDim     = 1;
    HitPoints.PropertyFlags = EPropertyFlags::CPF_None;

    // Thread ChildProperties: head=Stamina -> HitPoints -> nullptr.
    Cls.ChildProperties = &Stamina;
    Stamina.SetNext(&HitPoints);
    HitPoints.SetNext(nullptr);

    // Pre-Link: ClassReps empty.
    Check(Cls.GetClassReps().Num() == 0, "ClassReps not empty pre-Link");
    Check(Cls.GetClassRepCount() == 0,   "ClassRepCount != 0 pre-Link");

    // -----------------------------------------------------------------
    // Link.
    // -----------------------------------------------------------------
    Cls.Link();

    // -----------------------------------------------------------------
    // ClassReps: must contain exactly 4 entries (Stamina[0..3]).
    // -----------------------------------------------------------------
    const auto& Reps = Cls.GetClassReps();
    Check(Reps.Num() == 4, "ClassReps count != 4 (should be Stamina x 4)");
    Check(Cls.GetClassRepCount() == 4, "ClassRepCount != 4");

    if (Reps.Num() == 4)
    {
        // Each entry points at Stamina; Index = 0..3.
        Check(Reps[0].Property == &Stamina, "ClassReps[0].Property != &Stamina");
        Check(Reps[0].Index    == 0,        "ClassReps[0].Index != 0");
        Check(Reps[1].Property == &Stamina, "ClassReps[1].Property != &Stamina");
        Check(Reps[1].Index    == 1,        "ClassReps[1].Index != 1");
        Check(Reps[2].Property == &Stamina, "ClassReps[2].Property != &Stamina");
        Check(Reps[2].Index    == 2,        "ClassReps[2].Index != 2");
        Check(Reps[3].Property == &Stamina, "ClassReps[3].Property != &Stamina");
        Check(Reps[3].Index    == 3,        "ClassReps[3].Index != 3");
    }

    // -----------------------------------------------------------------
    // FirstOwnedClassRep + ClassRepCount.
    // -----------------------------------------------------------------
    Check(Cls.GetFirstOwnedClassRep() == 0,
          "FirstOwnedClassRep != 0 (no SuperStruct contribution)");
    Check(Cls.GetClassRepCount() == 4,
          "ClassRepCount != 4");

    // -----------------------------------------------------------------
    // Idempotency: Link again -> same result.
    // -----------------------------------------------------------------
    Cls.Link();
    Check(Cls.GetClassReps().Num() == 4, "ClassReps count != 4 after second Link");

    // -----------------------------------------------------------------
    // Add a second replicated property (non-array) and verify the
    // total is 4 + 1 = 5 entries.
    // -----------------------------------------------------------------
    FIntProperty Score(FFieldVariant{}, FName("Score"));
    Score.ArrayDim       = 1;
    Score.PropertyFlags  = EPropertyFlags::CPF_Net;

    HitPoints.SetNext(&Score);
    Score.SetNext(nullptr);

    Cls.Link();
    Check(Cls.GetClassReps().Num() == 5,
          "ClassReps count != 5 after adding Score (Stamina[0..3] + Score)");
    Check(Cls.GetClassRepCount() == 5, "ClassRepCount != 5");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FRepRecordExpansion: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FRepRecordExpansion: PASS\n";
    return 0;
}
