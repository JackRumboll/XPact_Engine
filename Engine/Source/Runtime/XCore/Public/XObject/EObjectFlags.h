// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EObjectFlags.h -- XObject lifecycle / policy flag bitmask
// (XCoreXObject Rev 4 §2.3 + §2.3.1 UE RF_* mapping).
// =====================================================================
//
// XCoreXObject Rev 4 Section 2.3 ("EObjectFlags layout") + Section
// 2.3.1 ("UE RF_* flag mapping table").
//
// 32-bit bitmask of XObject lifecycle / policy state. The bits live in
// `XObject::ObjectFlags` (offset 32; std::atomic<uint32_t>) and are
// mutated via a CAS loop with `memory_order_acq_rel` on success +
// `memory_order_acquire` on failure (per §2.3). Multiple threads may
// set / clear flags concurrently (e.g., the loader sets NeedLoad, the
// GC sweep sets BeginDestroyed); the CAS loop ensures no flag is lost.
//
// Bit-position contract: PART of the Stage B addendum
// (XPACT_XOBJECT_LAYOUT_TAG; Contract Rev 13.9). A change to any bit
// position breaks every XHT-emitted `.gen.cpp` that hard-codes the
// expected flag values into its FXObjectLifecycleTable Capabilities
// bitmask probe. Bit positions are LOCKED.
//
// HOT-RELOAD COMMITMENT: bit positions are ABI-frozen; a patched DLL
// that adds a new flag must use the unreserved bits in the
// `_Reserved20`..`_Reserved23` slot range or the `UserFlag_1`..
// `UserFlag_8` plugin range (bits 24-31; per Rev 3 FIX-M-R2-2). The
// engine reserves bits 0-23 for engine-internal use.
//
// =====================================================================
// UE RF_* MAPPING SUMMARY (per spec §2.3.1; full table in spec)
// =====================================================================
//
// XPact COVERS the load-bearing UE EObjectFlags surface while
// DROPPING UE-historical accidents:
//
//   * RF_TagGarbageTemp  (debug-only; replaced by XInsights category).
//   * RF_KeepForCooker   (Cooker is UE-only; XPact uses XBT).
//   * RF_BeingRegenerated (Blueprint regeneration; deferred to XBlueprintVM).
//   * RF_NewerVersionExists (deferred to XLiveCoding).
//   * RF_Dynamic (deprecated in UE itself).
//   * RF_WillBeLoaded (UE loader-internal state; XSerialization owns
//                      its own loader state).
//   * RF_AllocatedInSharedPage (UE UObject-allocator-specific; XPact's
//                                FXObjectAllocator handles slab placement
//                                without an instance flag).
//   * RF_HasDynamicImports (UE 5.x experimental; deprecating upstream).
//   * RF_MirroredGarbage (unified into MarkedAsGarbage per FIX-A-HIGH-19;
//                          UE's dual-tracked mirror was a historical
//                          accident).
//   * RF_Transactional (UE transaction system; deferred to XUndo).
//   * RF_TextExportTransient (text export is editor-tool only; deferred).
//   * RF_NeedPostLoadSubobjects (UE-historical split; folded into
//                                  NeedPostLoad in XPact -- sub-objects
//                                  PostLoad in the same pass).
//   * RF_InheritableComponentTemplate (deferred to XComponent system).
//   * RF_StrongRefOnFrame (VM frame-local strong ref; deferred to
//                            XBlueprintVM).
//   * RF_DuplicateTransient (UE has separate flags for serialize-skip
//                              vs duplicate-skip; XPact unifies into
//                              Transient since Duplicate is a special
//                              case of serialize).
//
// XPact ADDS:
//
//   * MarkedAsGarbage (the unified runtime tomb-stoning flag per
//                       FIX-A-HIGH-19; consulted by GC sweep via
//                       EXGCOptions::kEliminateGarbageRefs).
//   * BeingReplaced / HotReloadReplaced (hot-reload swap state).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"      // XPACT_FORCEINLINE

#include <cstdint>          // ::uint32_t
#include <type_traits>      // ::std::underlying_type_t

namespace XCore
{

