// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FField.Tests/FFieldVariantLSBTag.cpp -- FFieldVariant LSB-tag round-
// trip verification (XCore-4b §5.1; FIX-4).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 ("FField base" / FFieldVariant Rev 2
// reshape per FIX-4 to UE's LSB-tag pattern).
//
// Verifies:
//
//   1. FFieldVariant(const FField*) round-trips: the value returned by
//      `AsField()` equals the original pointer (LSB-clear; field bit
//      pattern is the pointer verbatim).
//   2. FFieldVariant(const FStruct*) round-trips: the value returned by
//      `AsStruct()` equals the original pointer (LSB stripped; field
//      bit pattern is `pointer | 0x1`).
//   3. LSB inversion vs UE: an FStruct-tagged variant has the LSB set
//      to 1 in its Storage (verified by direct Storage inspection),
//      while a Field-tagged variant has the LSB clear.
//   4. Cross-construct comparison: an FFieldVariant(field_ptr) compares
//      bytewise-equal to a second FFieldVariant(field_ptr); compares
//      bytewise-unequal to FFieldVariant(struct_ptr_with_same_address).
//   5. Predicates (`IsField`, `IsStruct`, `IsNull`) return the expected
//      values for each construction path.
//
// FStruct is forward-declared in FFieldVariant.h and not fully defined
// in Phase 4b.3 (the full FStruct lands at Phase 4b.5). For this test,
// we use a stack-local 16-byte buffer aligned to 8 bytes, cast to
// `const FStruct*` -- the LSB-tag scheme only stores the pointer (does
// not dereference), so a forward-declared FStruct* with a synthetic
// address is sufficient.
//
// =====================================================================

#include "Reflection/FField.h"
#include "Reflection/FFieldVariant.h"

#include <cstddef>
#include <cstdint>
#include <iostream>

namespace XCore::Reflect
{
    // Forward declaration is already in FFieldVariant.h; we don't need
    // to redeclare here. The struct type need only be incomplete.
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
    // (1) Field-pointer round-trip.
    //
    // Construct a stack FField; build an FFieldVariant from its
    // address; verify the variant's predicates and accessors recover
    // the original pointer.
    // -----------------------------------------------------------------
    {
        // FField is default-constructible (constexpr ctor); the
        // instance lives on the stack with at least 8-byte alignment
        // (alignas(8) declaration on the struct).
        FField LocalField;

        FFieldVariant V{&LocalField};

        Check(V.IsField(),   "FFieldVariant(field_ptr).IsField() returned false");
        Check(!V.IsStruct(), "FFieldVariant(field_ptr).IsStruct() returned true");
        Check(!V.IsNull(),   "FFieldVariant(field_ptr).IsNull() returned true for non-null");

        Check(V.AsField()  == &LocalField,
              "FFieldVariant(field_ptr).AsField() did not recover original ptr");
        Check(V.AsStruct() == nullptr,
              "FFieldVariant(field_ptr).AsStruct() did not return nullptr");

        // LSB tag MUST be clear for a Field-tagged variant.
        Check((V.GetRaw() & FFieldVariant::kFStructTag) == 0,
              "Field-tagged variant has LSB set (should be clear)");

        // The raw Storage MUST equal the pointer bits verbatim
        // (no transformation when LSB is already 0).
        Check(V.GetRaw() == reinterpret_cast<std::uint64_t>(&LocalField),
              "Field-tagged variant Storage != raw pointer bits");
    }

    // -----------------------------------------------------------------
    // (2) Struct-pointer round-trip.
    //
    // Synthesize an 8-byte-aligned address to use as a stand-in FStruct
    // pointer. The variant stores the pointer (does NOT dereference),
    // so a synthetic address is sufficient for the LSB-tag test.
    //
    // We use a stack-local uint64 with 8-byte alignment guaranteed by
    // the alignas declaration; the address of the uint64 is then cast
    // to `const FStruct*` via reinterpret_cast. The LSB of the address
    // is structurally zero (alignment >= 8).
    // -----------------------------------------------------------------
    {
        alignas(8) std::uint64_t SyntheticStructStorage = 0xDEADBEEFCAFEBABEull;
        const FStruct* SyntheticStruct =
            reinterpret_cast<const FStruct*>(&SyntheticStructStorage);

        // Pre-check: the synthetic address must have LSB == 0 (alignas(8)
        // guarantees this; we verify it as a sanity check before the
        // round-trip).
        Check((reinterpret_cast<std::uint64_t>(SyntheticStruct) & 0x1ull) == 0,
              "Synthetic FStruct address has LSB set (alignment violated)");

        FFieldVariant V{SyntheticStruct};

        Check(!V.IsField(), "FFieldVariant(struct_ptr).IsField() returned true");
        Check(V.IsStruct(), "FFieldVariant(struct_ptr).IsStruct() returned false");
        Check(!V.IsNull(),  "FFieldVariant(struct_ptr).IsNull() returned true for non-null");

        Check(V.AsStruct() == SyntheticStruct,
              "FFieldVariant(struct_ptr).AsStruct() did not recover original ptr");
        Check(V.AsField() == nullptr,
              "FFieldVariant(struct_ptr).AsField() did not return nullptr");

        // LSB tag MUST be set for a Struct-tagged variant.
        Check((V.GetRaw() & FFieldVariant::kFStructTag) == FFieldVariant::kFStructTag,
              "Struct-tagged variant has LSB clear (should be set)");

        // The raw Storage MUST equal `pointer_bits | 0x1`.
        const std::uint64_t Expected =
            reinterpret_cast<std::uint64_t>(SyntheticStruct) | FFieldVariant::kFStructTag;
        Check(V.GetRaw() == Expected,
              "Struct-tagged variant Storage != (pointer | 0x1)");
    }

