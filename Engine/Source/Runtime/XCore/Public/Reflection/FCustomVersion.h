// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FCustomVersion.h -- per-archive custom-version record (XCore-4b §8.1).
// =====================================================================
//
// XCore-4b Rev 3, Section 8 (FCustomVersion Design).
//
// Per Master Plan Rev 15 §2 Reflection schema versioning row:
//
//   "Reflection metadata binary format is self-describing with monotonic
//    FCustomVersion. Each reflected type has a stable GUID and a version
//    int."
//
// XCore-4b ships the FCustomVersion subsystem so XSerialization (Layer
// 9) can persist asset data with version migrations. This file defines
// the FCustomVersion POD record itself; the per-archive container is in
// FCustomVersionContainer.h and the process-wide registry is in
// FCustomVersionRegistry.h.
//
// LAYOUT (locked at 32 bytes per Section 8.1 + the Stage B addendum
// XPACT_FCUSTOMVERSION_LAYOUT_TAG = "FCustomVersion-v1: Key(FGuid 16) +
// Version(int32) + FriendlyName(FName)"):
//
//   struct alignas(8) FCustomVersion {
//       FGuid    Key;             //  0  +16   stable 128-bit per-type GUID
//       int32    Version;         //  16 +4    monotonic version int
//       uint32   _Reserved;       //  20 +4    reserved (alignment pad; future flags)
//       FName    FriendlyName;    //  24 +8    human-readable friendly name
//   };
//
// The Reserved field is named in the public API rather than left
// anonymous so future revisions can repurpose it without breaking ABI;
// it MUST be zero in all current code (the constructor enforces this
// and consumers verifying byte-exact round-trip can compare it as 0).
//
// Hot-reload safety (Section 9.1): zero virtual methods; trivially
// copyable; constinit-friendly (the constructor is constexpr so an
// XHT-emitted aggregator can populate a constinit array of
// FCustomVersion entries at module-init time).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Reflection/FGuid.h"
#include "Reflection/FName.h"     // Full FName definition required: FName is
                                  // a by-value member of FCustomVersion, not
                                  // a pointer/reference, so the layout-locked
                                  // struct body must be visible at this site.

