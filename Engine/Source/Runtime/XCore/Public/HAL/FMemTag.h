// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMemTag.h -- mandatory allocation tag (Section 4.1, fix B-M1).
// =====================================================================
//
// XCore-4a Rev 3, Section 4.1.
//
// FMemTag is the always-on attribution carrier for every FMemory::Malloc
// in the engine. UE's HAL/UnrealMemory.h:91 exposes FMemory::Malloc
// without a tag; UE's LLM (LowLevelMemTracker) is an opt-in retrofit
// that is routinely missing on hot paths. XPact's Prime Directive
// (engineering_principles.md) directs us to take the right path even
// if more expensive: every allocation carries a 2-byte tag, the
// allocator's per-block header carries the tag, and GetAllocatedBytes
// returns the per-tag total without an opt-in mechanism.
//
// Cost (Phase 1g Round 2): +0 bytes per block intra-block overhead.
// The Tag lives in an out-of-band uint16 side-table indexed by block
// number, stored at the head of each per-bin VM range. The side-
// table adds ~2 bytes per block in pool metadata but does not eat
// into the user-visible block size. The PoolIndexFromPtr swap
// (FMallocBinnedX.cpp + .h) recovers the BinIndex by binary search
// over the sorted PoolMetadataTable rather than by reading an
// intra-block header; the user pointer returned by Malloc is the
// block start itself, with no header offset.
//
// The enum is uint16-backed so each tag fits in exactly 2 bytes, with
// 16 reserved low slots (0x0000-0x000F) for engine-internal tags,
// 16 KiB of reserved-for-future-engine slots (0x0010-0x3FFF), 16 KiB
// of plugin slots (0x4000-0x7FFF), and 32 KiB of user/content-tier
// slots (0x8000-0xFFFF). The layout is intentionally generous; a
// user-tier module wanting per-system attribution can assign 100s of
// distinct tags without colliding with the engine.
//
// FMemTag::Generic (= 0) is the unattributed-allocation sentinel. The
// spec body (Section 4.1) names it "banned in Shipping by linker
// check"; XBT enforces this by scanning the final link's symbol table
// for any reference to FMemTag::Generic in Shipping-configuration
// builds. The check fires a build error if any Shipping TU still
// passes Generic. (TODO(Phase 1c): land the XBT scanner.)
//
// ABI lock: sizeof(FMemTag) == 2 (uint16_t).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // FMemTag -- the mandatory per-allocation attribution tag.
    //
    // Tag rules:
    //   * 0x0000 (Generic) -- unattributed; BANNED in Shipping by XBT
    //     linker check (TODO(Phase 1c): land the scanner). The slot
    //     stays as a runtime-debugging tag for ad-hoc Dev experiments
    //     where the developer wants to bisect "did this allocation
    //     show up?" without inventing a one-off tag.
    //   * 0x0001..0x000F -- engine subsystem tags (Container, Math,
    //     Platform, Threading, CVar, Stat, Localization,
    //     LeakTracker). Each subsystem owns one tag; sub-subsystems
    //     refine via the per-tag callstack inside FLeakTracker
    //     (Section 12) rather than burning more tag slots.
    //   * 0x0010..0x3FFF -- reserved-for-future-engine-systems.
    //     Includes XObject (XCore-4b GC heap) at the low end of this
    //     reservation block.
    //   * 0x4000 (Plugin_Begin) -- start of plugin-tier tag space.
    //     Plugins declare their tags via XPACT_DECLARE_MEMTAG (Phase
    //     1c) to claim a slot in this range; the XBT scanner ensures
    //     no two plugins claim the same slot.
    //   * 0x8000 (User_Begin) -- start of user/content-tier tag space.
    //     User modules use the same XPACT_DECLARE_MEMTAG mechanism.
    //
    // The enum is fixed-underlying-type uint16; static_assert below
    // pins sizeof at 2 bytes.
    // -----------------------------------------------------------------

    enum class FMemTag : ::uint16
    {
        // ---------------- Generic / unattributed ----------------
        // Banned in Shipping by XBT linker check (TODO(Phase 1c)).
        Generic       = 0,

        // ---------------- Engine subsystem tags ----------------
        Container     = 1,   // TArray, TMap, TSet, TBitArray, TStaticArray
        Math          = 2,   // FVector, FMatrix, FTransform, FQuat, etc.
        Platform      = 3,   // FPlatformMisc, FPlatformProcess, etc.
        Threading     = 4,   // FCriticalSection, FRWLock, TSpscQueue, TMpscQueue
        CVar          = 5,   // IConsoleManager, FAutoConsoleVariable
        Stat          = 6,   // FStatId, FStatRegistry, per-thread shard
        Localization  = 7,   // FText, FLocalizationManager, loctables
        LeakTracker   = 8,   // FLeakTracker internal allocations (must be
                             //   immune to itself -- the tracker's own
                             //   shadow-table allocs cannot recurse)

        // ---------------- Reserved (XObject) ----------------
        XObject       = 9,   // XCore-4b GC heap; reserved here so the
                             //   tag is stable from day one. XCore-4a's
                             //   allocator does NOT itself produce this
                             //   tag; the XCore-4b XObject heap routes
                             //   through s_XObjectAllocator (Section 4.4)
                             //   and tags its allocations XObject.

        // ---------------- XCore-4b Reflection ----------------
        // FName intern table, FField/FProperty descriptors, FClass/FStruct
        // metadata, XReflectionRuntime registry. Per XCore-4b Rev 3 Section 3
        // ("the FName intern table allocates from a dedicated allocator-
        // backed shard pool FMemory::Malloc(size, FMemTag::Reflection) using
        // the XCore-4a Phase-1g allocator at EInitPhase::PreStaticInit").
        // Reserved slot 10 within the 16-engine-slot space (kMemTagEngineSlotCount).
        Reflection    = 10,

        // ---------------- Plugin slot range (16 KiB) ----------------
        Plugin_Begin  = 0x4000,

        // ---------------- User/content slot range (32 KiB) ----------------
        User_Begin    = 0x8000,
    };

    // -----------------------------------------------------------------
    // ABI lock. The 2-byte width is load-bearing for the allocator's
    // out-of-band TagSideTable element width (Phase 1g Round 2 swapped
    // the per-allocation FBlockHeader for a per-pool TagSideTable that
    // stores one uint16 tag per block; see FMallocBinnedX.h:272
    // FPoolMetadata layout). A wider tag would inflate every pool's
    // metadata footprint by the same multiplier; a narrower tag would
    // not fit Plugin_Begin (0x4000) and User_Begin (0x8000). The
    // Phase 1g Round 3 errata sweep replaced the prior wording that
    // cited the now-removed FBlockHeader.
    // -----------------------------------------------------------------
    static_assert(sizeof(FMemTag) == 2,
                  "FMemTag ABI lock: must be exactly 2 bytes (uint16 underlying)");

    static_assert(alignof(FMemTag) == 2,
                  "FMemTag ABI lock: must be 2-byte aligned");

    // -----------------------------------------------------------------
    // Tag-name helper (Dev-only diagnostic).
    //
    // Returns a static literal for a known tag, or "Unknown(<index>)"
    // for unrecognised tags. Used by FMemory::DumpUsageReport and by
    // FLeakTracker::WriteReport for human-readable output. The helper
    // is inline so it has zero cost in builds that do not call it
    // (the diagnostic surfaces are dev-only; Shipping never invokes
    // them).
    //
    // For Plugin_Begin / User_Begin slot ranges the returned name is
    // "Plugin(<offset>)" / "User(<offset>)"; the per-plugin /
    // per-user-module string-name resolution requires the
    // XPACT_DECLARE_MEMTAG registration which lands in Phase 1c.
    //
    // The function is declared inline + constexpr so the compiler can
    // resolve known-tag cases at compile time when used in
    // constant-expression contexts (e.g., static_assert).
    // -----------------------------------------------------------------
    [[nodiscard]] inline constexpr const char* GetMemTagName(FMemTag Tag) noexcept
    {
        switch (Tag)
        {
            case FMemTag::Generic:      return "Generic";
            case FMemTag::Container:    return "Container";
            case FMemTag::Math:         return "Math";
            case FMemTag::Platform:     return "Platform";
            case FMemTag::Threading:    return "Threading";
            case FMemTag::CVar:         return "CVar";
            case FMemTag::Stat:         return "Stat";
            case FMemTag::Localization: return "Localization";
            case FMemTag::LeakTracker:  return "LeakTracker";
            case FMemTag::XObject:      return "XObject";
            case FMemTag::Reflection:   return "Reflection";
            default:
                // Plugin / User / unknown ranges return a generic label;
                // the per-tag resolution lives in the (Phase 1c)
                // XPACT_DECLARE_MEMTAG registry.
                return "Unknown";
        }
    }

    // -----------------------------------------------------------------
    // Number of engine-fixed tag slots. Used by the allocator's
    // per-tag accounting array to size its O(1) lookup table. Plugin
    // and User slots are sparse and use a secondary lookup (Phase 1c).
    //
    // Sized as 16 to give the engine room to grow without immediately
    // bumping the array; current engine population is 10 (Generic
    // through XObject inclusive).
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kMemTagEngineSlotCount = 16;

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1c):
//   * XPACT_DECLARE_MEMTAG macro for plugins / user modules to claim
//     slots in [Plugin_Begin, User_Begin) and [User_Begin, 0xFFFF).
//   * XBT linker-scan rule banning FMemTag::Generic in Shipping
//     builds (acceptance per Section 4.1 / Section 17.1 A1 wording
//     "banned in Shipping by linker check").
//   * Per-plugin / per-user tag-name resolution table for
//     GetMemTagName so plugin-tier tags resolve to a readable name in
//     FLeakTracker reports.
// =====================================================================
