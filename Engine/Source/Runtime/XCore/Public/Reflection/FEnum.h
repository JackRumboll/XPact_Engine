// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FEnum.h -- the 64-byte enum descriptor (XCore-4b §7.4 + §11.3;
// FIX-R2-CRIT-1).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.4 ("FEnum") + Section 11.3 layout row
// `FEnum: 64 bytes (32 FField + 32 FEnum-specific)`.
//
// FEnum is the runtime descriptor for reflected enums. UE's `UEnum`
// (`Class.h:2877`) is XPact's reference. Carries:
//
//   * EnumFlags    -- bitmask of enum-level traits (ENUM_Flags for
//                     bitflag enums, ENUM_NewType for post-Rev-1
//                     enums that need legacy-handling cues).
//   * Values       -- TArray<FEnumValue> of (Name, numeric value)
//                     pairs. Sorted by source-declaration order; the
//                     numeric values themselves may be non-contiguous
//                     (e.g., explicit `= 5` enumerators).
//   * CppForm      -- FName describing the C++ declaration shape:
//                     "Regular" (old-style C enum), "Namespaced"
//                     (UE-style namespace wrapper), "EnumClass"
//                     (C++11 `enum class` form).
//
// FEnum EXTENDS FField. The FField base carries Name + Owner + Class
// + Next; FEnum's NamePrivate (from FField) is the enum's FName
// (e.g., "ECollisionChannel").
//
// LAYOUT (Phase 4b.5 audit-corrected; spec said 64 with TArray=16,
// actually 72 with TArray=24; see FStruct.h SPEC DRIFT NOTICE):
//
//   struct alignas(8) FEnum : FField {
//       // FField base @ 0-31 (32 bytes)
//       EEnumFlags         EnumFlags;       // 32  +4
//       uint32             _pad;            // 36  +4
//       TArray<FEnumValue> Values;          // 40 +24 (TArray=24, not 16)
//       FName              CppForm;         // 64  +8
//   };
//
// sizeof(FEnum) == 72.
//
// XPACT_FENUM_LAYOUT_TAG (Rev 3 §11.6):
//   "FEnum-v3: 64 bytes; Values TArray @ offset 40 = 16 bytes;
//    CppForm @ 56"
//
// CppForm STRING VALUES:
//
//   "Regular"     -- old-style C enum: `enum EFoo { A, B, C };`
//   "Namespaced"  -- UE wrapper:       `namespace EFoo { enum Type { A, B, C }; }`
//   "EnumClass"   -- C++11 class:      `enum class EFoo { A, B, C };`
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/TArray.h"
#include "Reflection/EEnumFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