    // -----------------------------------------------------------------
    // EObjectFlags -- 32-bit lifecycle / policy bitmask.
    //
    // Per spec §2.3: strongly-typed enum class; underlying uint32_t.
    // Bit positions LOCKED at Contract Rev 13.9. The atomic carrier
    // lives at `XObject::ObjectFlags` (offset 32; std::atomic<uint32_t>);
    // operator& / operator| / operator~ provide the standard bitwise
    // surface for flag composition + masking.
    //
    // The enumerator naming follows the XPact convention (no `RF_`
    // prefix; the names are scoped via the enum class). The §2.3
    // spec body uses both the bare names (NeedInitialization,
    // ClassDefaultObject, ...) and the UE-faithful `RF_*` prefixed
    // names; we use the bare names because the enum class scope
    // already disambiguates. (The §2.3.1 mapping table documents the
    // UE-name correspondence for migration / cross-reference purposes.)
    // -----------------------------------------------------------------
    enum class EObjectFlags : ::std::uint32_t
    {
        None                       = 0,

        // ----- Allocation / lifecycle (bits 0-8) -----

        // Post-allocation, pre-PostInitProperties gate. The NewObject
        // path SETS this bit between AllocateRaw + PostInitProperties;
        // the FXObjectInitializer destructor (§8.4; lands at Phase 5.d)
        // CLEARS the bit and dispatches PostInitProperties via the
        // FClass FakeVTable.
        NeedInitialization         = 1u <<  0,

        // This object IS the Class Default Object for its FClass. The
        // CDO is constructed at FClass eager-init (Phase 5.g'+) and
        // serves as the source of default property values for every
        // subsequent NewObject<T> of the same class.
        ClassDefaultObject         = 1u <<  1,

        // This object is an archetype / template used as the source of
        // member defaults when constructing a derived instance. Matches
        // UE RF_ArchetypeObject (UE-faithful).
        ArchetypeObject            = 1u <<  2,

        // Do not serialize. Object is purely runtime state; the save
        // path skips it; the load path never instantiates it from
        // package data. Matches UE RF_Transient.
        Transient                  = 1u <<  3,

        // Serialized data has been encountered but not yet loaded. The
        // loader sets this bit before queuing the object's deserialize
        // pass; it is CLEARED when Serialize completes. While the bit
        // is set, the NewObject hot path skips FXObjectInitializer
        // construction (the loader takes responsibility for
        // PostInitProperties; Rev 3 per FIX-M-R2-4 + spec §3.5).
        NeedLoad                   = 1u <<  4,

        // PostLoad has not yet been called. Set by the loader once the
        // object's bytes are fully read; cleared once PostLoad
        // completes. The XObject graph PostLoad pass runs in
        // topological order across the entire load batch.
        NeedPostLoad               = 1u <<  5,

        // Object is a root-set member; GC never reclaims it. Matches
        // UE RF_Standalone semantics: an "I want to keep this alive
        // through every GC cycle until explicitly cleared" flag.
        // Mirrored by `MarkAsRootSet` (the spec body lists both names;
        // both bits map to the same root-pin semantic).
        Standalone                 = 1u <<  6,

        // BeginDestroy has been called. The instance is mostly still
        // valid but is en route to FinishDestroy. References to a
        // BeginDestroyed object are stale; consumers should treat as
        // pending teardown.
        BeginDestroyed             = 1u <<  7,

        // FinishDestroy has been called. The instance bytes are about
        // to be freed; the FXObjectArray slot is queued for SerialNumber
        // bump + reuse. No code should hold any reference to a
        // FinishDestroyed object.
        FinishDestroyed            = 1u <<  8,

        // ----- GC bits (9-12) -----

        // Pinned in the GC root set. Synonym of `Standalone` per spec
        // §2.3; the duplicate name preserves UE-faithful naming for
        // call sites that prefer the explicit "marked as root set"
        // wording. Both flags should be considered equivalent at the
        // collector mark phase.
        MarkAsRootSet              = 1u <<  9,

        // Object is constinit-allocated (.rodata / process-static
        // memory). The collector NEVER calls FinishDestroy on a
        // MarkAsNative object because the bytes are not heap-owned.
        // Matches UE RF_MarkAsNative.
        MarkAsNative               = 1u << 10,

