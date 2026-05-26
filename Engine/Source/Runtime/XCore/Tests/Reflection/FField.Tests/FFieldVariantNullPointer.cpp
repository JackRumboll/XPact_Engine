// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FField.Tests/FFieldVariantNullPointer.cpp -- FFieldVariant null /
// default-construction semantics (XCore-4b §5.1).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 ("FField base" / FFieldVariant null
// semantics).
//
// Verifies:
//
//   1. A default-constructed FFieldVariant has Storage == 0, IsField()
//      true (LSB=0 -> Field semantic), AsField() returns nullptr.
//      IsStruct() returns false.
//   2. An explicitly-null FFieldVariant (FFieldVariant{nullptr}) matches
//      the default-constructed form bytewise.
//   3. A FFieldVariant constructed from a nullptr FField* has the same
//      bytewise representation (Storage == 0) -- the cast(nullptr) ->
//      uint64 path produces 0.
//   4. A FFieldVariant constructed from a nullptr FStruct* has
//      Storage == 0x1 (the LSB tag is set, but the pointer bits are
//      zero). IsStruct() returns true; AsStruct() returns nullptr.
//      This is the "null with kind tag" form -- distinct from the
//      default-constructed null variant.
//   5. Equality: the default-constructed and nullptr-Field-ctor forms
//      compare equal; the nullptr-Struct-ctor form compares UNEQUAL
//      to the other two (because the LSB tag differs).
//
// Test (4) and (5) document a subtle but important property of the
// LSB-tag scheme: a "null variant" is not a single bit pattern; it's
// kind-tagged like any other. This matches UE's discipline (UE's
// `FFieldVariant(nullptr UObject)` produces a tagged-as-UObject zero
// pointer; UE's `FFieldVariant(nullptr FField)` produces a Field-tagged
// zero pointer; these are distinct values).
//
// Test cases (1)-(3) cover the "user did not specify a kind" path; case
// (4) covers the "user explicitly passed an FStruct* that happens to be
// null" path. Production code that mixes the two patterns must be
// aware of the semantic distinction.
//
// =====================================================================

#include "Reflection/FField.h"
#include "Reflection/FFieldVariant.h"

#include <cstdint>
#include <iostream>