#include <cstddef>                // offsetof
#include <type_traits>            // is_trivially_copyable etc.

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FCustomVersion -- {Key, Version, FriendlyName} triple.
    //
    // The record is what XSerialization writes into an archive header
    // and reads back at load time to drive version migrations. It is
    // also the unit of registration into the process-wide
    // FCustomVersionRegistry (see FCustomVersionRegistry.h).
    // -----------------------------------------------------------------
    struct alignas(8) FCustomVersion
    {
        FGuid    Key;             // 0  +16   stable 128-bit per-type GUID
        ::int32  Version;         // 16 +4    monotonic version int
        ::uint32 _Reserved;       // 20 +4    reserved for future flags (MUST be 0)
        FName    FriendlyName;    // 24 +8    human-readable friendly name (FName handle)

        // -------------------------------------------------------------
        // Default constructor: zero-initialised (NAME_None friendly
        // name, zero GUID, version 0). The zero state is the
        // "uninitialised" sentinel; calls to FCustomVersionRegistry::
        // RegisterCustomVersion(version) where version.Key.IsZero() are
        // rejected because the zero GUID is reserved.
        // -------------------------------------------------------------
        constexpr FCustomVersion() noexcept
            : Key{0, 0, 0, 0}
            , Version(0)
            , _Reserved(0)
            , FriendlyName()        // Use FName's default constexpr ctor explicitly
                                    // (the {0, 0} braced form was interpreted by
                                    // MSVC as a non-constexpr constructor-call
                                    // candidate during overload resolution).
        {
        }

        // -------------------------------------------------------------
        // Explicit constructor. The Reserved field is forced to zero;
        // callers MUST NOT populate it. A future revision that adds a
        // semantic interpretation to Reserved will add a separate
        // constructor; the no-Reserved overload remains binary-stable.
        // -------------------------------------------------------------
        constexpr FCustomVersion(FGuid InKey, ::int32 InVersion, FName InFriendlyName) noexcept
            : Key(InKey)
            , Version(InVersion)
            , _Reserved(0)
            , FriendlyName(InFriendlyName)
        {
        }

        // -------------------------------------------------------------
        // Equality. Bytewise across all four fields. Two FCustomVersion
        // records are equal iff their Key+Version+FriendlyName+_Reserved
        // are all bitwise equal. This is the strict round-trip equality
        // used by FCustomVersionContainer::Serialize round-trip tests
        // (Acceptance gate D1).
        // -------------------------------------------------------------
        [[nodiscard]] friend constexpr bool operator==(const FCustomVersion& Lhs,
                                                       const FCustomVersion& Rhs) noexcept
        {
            return Lhs.Key == Rhs.Key
                && Lhs.Version == Rhs.Version
                && Lhs._Reserved == Rhs._Reserved
                && Lhs.FriendlyName.Index == Rhs.FriendlyName.Index
                && Lhs.FriendlyName.SerialNumber == Rhs.FriendlyName.SerialNumber;
        }

        [[nodiscard]] friend constexpr bool operator!=(const FCustomVersion& Lhs,
                                                       const FCustomVersion& Rhs) noexcept
        {
            return !(Lhs == Rhs);
        }

        // -------------------------------------------------------------
        // Byte-level serialization helpers. Until XSerialization's
        // FArchive lands (Layer 9), the container's round-trip uses
        // these raw byte read/write helpers. They are explicit about
        // endianness: the on-the-wire format is little-endian for every
        // integer field (matching every other XPact persistent format
        // -- XCore-4a's loctable header is little-endian per §11.4).
        //
        // The buffer is exactly sizeof(FCustomVersion) == 32 bytes; the
        // helpers do NOT prepend/length-encode.
        //
        // RATIONALE for byte helpers rather than memcpy: explicit byte
        // ordering protects the on-disk format from being silently
        // sensitive to host endianness. Even though every XPact target
        // (Win64 / Linux / Android-ARM64) is little-endian today, the
        // discipline of going through byte helpers is the
        // engineering-principles-correct path (Prime Directive: build
        // the durable shape, not the shortcut). On every supported
        // target the helpers compile down to a single 32-byte memcpy
        // because the host is already little-endian.
        // -------------------------------------------------------------
        void WriteToBuffer(::uint8* OutBuffer) const noexcept;
        static FCustomVersion ReadFromBuffer(const ::uint8* InBuffer) noexcept;

        // Number of bytes WriteToBuffer writes / ReadFromBuffer reads.
        // Equal to sizeof(FCustomVersion) on the supported little-endian
        // targets; kept as a constant for clarity at call sites.
        static constexpr ::SIZE_T kSerializedSize = 32;
    };

    // ABI lock. The Stage B addendum's XPACT_FCUSTOMVERSION_LAYOUT_TAG
    // string-literal pin asserts the same shape at every XHT-emitted
    // .gen.cpp site.
    static_assert(sizeof(FCustomVersion)  == 32,
                  "FCustomVersion ABI lock: must be 32 bytes "
                  "(Key 16 + Version 4 + Reserved 4 + FriendlyName 8). "
                  "See XCore-4b Section 8.1.");
    static_assert(alignof(FCustomVersion) == 8,
                  "FCustomVersion ABI lock: 8-byte alignment "
                  "(natural alignment of the struct's largest member after "
                  "the alignas(8) tag).");

    // Member offsets locked to the bytes the Stage B addendum names.
    // These static_asserts give a sharper diagnostic than the total
    // sizeof check when a future refactor accidentally shifts a field.
    static_assert(offsetof(FCustomVersion, Key)          ==  0,
                  "FCustomVersion ABI lock: Key at offset 0");
    static_assert(offsetof(FCustomVersion, Version)      == 16,
                  "FCustomVersion ABI lock: Version at offset 16");
    static_assert(offsetof(FCustomVersion, _Reserved)    == 20,
                  "FCustomVersion ABI lock: Reserved at offset 20");
    static_assert(offsetof(FCustomVersion, FriendlyName) == 24,
                  "FCustomVersion ABI lock: FriendlyName at offset 24");

    // Type traits: FCustomVersion is a POD-style aggregate.
    static_assert(::std::is_standard_layout_v<FCustomVersion>,
                  "FCustomVersion must be standard layout (so offsetof is well-defined)");
    static_assert(::std::is_trivially_copyable_v<FCustomVersion>,
                  "FCustomVersion must be trivially copyable (memcpy-able in TArray)");
    static_assert(::std::is_trivially_destructible_v<FCustomVersion>,
                  "FCustomVersion must be trivially destructible (no per-instance teardown)");

} // namespace XCore::Reflect