        // CDO-template-spawned child object (sub-object instantiated
        // by CreateDefaultSubobject during the CDO's construction).
        // The save / duplicate paths special-case DefaultSubObject
        // instances to preserve the template relationship.
        DefaultSubObject           = 1u << 11,

        // Marked for runtime tomb-stoning (Rev 2 added per
        // FIX-A-HIGH-19). The next GC sweep with
        // EXGCOptions::kEliminateGarbageRefs set clears references TO
        // this object via the schema-vector walk (each ObjectRef slot
        // that points at a MarkedAsGarbage object is nulled). This is
        // the editor "delete an actor and have references null out
        // automatically" pattern; load-bearing for hot-reload tomb-
        // stoning.
        //
        // Set via `XObject::MarkAsGarbage()` (non-sim-path; guarded by
        // XPACT_CHECK_SL per spec §4.2 + Rev 3 FIX-M-R2-10 because the
        // next-GC-sweep timing is non-deterministic relative to the
        // sim-tick boundary).
        MarkedAsGarbage            = 1u << 12,

        // ----- Hot-reload (bits 13-14) -----

        // Hot-reload swap is in progress for this object's class. The
        // FXObjectArrayEntry's hot-reload-in-progress state-bit
        // mirrors this for the collector's read fast path. Set by
        // XLiveCoding before invoking the FClass swap; cleared once
        // the swap completes.
        BeingReplaced              = 1u << 13,

        // Post-swap: the instance was rebound to a new FClass*
        // (typically the patched class produced by XLiveCoding).
        // Diagnostic flag; subsystems that watch for hot-reload events
        // observe this transition.
        HotReloadReplaced          = 1u << 14,

        // ----- Loader / serialization (bits 15-18; Rev 2 per
        //       FIX-A-HIGH-18 / UE-MISS-4) -----

        // Visible across package boundaries. UE-faithful RF_Public:
        // a Public object can be referenced from another package's
        // serialized data; a non-Public object is package-local.
        Public                     = 1u << 15,

        // Object has an external (non-Outer-derived) Package. The
        // default packaging strategy is "the object's Package is its
        // root Outer's Package"; the HasExternalPackage flag signals
        // an explicit override (the object owns its own package file).
        HasExternalPackage         = 1u << 16,

        // Post-load is fully resolved: all referenced objects are
        // loaded + bound. The PostLoad pass sets this bit on each
        // object when it completes. Matches UE RF_LoadCompleted /
        // RF_WasLoaded (the two UE flags are unified here).
        LoadCompleted              = 1u << 17,

        // Transient in PIE (Play-In-Editor); persistent outside PIE.
        // The save path inspects this flag against the current PIE
        // context to decide whether to serialize. Matches UE
        // RF_NonPIEDuplicateTransient.
        NonPIETransient            = 1u << 18,

        // ----- Debug (bit 19) -----

        // Debug instrumentation marker. Set by debug tooling to flag
        // "this object already emitted a lifecycle-event debug-print"
        // so subsequent re-emissions are throttled. Cleared by the
        // debug tool when it resets the trace state.
        DebugLogged                = 1u << 19,

        // ----- Reserved engine slots (bits 20-23) -----

        // Reserved for future engine extensions. New engine-internal
        // flags should consume from this range FIRST (preserving the
        // plugin user range at bits 24-31). The reserved slots are
        // declared explicitly so the bit positions are visibly locked.

        _Reserved20                = 1u << 20,
        _Reserved21                = 1u << 21,
        _Reserved22                = 1u << 22,
        _Reserved23                = 1u << 23,

        // ----- User-plugin range (bits 24-31; Rev 3 expanded per
        //       FIX-M-R2-2) -----
        //
        // Plugin / user-policy flags. These bits are NEVER assigned a
        // semantic by the engine and are guaranteed-stable for plugin
        // use across hot-reload. A plugin claiming a UserFlag bit
        // SHOULD document its claim in the plugin's manifest so other
        // plugins do not collide on the same bit.
        //
        // The aliases UserFlag_Begin / UserFlag_End mark the inclusive
        // boundaries of the plugin range; iteration helpers can use
        // them to walk the plugin-reserved slot range.