#include <cstddef>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FEnumValue -- a single (Name, numeric value) pair in
    // FEnum::Values.
    //
    // Per spec §7.4 + §11.3: 16 bytes, FName + int64.
    // -----------------------------------------------------------------
    struct alignas(8) FEnumValue
    {
        FName   Name;       //  0  +8   enumerator name (e.g. "ECC_Pawn")
        ::int64 Value;      //  8  +8   numeric value (signed int64 to cover
                            //          UE's full range and negative-valued
                            //          enumerators)

        constexpr FEnumValue() noexcept
            : Name()
            , Value(0)
        {
        }

        constexpr FEnumValue(FName InName, ::int64 InValue) noexcept
            : Name(InName)
            , Value(InValue)
        {
        }
    };

    static_assert(sizeof(FEnumValue)  == 16,
                  "FEnumValue ABI lock: must be exactly 16 bytes "
                  "(FName 8 + int64 8). See XCore-4b §7.4 + §11.3.");
    static_assert(alignof(FEnumValue) == 8,
                  "FEnumValue ABI lock: 8-byte alignment");
    static_assert(offsetof(FEnumValue, Name)  == 0,
                  "FEnumValue ABI lock: Name at offset 0");
    static_assert(offsetof(FEnumValue, Value) == 8,
                  "FEnumValue ABI lock: Value at offset 8");
    static_assert(::std::is_standard_layout_v<FEnumValue>,
                  "FEnumValue must be standard layout");
    static_assert(::std::is_trivially_copyable_v<FEnumValue>,
                  "FEnumValue must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FEnumValue>,
                  "FEnumValue must be trivially destructible");

    // -----------------------------------------------------------------
    // FEnum -- 64-byte FField subclass for reflected enums.
    //
    // Per spec §7.4: alignas(8). NO virtual methods.
    //
    // FEnum is non-copyable + non-movable because it owns the Values
    // TArray (which holds heap-allocated FEnumValue storage); the
    // intra-engine references to FEnum are by pointer.
    // -----------------------------------------------------------------
    struct alignas(8) FEnum : public FField
    {
        // ---- Per-subclass payload (offsets 32-63; 32 bytes) ----

        EEnumFlags         EnumFlags;       // 32  +4   enum-level trait bitmask
        ::uint32           _pad;            // 36  +4   pad to 8-byte boundary
        ::XCore::TArray<FEnumValue> Values; // 40 +24   (Name, value) pairs; TArray=24
        FName              CppForm;         // 64  +8   "Regular" / "Namespaced" / "EnumClass"

        // -------------------------------------------------------------
        // Construction.
        // -------------------------------------------------------------

        FEnum() noexcept
            : FField()
            , EnumFlags(EEnumFlags::ENUM_None)
            , _pad(0)
            , Values()
            , CppForm()
        {
        }

        FEnum(FFieldVariant InOwner, FName InName,
              EEnumFlags InEnumFlags = EEnumFlags::ENUM_None) noexcept;

        // Non-copyable + non-movable: owns Values TArray.
        FEnum(const FEnum&)            = delete;
        FEnum(FEnum&&)                 = delete;
        FEnum& operator=(const FEnum&) = delete;
        FEnum& operator=(FEnum&&)      = delete;
        ~FEnum() noexcept              = default;

        // -------------------------------------------------------------
        // Accessors.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE EEnumFlags GetEnumFlags() const noexcept
        {
            return EnumFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE const ::XCore::TArray<FEnumValue>&
            GetValues() const noexcept
        {
            return Values;
        }

        [[nodiscard]] XPACT_FORCEINLINE FName GetCppForm() const noexcept
        {
            return CppForm;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 NumEnumerators() const noexcept
        {
            return Values.Num();
        }

        // -------------------------------------------------------------
        // FindByName -- linear scan for an enumerator with the given
        // FName. Returns the numeric value via the out parameter and
        // true on match; false if the name is not present.
        //
        // Body in FEnum.cpp.
        // -------------------------------------------------------------
        [[nodiscard]] bool FindByName(FName EnumeratorName, ::int64& OutValue) const noexcept;

        // -------------------------------------------------------------
        // FindByValue -- linear scan for an enumerator with the given
        // numeric value. Returns the FName via the out parameter and
        // true on match; false if no enumerator has that value.
        //
        // For bitflag enums (ENUM_Flags), FindByValue matches only
        // declared single-bit enumerators; composed bitflag values
        // are not in Values and produce false.
        //
        // Body in FEnum.cpp.
        // -------------------------------------------------------------
        [[nodiscard]] bool FindByValue(::int64 EnumeratorValue, FName& OutName) const noexcept;

        // -------------------------------------------------------------
        // FConstructFn target.
        // -------------------------------------------------------------
        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // -------------------------------------------------------------
        // StaticClass hook for Cast<T>.
        // -------------------------------------------------------------
        static const FFieldClass* StaticClass() noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (Phase 4b.5 audit-corrected; see SPEC DRIFT in FStruct.h).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FEnum)  == 72,
                  "FEnum ABI lock (audit-corrected): 72 bytes (32 FField + 40 "
                  "FEnum-specific). Spec Rev 3 §7.4 declared 64 assuming "
                  "TArray=16; actual TArray=24 makes the Values member 24 "
                  "bytes and pushes CppForm to offset 64. Note Rev 2 also "
                  "declared 72 -- this matches Rev 2's value coincidentally.");
    static_assert(alignof(FEnum) == 8,
                  "FEnum ABI lock: 8-byte alignment per §7.4 alignas(8)");

    // Member offsets locked per audit-corrected layout.
    static_assert(offsetof(FEnum, EnumFlags) == 32,
                  "FEnum ABI lock: EnumFlags at offset 32");
    static_assert(offsetof(FEnum, Values)    == 40,
                  "FEnum ABI lock: Values at offset 40");
    static_assert(offsetof(FEnum, CppForm)   == 64,
                  "FEnum ABI lock (audit-corrected): CppForm at offset 64 "
                  "(spec said 56; +8 shift from TArray=24)");

    // -----------------------------------------------------------------
    // The FFieldClass + accessor for FEnum.
    //
    // FEnum is an FField subclass like FProperty; it gets its own
    // FFieldClass anchor + StaticClass() hook for Cast<FEnum>. The
    // FFieldClass carries kNone CastFlags (no FProperty parent gate
    // bit; FEnum is not part of the FProperty hierarchy).
    // -----------------------------------------------------------------
    extern FFieldClass kFEnumStaticClass;
    const FFieldClass& GetFEnumStaticClass() noexcept;

} // namespace XCore::Reflect
