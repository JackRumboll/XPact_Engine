// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FField.Tests/SizeofAndAlignof.cpp -- FField + FFieldClass +
// FFieldVariant ABI lock verification (acceptance gate B1).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 + Section 5.2 + Section 11.2 (Stage B
// addendum layout table).
//
// Verifies at runtime via the returned exit code that:
//
//   * sizeof(FFieldVariant)             == 8
//   * alignof(FFieldVariant)            == 8
//   * offsetof(FFieldVariant, Storage)  == 0
//
//   * sizeof(FFieldClass)               == 48
//   * alignof(FFieldClass)              == 16
//   * offsetof(FFieldClass, Name)       == 0
//   * offsetof(FFieldClass, Id)         == 8
//   * offsetof(FFieldClass, CastFlags)  == 16
//   * offsetof(FFieldClass, SuperClass) == 24
//   * offsetof(FFieldClass, Construct)  == 32
//   * offsetof(FFieldClass, FakeVTable) == 40
//
//   * sizeof(FField)                    == 32
//   * alignof(FField)                   == 8
//   * offsetof(FField, ClassPrivate)    == 0
//   * offsetof(FField, Owner)           == 8
//   * offsetof(FField, Next)            == 16
//   * offsetof(FField, NamePrivate)     == 24
//
//   * sizeof(EClassCastFlags)           == 8 (uint64 underlying)
//
// The static_asserts in the headers themselves already pin these; this
// test runs them as a separate TU to catch any toolchain divergence
// (e.g. MSVC vs Clang struct-layout differences that one header alone
// would not surface) and emits a CI artifact for acceptance gate B1.
//
// =====================================================================

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"

#include <cstddef>
#include <iostream>
#include <type_traits>

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
    using ::XCore::Reflect::EClassCastFlags;
    using ::XCore::Reflect::FField;
    using ::XCore::Reflect::FFieldClass;
    using ::XCore::Reflect::FFieldVariant;

    // -----------------------------------------------------------------
    // EClassCastFlags underlying type.
    // -----------------------------------------------------------------
    Check(sizeof(EClassCastFlags) == 8,
          "sizeof(EClassCastFlags) != 8 (must be uint64-backed)");
    Check(std::is_same_v<std::underlying_type_t<EClassCastFlags>, std::uint64_t>,
          "EClassCastFlags underlying type must be uint64");

    // -----------------------------------------------------------------
    // FFieldVariant 8-byte ABI lock (§5.1; FIX-4).
    // -----------------------------------------------------------------
    Check(sizeof(FFieldVariant) == 8, "sizeof(FFieldVariant) != 8");
    Check(alignof(FFieldVariant) == 8, "alignof(FFieldVariant) != 8");
    Check(offsetof(FFieldVariant, Storage) == 0,
          "offsetof(FFieldVariant, Storage) != 0");

    Check(FFieldVariant::kFStructTag == 0x1ull,
          "FFieldVariant::kFStructTag must be 0x1");
    Check(FFieldVariant::kPointerMask == ~std::uint64_t(0x1),
          "FFieldVariant::kPointerMask must be ~0x1");

    // -----------------------------------------------------------------
    // FFieldClass 48-byte ABI lock (§5.2; FIX-2 added FakeVTable slot).
    // -----------------------------------------------------------------
    Check(sizeof(FFieldClass) == 48, "sizeof(FFieldClass) != 48");
    Check(alignof(FFieldClass) == 16, "alignof(FFieldClass) != 16");

    Check(offsetof(FFieldClass, Name)       ==  0,
          "offsetof(FFieldClass, Name) != 0");
    Check(offsetof(FFieldClass, Id)         ==  8,
          "offsetof(FFieldClass, Id) != 8");
    Check(offsetof(FFieldClass, CastFlags)  == 16,
          "offsetof(FFieldClass, CastFlags) != 16");
    Check(offsetof(FFieldClass, SuperClass) == 24,
          "offsetof(FFieldClass, SuperClass) != 24");
    Check(offsetof(FFieldClass, Construct)  == 32,
          "offsetof(FFieldClass, Construct) != 32");
    Check(offsetof(FFieldClass, FakeVTable) == 40,
          "offsetof(FFieldClass, FakeVTable) != 40");

    // -----------------------------------------------------------------
    // FField 32-byte ABI lock (§5.1; acceptance gate B1).
    // -----------------------------------------------------------------
    Check(sizeof(FField)  == 32, "sizeof(FField) != 32");
    Check(alignof(FField) ==  8, "alignof(FField) != 8");

    Check(offsetof(FField, ClassPrivate) ==  0,
          "offsetof(FField, ClassPrivate) != 0");
    Check(offsetof(FField, Owner)        ==  8,
          "offsetof(FField, Owner) != 8");
    Check(offsetof(FField, Next)         == 16,
          "offsetof(FField, Next) != 16");
    Check(offsetof(FField, NamePrivate)  == 24,
          "offsetof(FField, NamePrivate) != 24");

    // -----------------------------------------------------------------
    // POD-ness traits. FField is standard-layout + trivially-copyable +
    // trivially-destructible per the discipline at the FField.h ABI
    // static_assert site.
    // -----------------------------------------------------------------
    Check(std::is_standard_layout_v<FFieldVariant>,
          "FFieldVariant is not standard-layout");
    Check(std::is_trivially_copyable_v<FFieldVariant>,
          "FFieldVariant is not trivially copyable");
    Check(std::is_trivially_destructible_v<FFieldVariant>,
          "FFieldVariant is not trivially destructible");

    Check(std::is_standard_layout_v<FFieldClass>,
          "FFieldClass is not standard-layout");
    Check(std::is_trivially_copyable_v<FFieldClass>,
          "FFieldClass is not trivially copyable");
    Check(std::is_trivially_destructible_v<FFieldClass>,
          "FFieldClass is not trivially destructible");

    Check(std::is_standard_layout_v<FField>,
          "FField is not standard-layout");
    Check(std::is_trivially_copyable_v<FField>,
          "FField is not trivially copyable");
    Check(std::is_trivially_destructible_v<FField>,
          "FField is not trivially destructible");

    if (g_FailureCount > 0)
    {
        std::cerr << "FField.SizeofAndAlignof: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FField.SizeofAndAlignof: PASS\n";
    return 0;
}
