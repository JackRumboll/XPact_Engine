// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// ELifetimeCondition.h -- replication-condition enumeration
// (XCore-4b §6.1 + Section 11.4).
// =====================================================================
//
// XCore-4b Rev 3, Section 6.1 ("ELifetimeCondition complete enumeration")
// + Section 11.4 ("ELifetimeCondition numeric values").
//
// Mirrors UE's `ELifetimeCondition` (`CoreNetTypes.h:16`) verbatim for
// behaviour but is explicitly defined here with locked numeric values.
// The numeric values are part of the Stage B addendum
// (XPACT_REPMETA_LAYOUT_TAG; see §11.4).
//
// The enum is `uint8_t`-backed so the BlueprintReplicationCondition
// field on FProperty (offset 58 per §11.2) packs into one byte. The
// 4-bit values UE uses are widened to 8 bits to leave reserved space
// for future conditions (XPact discipline of leaving room).
//
// ABI LOCK (Rev 2; Section 11.4):
//
//   * sizeof(ELifetimeCondition) == 1 (locked).
//   * Numeric values 0..14 are committed.
//   * Value 15 is COND_Max sentinel (not a real condition).
//   * Values 16..255 are RESERVED for future conditions.
//
// XPACT_REPMETA_LAYOUT_TAG = "RepMeta-v2: 15-condition ELifetimeCondition(1)
//   + RepIndex(2) + RepNotifyFunc-as-FName(8); UE-equivalent pre-Iris set;
//   CPF_PushModel + CPF_DeltaCompressed are EPropertyFlags bits"
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // ELifetimeCondition -- 15 numbered replication-condition values
    // per §6.1.
    //
    // Numeric values are part of the Stage B addendum (§11.4) and MUST
    // NOT change without a Contract Rev 13.9+ bump.
    // -----------------------------------------------------------------
    enum class ELifetimeCondition : ::uint8
    {
        COND_None                       = 0,   // no condition; sends to every connection
        COND_InitialOnly                = 1,   // sends only on initial bunch
        COND_OwnerOnly                  = 2,   // sends only to owning connection
        COND_SkipOwner                  = 3,   // sends to every connection EXCEPT owner
        COND_SimulatedOnly              = 4,   // sends only to simulated proxies
        COND_AutonomousOnly             = 5,   // sends only to autonomous-proxy connection
        COND_SimulatedOrPhysics         = 6,   // sends to simulated OR bRepPhysics actors
        COND_InitialOrOwner             = 7,   // initial bunch OR owning connection
        COND_Custom                     = 8,   // per-property custom predicate
        COND_ReplayOrOwner              = 9,   // replay connections OR owning connection
        COND_ReplayOnly                 = 10,  // replay connections only
        COND_SimulatedOnlyNoReplay      = 11,  // simulated proxies, not replay
        COND_SimulatedOrPhysicsNoReplay = 12,  // simulated OR physics-true, not replay
        COND_SkipReplay                 = 13,  // every connection EXCEPT replay
        COND_Never                      = 14,  // reflectable but never replicated

        COND_Max                        = 15,  // sentinel; not used as a real condition
    };

    // -----------------------------------------------------------------
    // ABI lock (Section 11.4 + XPACT_REPMETA_LAYOUT_TAG).
    // -----------------------------------------------------------------
    static_assert(sizeof(ELifetimeCondition) == 1,
                  "ELifetimeCondition ABI lock: must be 1 byte "
                  "(uint8-backed; matches FProperty::BlueprintReplicationCondition "
                  "@ offset 58 in §11.2)");
    static_assert(::std::is_same_v<::std::underlying_type_t<ELifetimeCondition>, ::uint8>,
                  "ELifetimeCondition ABI lock: underlying type must be uint8");

    // -----------------------------------------------------------------
    // Numeric-value locks (Section 11.4). The exact numeric encoding is
    // wire-locked; downstream consumers (XNetworking, XSerialization)
    // read these as raw bytes off the wire.
    // -----------------------------------------------------------------
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_None)               ==  0);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_InitialOnly)        ==  1);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_OwnerOnly)          ==  2);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_SkipOwner)          ==  3);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_SimulatedOnly)      ==  4);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_AutonomousOnly)     ==  5);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_SimulatedOrPhysics) ==  6);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_InitialOrOwner)     ==  7);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_Custom)             ==  8);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_ReplayOrOwner)      ==  9);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_ReplayOnly)         == 10);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_Never)              == 14);
    static_assert(static_cast<::uint8>(ELifetimeCondition::COND_Max)                == 15);

} // namespace XCore::Reflect