        UserFlag_Begin             = 1u << 24,
        UserFlag_1                 = 1u << 24,
        UserFlag_2                 = 1u << 25,
        UserFlag_3                 = 1u << 26,
        UserFlag_4                 = 1u << 27,
        UserFlag_5                 = 1u << 28,
        UserFlag_6                 = 1u << 29,
        UserFlag_7                 = 1u << 30,
        UserFlag_8                 = 1u << 31,
        UserFlag_End               = 1u << 31,
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface (constexpr; per the XPact strongly-typed-
    // enum convention shared with EClassFlags / EClassCastFlags /
    // EPropertyFlags / EStructFlags).
    //
    // Returning EObjectFlags from the bitwise operators preserves the
    // enum-class type at the call site; comparison against the integer
    // 0 should use `Flags != EObjectFlags::None` rather than `Flags !=
    // 0` to keep the type discipline intact.
    //
    // The HasAnyFlags / HasAllFlags free-function helpers expose the
    // predicate sugar used at every flag-check site:
    //
    //   HasAnyFlags(Flags, EObjectFlags::Transient | EObjectFlags::Public)
    //     -- true iff at least one of Transient / Public is set.
    //   HasAllFlags(Flags, EObjectFlags::Transient | EObjectFlags::Public)
    //     -- true iff BOTH Transient AND Public are set.
    //
    // (The XObject member predicates `XObject::HasAnyFlags` /
    // `XObject::HasAllFlags` route through these helpers after loading
    // ObjectFlags atomically.)
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr EObjectFlags
        operator|(EObjectFlags Lhs, EObjectFlags Rhs) noexcept
    {
        using U = ::std::underlying_type_t<EObjectFlags>;
        return static_cast<EObjectFlags>(
            static_cast<U>(Lhs) | static_cast<U>(Rhs));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr EObjectFlags
        operator&(EObjectFlags Lhs, EObjectFlags Rhs) noexcept
    {
        using U = ::std::underlying_type_t<EObjectFlags>;
        return static_cast<EObjectFlags>(
            static_cast<U>(Lhs) & static_cast<U>(Rhs));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr EObjectFlags
        operator^(EObjectFlags Lhs, EObjectFlags Rhs) noexcept
    {
        using U = ::std::underlying_type_t<EObjectFlags>;
        return static_cast<EObjectFlags>(
            static_cast<U>(Lhs) ^ static_cast<U>(Rhs));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr EObjectFlags
        operator~(EObjectFlags Value) noexcept
    {
        using U = ::std::underlying_type_t<EObjectFlags>;
        return static_cast<EObjectFlags>(~static_cast<U>(Value));
    }

    XPACT_FORCEINLINE constexpr EObjectFlags&
        operator|=(EObjectFlags& Lhs, EObjectFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    XPACT_FORCEINLINE constexpr EObjectFlags&
        operator&=(EObjectFlags& Lhs, EObjectFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    XPACT_FORCEINLINE constexpr EObjectFlags&
        operator^=(EObjectFlags& Lhs, EObjectFlags Rhs) noexcept
    {
        Lhs = Lhs ^ Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // HasAnyFlags / HasAllFlags -- free-function predicates for the
    // strongly-typed enum class. Header-inline + constexpr so the call
    // sites are zero-cost.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool
        HasAnyObjectFlags(EObjectFlags Bits, EObjectFlags Mask) noexcept
    {
        return (Bits & Mask) != EObjectFlags::None;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool
        HasAllObjectFlags(EObjectFlags Bits, EObjectFlags Mask) noexcept
    {
        return (Bits & Mask) == Mask;
    }

    // -----------------------------------------------------------------
    // ToUnderlying -- explicit conversion to the underlying uint32_t.
    // Used at the XObject::ObjectFlags atomic boundary (the atomic
    // wraps uint32_t directly; per-flag operations encode as uint32_t
    // masks before the CAS).
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE constexpr ::std::uint32_t
        ToUnderlying(EObjectFlags Value) noexcept
    {
        return static_cast<::std::uint32_t>(Value);
    }

} // namespace XCore
