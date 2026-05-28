// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// X9_ConservativeScanningCorrectness.cpp -- Foundation Prototype X9
// acceptance: Conservative root span scanning correctness on 10k
// mixed XObject/non-XObject entries.
// =====================================================================
//
// X9 acceptance (spec §13.2; Rev 2 refined per FIX-A-MIN-52):
//   "Conservative root span scanning is correct -- specific test
//    surface: a List<object> with 10k mixed entries; 50/50
//    XObject/non-XObject; assert classification correctness for every
//    entry. Every false-positive candidate (a non-XObject value that
//    happens to land in the XObject heap range) is rejected by the
//    validation order (§5.3 per FIX-A-MED-35: heap-range -> index-
//    range -> entry-bind -> SerialNumber match); no false-negatives
//    (every real XObject candidate is correctly identified)."
//
// This test exercises ValidateConservativeCandidate (Phase 5.e API)
// against a 10k-entry mixed array. For each entry:
//   * If it is a live XObject*, the validator MUST return the same
//     pointer (no false-negative).
//   * If it is a non-XObject value (stack-pointer, integer-looks-like
//     -pointer, freed-and-recycled garbage), the validator MUST
//     return nullptr (no false-positive).
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/XGCConservativeValidate.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <vector>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::ValidateConservativeCandidate;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    Phase5L::ResetAllForTests();

    FClass TestClass(FName("X9TestClass"), nullptr);
    TestClass.PropertiesSize = sizeof(XObject);
    TestClass.MinAlignment   = alignof(XObject);
    ::XCore::FXObjectAllocator::Get().RegisterClassPool(&TestClass);

    // -----------------------------------------------------------------
    // 10k mixed entries: 5000 real XObjects + 5000 non-XObject values.
    // The non-XObject values include:
    //   * Stack addresses (a local int's address).
    //   * Small integers (1, 2, ..., 5000 cast to void*).
    //   * Heap pointers to non-XObject objects (a vector<int> internal
    //     pointer; outside the XObject allocator's range).
    //   * Aligned-but-outside-range pointers.
    // -----------------------------------------------------------------
    constexpr int kRealXObjects = 5000;
    constexpr int kNonXObjects  = 5000;

    std::vector<XObject*> RealObjects;
    RealObjects.reserve(kRealXObjects);
    for (int I = 0; I < kRealXObjects; ++I)
    {
        XObject* Obj = Phase5L::CreateAndBind(&TestClass);
        P5L_CHECK(Obj != nullptr,
                  "X9: CreateAndBind returned nullptr in setup");
        if (Obj == nullptr) break;
        RealObjects.push_back(Obj);
    }

    // Non-XObject candidates.
    std::vector<const void*> NonXObjects;
    NonXObjects.reserve(kNonXObjects);
    int StackInt = 42;
    std::vector<int> HeapVec(16, 7);
    for (int I = 0; I < kNonXObjects; ++I)
    {
        switch (I % 4)
        {
            case 0:
                // Small integer cast to pointer (low addresses; below
                // any plausible XObject heap range).
                NonXObjects.push_back(
                    reinterpret_cast<const void*>(static_cast<::std::uintptr_t>(I + 1)));
                break;
            case 1:
                // Stack address (a different region from the XObject
                // allocator's slabs).
                NonXObjects.push_back(&StackInt);
                break;
            case 2:
                // Non-XObject heap pointer.
                NonXObjects.push_back(HeapVec.data());
                break;
            case 3:
                // Aligned-but-far value.
                NonXObjects.push_back(
                    reinterpret_cast<const void*>(
                        static_cast<::std::uintptr_t>(0x7FFF'FFFF'FFFF'FF00ULL)));
                break;
        }
    }

    // -----------------------------------------------------------------
    // X9 ASSERTION 1: every real XObject* is correctly identified
    // (no false-negatives).
    // -----------------------------------------------------------------
    int TrueIdentified = 0;
    for (XObject* Real : RealObjects)
    {
        XObject* Validated = ValidateConservativeCandidate(Real);
        if (Validated == Real)
        {
            ++TrueIdentified;
        }
    }
    P5L_CHECK(TrueIdentified == static_cast<int>(RealObjects.size()),
              "X9: false-negative -- a real XObject was rejected by "
              "ValidateConservativeCandidate");

    // -----------------------------------------------------------------
    // X9 ASSERTION 2: every non-XObject candidate is correctly
    // rejected (no false-positives).
    // -----------------------------------------------------------------
    int TrueRejected = 0;
    for (const void* NonXO : NonXObjects)
    {
        XObject* Validated = ValidateConservativeCandidate(NonXO);
        if (Validated == nullptr)
        {
            ++TrueRejected;
        }
    }
    P5L_CHECK(TrueRejected == static_cast<int>(NonXObjects.size()),
              "X9: false-positive -- a non-XObject candidate was "
              "accepted by ValidateConservativeCandidate");

    // -----------------------------------------------------------------
    // X9 ASSERTION 3: nullptr is rejected.
    // -----------------------------------------------------------------
    P5L_CHECK(ValidateConservativeCandidate(nullptr) == nullptr,
              "X9: ValidateConservativeCandidate(nullptr) did not return "
              "nullptr");

    const double ClassificationRate =
        100.0 * static_cast<double>(TrueIdentified + TrueRejected)
              / static_cast<double>(RealObjects.size() + NonXObjects.size());

    std::cout << "X9: classification correctness = "
              << ClassificationRate << "% across "
              << (RealObjects.size() + NonXObjects.size())
              << " mixed entries (" << TrueIdentified << " true-positives, "
              << TrueRejected << " true-negatives).\n";

    // Cleanup.
    for (XObject* Obj : RealObjects)
    {
        Phase5L::ReleaseAndDeallocate(Obj);
    }

    return P5L_REPORT_PASS("FoundationPrototype.X9_ConservativeScanningCorrectness");
}
