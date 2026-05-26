// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/EPropertyFlagsBitwise.cpp -- EPropertyFlags bitwise
// operator surface (XCore-4b §5.3 + §5.8).
// =====================================================================

#include "Reflection/EPropertyFlags.h"

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
    using ::XCore::Reflect::EPropertyFlags;
    using ::XCore::Reflect::HasAllPropertyFlags;
    using ::XCore::Reflect::HasAnyPropertyFlags;

    // Bit-position locks.
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_Edit)              == (1ULL << 0),
          "CPF_Edit @ != bit 0");
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_Net)               == (1ULL << 4),
          "CPF_Net @ != bit 4");
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_RepNotify)         == (1ULL << 5),
          "CPF_RepNotify @ != bit 5");
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_PushModel)         == (1ULL << 56),
          "CPF_PushModel @ != bit 56");
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_DeltaCompressed)   == (1ULL << 57),
          "CPF_DeltaCompressed @ != bit 57");
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_NullableReferenceType) == (1ULL << 60),
          "CPF_NullableReferenceType @ != bit 60");
    Check(static_cast<std::uint64_t>(EPropertyFlags::CPF_RequiredInit)      == (1ULL << 61),
          "CPF_RequiredInit @ != bit 61");

    // OR composition.
    auto Combined = EPropertyFlags::CPF_Edit | EPropertyFlags::CPF_Net | EPropertyFlags::CPF_RepNotify;
    Check(static_cast<std::uint64_t>(Combined) == ((1ULL << 0) | (1ULL << 4) | (1ULL << 5)),
          "OR composition failed");

    // HasAnyPropertyFlags.
    Check(HasAnyPropertyFlags(Combined, EPropertyFlags::CPF_Edit),
          "HasAny(CPF_Edit) returned false");
    Check(HasAnyPropertyFlags(Combined, EPropertyFlags::CPF_Net),
          "HasAny(CPF_Net) returned false");
    Check(!HasAnyPropertyFlags(Combined, EPropertyFlags::CPF_Transient),
          "HasAny(CPF_Transient) returned true (not in set)");
    Check(HasAnyPropertyFlags(Combined,
            EPropertyFlags::CPF_Transient | EPropertyFlags::CPF_Edit),
          "HasAny(CPF_Transient | CPF_Edit) returned false (Edit IS in set)");

    // HasAllPropertyFlags.
    Check(HasAllPropertyFlags(Combined, EPropertyFlags::CPF_Edit),
          "HasAll(CPF_Edit) returned false");
    Check(HasAllPropertyFlags(Combined, EPropertyFlags::CPF_Edit | EPropertyFlags::CPF_Net),
          "HasAll(CPF_Edit|CPF_Net) returned false");
    Check(!HasAllPropertyFlags(Combined, EPropertyFlags::CPF_Edit | EPropertyFlags::CPF_Transient),
          "HasAll(CPF_Edit|CPF_Transient) returned true (Transient NOT in set)");

    // AND composition.
    auto AndMask = Combined & EPropertyFlags::CPF_Edit;
    Check(AndMask == EPropertyFlags::CPF_Edit, "AND with single bit failed");

    // XOR composition.
    auto XorMask = (EPropertyFlags::CPF_Edit | EPropertyFlags::CPF_Net)
                 ^ EPropertyFlags::CPF_Edit;
    Check(XorMask == EPropertyFlags::CPF_Net, "XOR composition failed");

    // NOT composition.
    auto NotMask = ~EPropertyFlags::CPF_None;
    Check(static_cast<std::uint64_t>(NotMask) == 0xFFFFFFFFFFFFFFFFULL,
          "NOT of CPF_None must be all ones");

    // Compound assignment.
    EPropertyFlags Acc = EPropertyFlags::CPF_None;
    Acc |= EPropertyFlags::CPF_Edit;
    Acc |= EPropertyFlags::CPF_Net;
    Check(Acc == (EPropertyFlags::CPF_Edit | EPropertyFlags::CPF_Net),
          "|= compound assignment failed");
    Acc &= EPropertyFlags::CPF_Edit;
    Check(Acc == EPropertyFlags::CPF_Edit, "&= compound assignment failed");

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.EPropertyFlagsBitwise: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.EPropertyFlagsBitwise: PASS\n";
    return 0;
}