namespace XCore::Reflect
{
    struct FStruct;
}

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
    using ::XCore::Reflect::FField;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FStruct;

    // -----------------------------------------------------------------
    // (1) Default-constructed FFieldVariant.
    // -----------------------------------------------------------------
    {
        FFieldVariant V;

        Check(V.GetRaw()   == 0,    "Default-ctor FFieldVariant Storage != 0");
        Check(V.IsNull(),           "Default-ctor FFieldVariant IsNull() returned false");
        Check(V.IsField(),          "Default-ctor FFieldVariant IsField() returned false");
        Check(!V.IsStruct(),        "Default-ctor FFieldVariant IsStruct() returned true");
        Check(V.AsField() == nullptr,
              "Default-ctor FFieldVariant AsField() did not return nullptr");
        Check(V.AsStruct() == nullptr,
              "Default-ctor FFieldVariant AsStruct() did not return nullptr");
    }

    // -----------------------------------------------------------------
    // (2) Explicitly-null FFieldVariant.
    // -----------------------------------------------------------------
    {
        FFieldVariant V{nullptr};

        Check(V.GetRaw()   == 0,    "nullptr-ctor FFieldVariant Storage != 0");
        Check(V.IsNull(),           "nullptr-ctor FFieldVariant IsNull() returned false");
        Check(V.IsField(),          "nullptr-ctor FFieldVariant IsField() returned false");
        Check(!V.IsStruct(),        "nullptr-ctor FFieldVariant IsStruct() returned true");
        Check(V.AsField() == nullptr,
              "nullptr-ctor FFieldVariant AsField() did not return nullptr");
        Check(V.AsStruct() == nullptr,
              "nullptr-ctor FFieldVariant AsStruct() did not return nullptr");
    }

    // -----------------------------------------------------------------
    // (3) Nullptr FField* ctor.
    //
    // Explicit pointer-typed nullptr -> Field-tagged null variant.
    // Storage is 0 (LSB tag clear; pointer bits clear).
    // -----------------------------------------------------------------
    {
        const FField* NullField = nullptr;
        FFieldVariant V{NullField};

        Check(V.GetRaw()   == 0,    "nullptr-FField-ctor FFieldVariant Storage != 0");
        Check(V.IsNull(),           "nullptr-FField-ctor FFieldVariant IsNull() returned false");
        Check(V.IsField(),          "nullptr-FField-ctor FFieldVariant IsField() returned false");
        Check(!V.IsStruct(),        "nullptr-FField-ctor FFieldVariant IsStruct() returned true");
        Check(V.AsField() == nullptr,
              "nullptr-FField-ctor FFieldVariant AsField() did not return nullptr");
        Check(V.AsStruct() == nullptr,
              "nullptr-FField-ctor FFieldVariant AsStruct() did not return nullptr");
    }

    // -----------------------------------------------------------------
    // (4) Nullptr FStruct* ctor.
    //
    // Explicit FStruct-typed nullptr -> Struct-tagged null variant.
    // Storage is 0x1 (LSB tag set; pointer bits clear).
    //
    // This is structurally distinct from the default-constructed form
    // (Storage == 0). IsStruct() returns true; AsStruct() returns
    // nullptr because the pointer-bits-after-mask are zero.
    // -----------------------------------------------------------------
    {
        const FStruct* NullStruct = nullptr;
        FFieldVariant V{NullStruct};

        Check(V.GetRaw()   == FFieldVariant::kFStructTag,
              "nullptr-FStruct-ctor FFieldVariant Storage != kFStructTag (0x1)");
        Check(!V.IsNull(),
              "nullptr-FStruct-ctor FFieldVariant IsNull() returned true "
              "(Storage is 0x1, not 0)");
        Check(!V.IsField(),
              "nullptr-FStruct-ctor FFieldVariant IsField() returned true");
        Check(V.IsStruct(),
              "nullptr-FStruct-ctor FFieldVariant IsStruct() returned false");
        Check(V.AsField() == nullptr,
              "nullptr-FStruct-ctor FFieldVariant AsField() did not return nullptr");
        Check(V.AsStruct() == nullptr,
              "nullptr-FStruct-ctor FFieldVariant AsStruct() did not return nullptr");
    }

    // -----------------------------------------------------------------
    // (5) Equality across null forms.
    //
    //   default-ctor == nullptr-ctor == nullptr-FField-ctor
    //   default-ctor != nullptr-FStruct-ctor
    //
    // The "kind tag is part of identity" property of the LSB-tag scheme
    // is visible here.
    // -----------------------------------------------------------------
    {
        const FField*  NullField  = nullptr;
        const FStruct* NullStruct = nullptr;

        FFieldVariant Default;
        FFieldVariant Nullptr_{nullptr};
        FFieldVariant NullFieldVariant{NullField};
        FFieldVariant NullStructVariant{NullStruct};

        Check(Default == Nullptr_,
              "Default-ctor != nullptr-ctor (both should be null Field variants)");
        Check(Default == NullFieldVariant,
              "Default-ctor != nullptr-FField-ctor");
        Check(Nullptr_ == NullFieldVariant,
              "nullptr-ctor != nullptr-FField-ctor");

        Check(Default != NullStructVariant,
              "Default-ctor and nullptr-FStruct-ctor compared equal (LSB tag differs)");
        Check(NullFieldVariant != NullStructVariant,
              "nullptr-FField-ctor and nullptr-FStruct-ctor compared equal (LSB tag differs)");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FField.FFieldVariantNullPointer: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FField.FFieldVariantNullPointer: PASS\n";
    return 0;
}