    // -----------------------------------------------------------------
    // (3) LSB inversion from UE.
    //
    // XPact's tag semantic is the INVERSE of UE's. Verify by inspecting
    // the raw Storage bits directly:
    //
    //   * UE: LSB=1 means UObject (Field.h:454); LSB=0 means FField.
    //   * XPact: LSB=1 means FStruct; LSB=0 means FField.
    //
    // The FField case matches between UE and XPact (both clear the LSB).
    // The FStruct vs UObject case is the inverted polarity.
    //
    // The test confirms: a Field-tagged variant has LSB == 0; a Struct-
    // tagged variant has LSB == 1. The two polarity bits are distinct.
    // -----------------------------------------------------------------
    {
        FField LocalField;
        alignas(8) std::uint64_t SyntheticStructStorage = 0;
        const FStruct* SyntheticStruct =
            reinterpret_cast<const FStruct*>(&SyntheticStructStorage);

        FFieldVariant Field{&LocalField};
        FFieldVariant Struct{SyntheticStruct};

        // Field's LSB is structurally zero.
        Check((Field.GetRaw() & 0x1ull) == 0,
              "Field-tagged variant LSB != 0 (xPact polarity violated)");

        // Struct's LSB is set.
        Check((Struct.GetRaw() & 0x1ull) == 0x1ull,
              "Struct-tagged variant LSB != 1 (XPact polarity violated)");
    }

    // -----------------------------------------------------------------
    // (4) Cross-construct comparison.
    //
    // Two variants constructed from the same FField pointer compare
    // bytewise-equal. A variant constructed from a synthetic FStruct*
    // whose address numerically equals the FField pointer compares
    // bytewise-UNEQUAL to the Field-tagged variant (because the
    // tagged Storage values differ in the LSB).
    //
    // This subtle distinction matters: a hand-written equality check
    // that compared the AsField()/AsStruct() return values without
    // first probing the kind would incorrectly conclude "same pointer
    // == same variant" -- which is wrong because the kind tag is part
    // of identity.
    // -----------------------------------------------------------------
    {
        FField LocalField;

        FFieldVariant Va{&LocalField};
        FFieldVariant Vb{&LocalField};

        Check(Va == Vb,    "Field variant equality (same ptr) returned false");
        Check(!(Va != Vb), "Field variant inequality (same ptr) returned true");

        // A second Field with a DIFFERENT address -> unequal variants.
        FField OtherField;
        FFieldVariant Vc{&OtherField};
        Check(Va != Vc,    "Field variants with different ptrs compared equal");
        Check(!(Va == Vc), "Field variants with different ptrs compared equal (operator==)");

        // Synthetic FStruct at the address of LocalField -- numerically
        // matches the FField pointer but kind tag differs, so variants
        // compare unequal. (Aside: this is a synthetic scenario;
        // production code never reuses an FField address as an FStruct.)
        const FStruct* SyntheticStruct =
            reinterpret_cast<const FStruct*>(&LocalField);
        FFieldVariant Vstruct{SyntheticStruct};
        Check(Va != Vstruct,
              "Field-tagged variant and Struct-tagged variant at same address compared equal");
    }

    // -----------------------------------------------------------------
    // (5) Predicate consistency: every variant has exactly one of
    // IsField/IsStruct true at any time (except for the constinit-init
    // null case, which is IsField==true + AsField()==nullptr).
    // -----------------------------------------------------------------
    {
        FField LocalField;
        alignas(8) std::uint64_t SyntheticStructStorage = 0;
        const FStruct* SyntheticStruct =
            reinterpret_cast<const FStruct*>(&SyntheticStructStorage);

        FFieldVariant Field{&LocalField};
        FFieldVariant Struct{SyntheticStruct};

        // Field: IsField && !IsStruct
        Check(Field.IsField() && !Field.IsStruct(),
              "Field-tagged variant predicates inconsistent (IsField && !IsStruct expected)");

        // Struct: !IsField && IsStruct
        Check(!Struct.IsField() && Struct.IsStruct(),
              "Struct-tagged variant predicates inconsistent (!IsField && IsStruct expected)");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FField.FFieldVariantLSBTag: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FField.FFieldVariantLSBTag: PASS\n";
    return 0;
}
