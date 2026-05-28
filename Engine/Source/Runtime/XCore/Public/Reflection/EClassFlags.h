// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EClassFlags.h -- per-FClass trait bitmask (XCore-4b §7.3).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.3 ("FClass") + Section 11.3 byte layout
// row `ClassFlags 24 +8`.
//
// EClassFlags is the 64-bit trait bitmask carried directly on FClass
// at offset 128 (24 from end of FStruct base = 24 + 104 = 128). The
// 8-byte width matches UE's `EClassFlags` (`ObjectMacros.h:267`); the
// bit-position layout is locked in the Stage B addendum and mirrors
// UE's MVP subset.
//
// Bit assignments (matching UE's CLASS_* constants):
//
//   CLASS_None                  0x00000000
//   CLASS_Abstract              0x00000001  cannot be instantiated directly
//   CLASS_DefaultConfig         0x00000002  load default config from class-specified .ini
//   CLASS_Config                0x00000004  loads config on construction
//   CLASS_Transient             0x00000008  not saved to disk
//   CLASS_Native                0x00000020  C++-declared (not Blueprint)
//   CLASS_NoExport              0x00000100  not exported to script
//   CLASS_Hidden                0x00001000  hidden from Editor browse
//   CLASS_Deprecated            0x00002000  deprecated; show warning
//   CLASS_HideDropDown          0x00004000  hidden from dropdowns
//   CLASS_Interface             0x00040000  interface class (FInterface)
//   CLASS_Const                 0x00010000  immutable instances
//   CLASS_HasInstancedReference 0x00200000  contains instanced object reference
//   CLASS_CompiledFromBlueprint 0x00040000  generated from Blueprint
//   CLASS_MatchedSerializers    0x10000000  has matched C++/Blueprint serializers
//
// XPact additions:
//
//   CLASS_EagerCDO              0x0000000000000010  per Rev 3 FIX-H-R2-6;
//                                                    eager CDO at PostStaticInit
//                                                    (XCoreXObject Rev 4 §8.1 +
//                                                    §8.1.1). Bit 4 was unused in
//                                                    the UE EClassFlags layout
//                                                    (CLASS_None@0, CLASS_Abstract@0,
//                                                    Default/Config@1/2, Transient@3,
//                                                    Native@5); Phase 5.d claims it
//                                                    for the EagerCDO opt-in.
//
//   CLASS_GameDataClass         0x4000000000000000  game-system-data tag
//   CLASS_ReplicationRoot       0x8000000000000000  network replication root
//
// ABI LOCK: underlying type uint64 (matches FClass layout @ offset 128
// in an 8-byte slot).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EClassFlags -- 64-bit per-FClass trait bitmask.
    // -----------------------------------------------------------------
    enum class EClassFlags : ::uint64
    {
        CLASS_None                  = 0,

        // Construction / lifecycle.
        CLASS_Abstract              = 1ULL <<  0,
        CLASS_DefaultConfig         = 1ULL <<  1,
        CLASS_Config                = 1ULL <<  2,
        CLASS_Transient             = 1ULL <<  3,

        // CDO construction policy (Rev 3 FIX-H-R2-6; per XCoreXObject
        // Rev 4 §8.1.1). Opt-in eager construction at PostStaticInit
        // boundary; default (flag clear) is lazy CDO construction on
        // first GetClassDefaultObject call.
        //
        // Bit 4 was previously unused in the UE EClassFlags layout
        // (CLASS_Abstract@0, CLASS_DefaultConfig@1, CLASS_Config@2,
        // CLASS_Transient@3, CLASS_Native@5, no UE flag at bit 4).
        // Phase 5.d claims it for XPact's EagerCDO opt-in.
        CLASS_EagerCDO              = 1ULL <<  4,

        // Provenance.
        CLASS_Native                = 1ULL <<  5,
        CLASS_NoExport              = 1ULL <<  8,

        // Editor visibility.
        CLASS_Hidden                = 1ULL << 12,
        CLASS_Deprecated            = 1ULL << 13,
        CLASS_HideDropDown          = 1ULL << 14,

        // Class semantics.
        CLASS_Const                 = 1ULL << 16,
        CLASS_HasInstancedReference = 1ULL << 21,
        CLASS_CompiledFromBlueprint = 1ULL << 17,
        CLASS_Interface             = 1ULL << 18,
        CLASS_MatchedSerializers    = 1ULL << 28,

        // XPact-only bits in upper half (post-MVP placeholders).
        CLASS_GameDataClass         = 1ULL << 62,
        CLASS_ReplicationRoot       = 1ULL << 63,
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EClassFlags operator|(EClassFlags Lhs, EClassFlags Rhs) noexcept
    {
        return static_cast<EClassFlags>(
            static_cast<::uint64>(Lhs) | static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EClassFlags operator&(EClassFlags Lhs, EClassFlags Rhs) noexcept
    {
        return static_cast<EClassFlags>(
            static_cast<::uint64>(Lhs) & static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EClassFlags operator^(EClassFlags Lhs, EClassFlags Rhs) noexcept
    {
        return static_cast<EClassFlags>(
            static_cast<::uint64>(Lhs) ^ static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EClassFlags operator~(EClassFlags Operand) noexcept
    {
        return static_cast<EClassFlags>(~static_cast<::uint64>(Operand));
    }

    constexpr EClassFlags& operator|=(EClassFlags& Lhs, EClassFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EClassFlags& operator&=(EClassFlags& Lhs, EClassFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    constexpr EClassFlags& operator^=(EClassFlags& Lhs, EClassFlags Rhs) noexcept
    {
        Lhs = Lhs ^ Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock.
    // -----------------------------------------------------------------
    static_assert(sizeof(EClassFlags) == 8,
                  "EClassFlags ABI lock: underlying type must be uint64 "
                  "(8 bytes; matches FClass::ClassFlags @ FStruct-end + 24)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EClassFlags>, ::uint64>,
                  "EClassFlags ABI lock: underlying type must be uint64");

} // namespace XCore::Reflect
