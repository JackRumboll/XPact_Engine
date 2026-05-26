// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FCppStructOpsFakeVTable.h -- per-FScriptStruct-subtype dispatch table
// (XCore-4b §7.2 + §11.3; FIX-R2-MAJ-2 + FIX-R2-MED-1).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.2 ("FScriptStruct: adopt FakeVTable pattern
// + expand Capabilities to 32 bits") + Section 11.3 byte layout row
// `FCppStructOpsFakeVTable: 136 per FScriptStruct subtype in .rodata`.
//
// FCppStructOpsFakeVTable is the per-FScriptStruct-subtype dispatch
// table that replaces UE's `UScriptStruct::ICppStructOps` virtual
// interface (`Class.h:1774`). Each reflectable script struct subtype
// (FVector, FRotator, FTransform, FLinearColor, custom user structs)
// has exactly one FCppStructOpsFakeVTable constinit instance in
// `.rodata`; every FScriptStruct instance carries an 8-byte pointer
// to its subtype's table at offset 112 (relative to the start of
// FScriptStruct).
//
// HOT-RELOAD SAFETY (§7.2):
//
//   UE's `UScriptStruct::ICppStructOps` uses a virtual interface with
//   ~27 overridable methods. A patch DLL's struct ops vtable must
//   match the runtime's exactly OR the runtime patches the patch
//   DLL's vtable bytes in-place; both paths are fragile.
//
//   XPact's pattern eliminates the vtable. Each subtype's
//   FCppStructOpsFakeVTable lives in .rodata, addressed by the
//   FScriptStruct's `CppOpsTable` slot; a patch DLL compiled against
//   the same XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG produces a
//   binary-compatible table.
//
// XPACT-NOVEL DIVERGENCE (FIX-R2-MED-2):
//
//   UE applies the TStructOpsFakeVTable mechanism ONLY to
//   `UScriptStruct::ICppStructOps` (`Class.h:1737-1768`), not to
//   FProperty. XPact applies the FakeVTable pattern to BOTH FProperty
//   dispatch (per FFakeVTable.h; FIX-2) AND FScriptStruct ICppStructOps
//   (here; FIX-R2-MAJ-2). The broader application is deliberate per
//   Prime Directive: avoid the hot-reload vtable-mismatch class that
//   UE's FProperty virtuals introduce.
//
// LAYOUT (locked at 136 bytes per Contract Rev 13.8 §11.3):
//
//   struct alignas(8) FCppStructOpsFakeVTable {
//       uint32 Capabilities;           //  0  +4    bit N => handler N populated
//       uint32 _reservedHeader;        //  4  +4    pad to 8-byte slot boundary
//       void(*Slots[16])(void);        //  8  +128  sixteen 8-byte handler pointers
//   };
//
//   sizeof(FCppStructOpsFakeVTable) == 136 = 8-byte header + 16 * 8.
//
// XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG (Rev 3 §11.6):
//   "FCppStructOpsFakeVTable-v1: 8-byte header (32-bit Capabilities +
//    _reservedHeader) + 16 handler slots (8 bytes each) = 136 bytes per
//    FScriptStruct subtype in .rodata"
//
// CAPABILITY BITMASK (Rev 3 Round 2 audit FIX-R2-HIGH-NEW-2: 31 active +
// 1 reserved; previously 26 active + 6 reserved):
//
//   The 32-bit Capabilities mask declares which dispatch slots are
//   populated AND which capability-only properties hold for the type
//   (some bits carry information without a corresponding callable
//   handler, e.g. IsPlainOldData). The bit-position layout is locked
//   in the Stage B addendum:
//
//     Bit  Name                                       Slot? (yes/no)
//     ---  -----------------------------------------  --------------
//     0    HasNoOpConstructor                         no  (capability-only)
//     1    HasZeroConstructor                         no  (capability-only)
//     2    HasDestructor                              no  (capability-only)
//     3    HasPostScriptConstruct                     yes (slot 0)
//     4    HasSerializer                              yes (Layer 9 routes)
//     5    HasIdentical                               no  (capability-only)
//     6    HasNetSerializer                           yes (slot 3)
//     7    HasNetDeltaSerializer                      yes (slot 4)
//     8    HasAddStructReferencedObjects              yes (slot 1)
//     9    HasGetTypeHash                             yes (slot 2)
//     10   HasSerializeFromMismatchedTag              yes (slot 8)
//     11   HasPostSerialize                           yes (slot 5)
//     12   HasExportTextItem                          yes (slot 6)
//     13   HasImportTextItem                          yes (slot 7)
//     14   HasCopy                                    yes (slot 11)
//     15   HasMoveAssign                              yes (slot 12)
//     16   IsPlainOldData                             no  (capability-only)
//     17   IsUECoreType                               no  (capability-only)
//     18   IsUECoreVariant                            no  (capability-only)
//     19   HasGetPreloadDependencies                  yes (slot 15)
//     20   HasFindInnerPropertyInstance               yes (slot 14)
//     21   HasVisitor                                 yes (slot 13)
//     22   ClearOnFinishDestroy                       no  (capability-only)
//     23   HasIntrusiveUnsetOptionalState             no  (capability-only; FOptional)
//     24   IsBlueprintBase                            no  (capability-only; XPact addition)
//     25   IsNative                                   no  (capability-only; XPact addition)
//     26   HasStructuredSerializer                    yes (slot 9; UE-parity per FIX-R2-HIGH-NEW-2)
//     27   HasNetSharedSerialization                  no  (capability-only; UE-parity)
//     28   HasStructuredSerializeFromMismatchedTag    yes (slot 10; UE-parity per FIX-R2-HIGH-NEW-2)
//     29   IsAbstract                                 no  (capability-only; UE-parity)
//     30   IsIntrusiveOptionalSafeForGC               no  (capability-only; UE-parity FOptional+GC)
//     31   RESERVED (1 bit for post-MVP additions)
//
//   Round 2 audit note (FIX-R2-HIGH-NEW-2): the prior comment claimed
//   IsBlueprintBase and IsNative were UE-parity additions; this is
//   incorrect. UE keeps both in `StructFlags & STRUCT_BlueprintBase`
//   (per `UScriptStruct::StructFlags`) and `EClassFlags & CLASS_Native`
//   respectively, not in the `FCapabilities` mask. The XPact bits at
//   24-25 are deliberate XPACT-NOVEL additions that fold the two flags
//   into the unified capability mask (one bit-test instead of one
//   StructFlags-load + one mask-and). The five new bits at 26-30 ARE
//   UE-parity additions to close gaps the prior revision missed.
//
//   ABI bump note: the FCapabilities mask is now 31 active bits with 1
//   reserved. Future expansion past 32 capabilities requires widening
//   the underlying type from uint32 to uint64; this is a Phase 2+ ABI
//   bump (the XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG would change
//   from v1 to v2). TODO(Phase 2): consider widening to uint64 with a
//   reserved-bits expansion to 64 to allow ~30 more capability bits
//   for the post-MVP expansion (e.g., serialisation-format dialect
//   bits, editor-only handler categories).
//
// SLOT LAYOUT (16 fixed slots; subset of capability bits):
//
//   Slot 0  PostScriptConstruct
//   Slot 1  AddStructReferencedObjects (GC delegation)
//   Slot 2  GetTypeHash
//   Slot 3  NetSerialize (Iris-relevant; XNetworking consumes)
//   Slot 4  NetDeltaSerialize (FastArraySerializer pattern)
//   Slot 5  PostSerialize
//   Slot 6  ExportTextItem
//   Slot 7  ImportTextItem
//   Slot 8  SerializeFromMismatchedTag (FCustomVersion migration)
//   Slot 9  StructuredSerialize (UE-parity; slot active per FIX-R2-HIGH-NEW-2)
//   Slot 10 StructuredSerializeFromMismatchedTag (UE-parity; slot active per FIX-R2-HIGH-NEW-2)
//   Slot 11 Copy (declared bit; handler reserved)
//   Slot 12 MoveAssign (declared bit; handler reserved)
//   Slot 13 Visitor (editor PropertyVisitor; reserved)
//   Slot 14 FindInnerPropertyInstance (editor PropertyPath; reserved)
//   Slot 15 GetPreloadDependencies (asset cooking; reserved)
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cstddef>      // offsetof
#include <type_traits>  // is_standard_layout etc.

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // ECppOpSlot -- compile-time enumeration of the 16 dispatch slots.
    //
    // The slot ordering is LOCKED at the Stage B addendum
    // (XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG); changing the order
    // ABI-breaks every FCppStructOpsFakeVTable constinit instance
    // engine-wide.
    //
    // Slot indices are 0-based; ECppOpSlot::Count == 16 is the sentinel
    // (NOT a real slot). Slot bodies are referenced via the GetSlot<T>
    // accessor on FCppStructOpsFakeVTable.
    // -----------------------------------------------------------------
    enum class ECppOpSlot : ::uint32
    {
        PostScriptConstruct                  = 0,
        AddStructReferencedObjects           = 1,
        GetTypeHash                          = 2,
        NetSerialize                         = 3,
        NetDeltaSerialize                    = 4,
        PostSerialize                        = 5,
        ExportTextItem                       = 6,
        ImportTextItem                       = 7,
        SerializeFromMismatchedTag           = 8,
        StructuredSerialize                  = 9,   // post-MVP slot (reserved)
        StructuredSerializeFromMismatchedTag = 10,  // post-MVP slot (reserved)
        Copy                                 = 11,  // declared capability bit; handler reserved
        MoveAssign                           = 12,  // declared capability bit; handler reserved
        Visitor                              = 13,  // editor PropertyVisitor; reserved
        FindInnerPropertyInstance            = 14,  // editor PropertyPath; reserved
        GetPreloadDependencies               = 15,  // asset cooking; reserved

        Count                                = 16,  // sentinel; NOT a real slot
    };

    static_assert(static_cast<::uint32>(ECppOpSlot::Count) == 16,
                  "FCppStructOpsFakeVTable slot count locked at 16 (Rev 3 FIX-R2-MAJ-2)");

    // -----------------------------------------------------------------
    // ECppStructOpsCapability -- 32-bit capability bitmask layout.
    //
    // Per Rev 3 Round 2 audit FIX-R2-HIGH-NEW-2: 31 active bits + 1
    // reserved (previously 26 active + 6 reserved). Each bit represents
    // either a populated dispatch slot OR a capability-only declaration
    // (e.g., IsPlainOldData, HasNoOpConstructor) that callers consult
    // without dispatching through a slot.
    //
    // The bit positions are LOCKED in the Stage B addendum and any
    // change requires a Contract Rev 13.9+ bump.
    //
    // Capability-only bits (no corresponding slot in Slots[]):
    //   HasNoOpConstructor / HasZeroConstructor / HasDestructor /
    //   HasIdentical / IsPlainOldData / IsUECoreType / IsUECoreVariant /
    //   ClearOnFinishDestroy / HasIntrusiveUnsetOptionalState /
    //   IsBlueprintBase / IsNative / HasNetSharedSerialization /
    //   IsAbstract / IsIntrusiveOptionalSafeForGC.
    //
    // Slot-bearing bits (Slots[] index recorded in the comment per row):
    //   HasPostScriptConstruct                  -> Slot 0
    //   HasSerializer                           -> Layer 9 routes
    //   HasNetSerializer                        -> Slot 3
    //   HasNetDeltaSerializer                   -> Slot 4
    //   HasAddStructReferencedObjects           -> Slot 1
    //   HasGetTypeHash                          -> Slot 2
    //   HasSerializeFromMismatchedTag           -> Slot 8
    //   HasPostSerialize                        -> Slot 5
    //   HasExportTextItem                       -> Slot 6
    //   HasImportTextItem                       -> Slot 7
    //   HasCopy                                 -> Slot 11
    //   HasMoveAssign                           -> Slot 12
    //   HasGetPreloadDependencies               -> Slot 15
    //   HasFindInnerPropertyInstance            -> Slot 14
    //   HasVisitor                              -> Slot 13
    //   HasStructuredSerializer                 -> Slot 9  (Round 2 FIX)
    //   HasStructuredSerializeFromMismatchedTag -> Slot 10 (Round 2 FIX)
    // -----------------------------------------------------------------
    enum class ECppStructOpsCapability : ::uint32
    {
        kNone                              = 0U,

        // Capability-only (no callable slot).
        HasNoOpConstructor                 = 1U << 0,
        HasZeroConstructor                 = 1U << 1,
        HasDestructor                      = 1U << 2,

        // Slot-bearing (each maps to a Slots[] index documented above).
        HasPostScriptConstruct             = 1U << 3,
        HasSerializer                      = 1U << 4,

        // Capability-only (value-equality declaration; no slot).
        HasIdentical                       = 1U << 5,

        // Slot-bearing.
        HasNetSerializer                   = 1U << 6,
        HasNetDeltaSerializer              = 1U << 7,
        HasAddStructReferencedObjects      = 1U << 8,
        HasGetTypeHash                     = 1U << 9,
        HasSerializeFromMismatchedTag      = 1U << 10,
        HasPostSerialize                   = 1U << 11,
        HasExportTextItem                  = 1U << 12,
        HasImportTextItem                  = 1U << 13,
        HasCopy                            = 1U << 14,
        HasMoveAssign                      = 1U << 15,

        // Capability-only (codegen / packing declarations; no slot).
        IsPlainOldData                     = 1U << 16,
        IsUECoreType                       = 1U << 17,
        IsUECoreVariant                    = 1U << 18,

        // Slot-bearing.
        HasGetPreloadDependencies          = 1U << 19,
        HasFindInnerPropertyInstance       = 1U << 20,
        HasVisitor                         = 1U << 21,

        // Capability-only.
        ClearOnFinishDestroy               = 1U << 22,
        HasIntrusiveUnsetOptionalState     = 1U << 23,

        // -------------------------------------------------------------
        // XPACT-NOVEL additions (bits 24-25; Rev 3 Round 2 audit
        // correction per FIX-R2-HIGH-NEW-2).
        //
        // Per the audit: UE does NOT keep IsBlueprintBase or IsNative
        // in `FCapabilities`; it stores them in
        // `UScriptStruct::StructFlags & STRUCT_BlueprintBase` and
        // `EClassFlags & CLASS_Native` respectively
        // (Engine/Source/Runtime/CoreUObject/Public/UObject/Class.h).
        // The prior revision's comment claiming "UE-parity" was
        // incorrect.
        //
        // XPact folds both into the unified capability mask as a
        // deliberate divergence -- a single bit-test (mask & bit) is
        // simpler than a separate StructFlags-load + mask-and. The
        // value is set at constinit time per FScriptStruct subtype by
        // the XHT-emitted .gen.cpp.
        // -------------------------------------------------------------

        // IsBlueprintBase -- struct is permitted to serve as a Blueprint
        // derivable base type. Drives the Blueprint compiler's struct-
        // derivation rules (a Blueprint can only derive from a struct
        // that returns true). Default per type: false; explicit
        // USTRUCT(BlueprintType) sets the bit.
        IsBlueprintBase                    = 1U << 24,

        // IsNative -- struct is a native C++ type (defined in C++
        // source via USTRUCT()), as opposed to a Blueprint-generated
        // struct (defined in a Blueprint asset's user-defined struct).
        // Default per type: true for C++-emitted XHT-generated tables;
        // false for Blueprint-emitted tables.
        IsNative                           = 1U << 25,

        // -------------------------------------------------------------
        // Rev 3 Round 2 audit FIX-R2-HIGH-NEW-2: 5 missing UE-parity
        // capability bits.
        //
        // The prior revision (Rev 2 FIX-4) added IsBlueprintBase + IsNative
        // claiming UE-parity, but the audit found those are NOT in UE's
        // FCapabilities and ALSO that 5 genuine UE-parity bits were
        // missing entirely. The 5 bits below close that gap. Two of
        // them (HasStructuredSerializer, HasStructuredSerializeFromMismatchedTag)
        // are slot-bearing and bind to existing Slots[9] and Slots[10]
        // (which the prior revision marked "RESERVED post-MVP" but for
        // which the slot infrastructure was already in place).
        // -------------------------------------------------------------

        // HasStructuredSerializer -- bind to Slots[9] (StructuredSerialize).
        // UE-equivalent: `UScriptStruct::ICppStructOps::HasStructuredSerializer`.
        // Indicates the struct provides a structured-archive serializer
        // (FStructuredArchive); replaces or supplements the legacy
        // FArchive serializer (slot 4 'HasSerializer'). Structured
        // serialization is the path forward for editor-side cooking
        // determinism.
        HasStructuredSerializer                  = 1U << 26,

        // HasNetSharedSerialization -- capability-only flag.
        // UE-equivalent: `UScriptStruct::ICppStructOps::HasNetSharedSerialization`.
        // Indicates the struct's NetSerialize handler produces output
        // that is identical for every recipient (no per-receiver
        // adaptation). Enables replication-system bandwidth optimisation
        // (serialise once, broadcast). The XNetworking subsystem reads
        // this bit when batching net updates.
        HasNetSharedSerialization                = 1U << 27,

        // HasStructuredSerializeFromMismatchedTag -- bind to Slots[10]
        // (StructuredSerializeFromMismatchedTag).
        // UE-equivalent: `UScriptStruct::ICppStructOps::HasStructuredSerializeFromMismatchedTag`.
        // Structured-archive variant of HasSerializeFromMismatchedTag
        // (slot 8). Used for FCustomVersion-driven migrations when the
        // archive is structured rather than legacy FArchive.
        HasStructuredSerializeFromMismatchedTag  = 1U << 28,

        // IsAbstract -- capability-only flag.
        // UE-equivalent: `UScriptStruct::StructFlags & STRUCT_Abstract`,
        // folded into the unified capability mask per XPact convention
        // (same reasoning as IsBlueprintBase / IsNative). Indicates the
        // struct cannot be instantiated directly; only derived types
        // may instantiate. The reflection registrar reads this bit to
        // gate Add-via-FScriptStruct paths.
        IsAbstract                               = 1U << 29,

        // IsIntrusiveOptionalSafeForGC -- capability-only flag.
        // UE-equivalent: `UScriptStruct::ICppStructOps::IsIntrusiveOptionalSafeForGC`.
        // Indicates the struct's intrusive-FOptional unset state
        // (HasIntrusiveUnsetOptionalState at bit 23) does NOT produce
        // a value that the GC walker could misinterpret as a live
        // XObject pointer. Required when an FOptional<T> field carries
        // an XObject-pointer-bearing T; the unset state must be
        // GC-safe (typically zero-initialised) or the collector would
        // chase the stale bytes.
        IsIntrusiveOptionalSafeForGC             = 1U << 30,

        // Bit 31 reserved for one post-MVP capability addition. After
        // that point, the uint32-backed mask is exhausted; further
        // expansion requires widening to uint64 (Phase 2+ ABI bump).
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface for ECppStructOpsCapability.
    //
    // Strongly-typed enum classes do not implicitly support bitwise
    // composition; we provide the operators explicitly so callers can
    // write `Caps1 | Caps2` without static_cast at every site.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr ECppStructOpsCapability operator|(
        ECppStructOpsCapability Lhs, ECppStructOpsCapability Rhs) noexcept
    {
        return static_cast<ECppStructOpsCapability>(
            static_cast<::uint32>(Lhs) | static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr ECppStructOpsCapability operator&(
        ECppStructOpsCapability Lhs, ECppStructOpsCapability Rhs) noexcept
    {
        return static_cast<ECppStructOpsCapability>(
            static_cast<::uint32>(Lhs) & static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr ECppStructOpsCapability operator~(
        ECppStructOpsCapability Operand) noexcept
    {
        return static_cast<ECppStructOpsCapability>(
            ~static_cast<::uint32>(Operand));
    }

    constexpr ECppStructOpsCapability& operator|=(
        ECppStructOpsCapability& Lhs, ECppStructOpsCapability Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr ECppStructOpsCapability& operator&=(
        ECppStructOpsCapability& Lhs, ECppStructOpsCapability Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock for ECppStructOpsCapability.
    // -----------------------------------------------------------------
    static_assert(sizeof(ECppStructOpsCapability) == 4,
                  "ECppStructOpsCapability ABI lock: underlying type must be uint32 "
                  "(4 bytes; matches FCppStructOpsFakeVTable::Capabilities slot)");
    static_assert(::std::is_same_v<::std::underlying_type_t<ECppStructOpsCapability>, ::uint32>,
                  "ECppStructOpsCapability ABI lock: underlying type must be uint32");

    // -----------------------------------------------------------------
    // CapabilityBit / SlotBit -- bit-pattern helpers.
    //
    // CapabilityBit(ECppStructOpsCapability::X) returns the single-bit
    // pattern for that capability. Used in constinit Capabilities-mask
    // composition.
    //
    // SlotBit(ECppOpSlot::X) returns 1U << X for the slot-presence
    // bitmask the spec optionally tracks alongside Capabilities; the
    // FCppStructOpsFakeVTable's Capabilities mask is the authoritative
    // source of truth for "is this slot callable".
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint32 CapabilityBit(
        ECppStructOpsCapability Cap) noexcept
    {
        return static_cast<::uint32>(Cap);
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint32 CppOpSlotBit(
        ECppOpSlot Slot) noexcept
    {
        return ::uint32(1) << static_cast<::uint32>(Slot);
    }

    // -----------------------------------------------------------------
    // FCppStructOpsFakeVTable -- the 136-byte per-FScriptStruct-subtype
    // dispatch table.
    //
    // Per spec §7.2 / §11.3: alignas(8). NO virtual methods (hot-reload
    // safety).
    //
    // The Capabilities bitmask declares which Slots are populated AND
    // which capability-only properties hold. Callers SHOULD probe via
    // `HasCapability(ECppStructOpsCapability::X)` before dispatching
    // through `Slots[ECppOpSlot::X]` to avoid null-deref on unsupported
    // operations.
    //
    // The function pointers are stored as `void(*)(void)` (type-erased)
    // and cast at the call site via `GetSlot<T>()`. The type erasure is
    // necessary because the 16 slots have distinct signatures; a union
    // of function-pointer types would not be standard-layout. The
    // type-erased shape is the canonical pattern used by every engine
    // that ships a FakeVTable.
    // -----------------------------------------------------------------
    struct alignas(8) FCppStructOpsFakeVTable
    {
        ::uint32 Capabilities;       //   0  +4    bit N => capability/slot N populated (32 bits)
        ::uint32 _reservedHeader;    //   4  +4    pad to 8-byte boundary
        void   (*Slots[16])(void);   //   8  +128  sixteen 8-byte handler pointers

        // -------------------------------------------------------------
        // HasCapability -- predicate over the Capabilities bitmask.
        //
        // Returns true iff the capability bit is set. For slot-bearing
        // capabilities (HasNetSerializer, HasGetTypeHash, etc.), the
        // bit being set ALSO guarantees the corresponding Slots[]
        // entry is non-null. For capability-only bits (IsPlainOldData,
        // HasDestructor, etc.), the bit being set is the answer (no
        // dispatch involved).
        //
        // O(1); branch-free.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasCapability(
            ECppStructOpsCapability Cap) const noexcept
        {
            return (Capabilities & static_cast<::uint32>(Cap)) != 0;
        }

        // -------------------------------------------------------------
        // HasSlot -- predicate by slot index.
        //
        // Returns true if Slots[Slot] is non-null. Note: this is a
        // direct null-check, not a Capabilities probe -- some slots
        // (e.g. the post-MVP reserved entries) may be null even when
        // their corresponding bit is set. For the MVP slot set
        // (HasPostScriptConstruct .. HasFindInnerPropertyInstance),
        // capability-bit and slot-non-null are equivalent.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasSlot(ECppOpSlot Slot) const noexcept
        {
            return Slots[static_cast<::uint32>(Slot)] != nullptr;
        }

        // -------------------------------------------------------------
        // GetSlot<T> -- typed accessor.
        //
        // Returns the function pointer at Slots[Slot] cast to the
        // caller-supplied signature `T`. Returns nullptr if the slot
        // is not populated.
        //
        // `T` MUST be a function-pointer type matching the documented
        // signature for that slot. The cast uses reinterpret_cast; the
        // caller bears responsibility for matching the actual signature.
        // The slot signatures documented at the ECppOpSlot enum and in
        // the spec §7.2 ARE the contract.
        //
        // Usage example:
        //
        //   using FGetTypeHashFn = uint64 (*)(const void*);
        //   if (auto* Fn = vtable->GetSlot<FGetTypeHashFn>(ECppOpSlot::GetTypeHash)) {
        //       const uint64 Hash = Fn(InstancePtr);
        //   }
        //
        // The XPACT_CHECK guards against out-of-range Slot values
        // (Slot >= ECppOpSlot::Count); in Shipping the check compiles
        // out.
        // -------------------------------------------------------------
        template <typename T>
        [[nodiscard]] XPACT_FORCEINLINE T GetSlot(ECppOpSlot Slot) const noexcept
        {
            XPACT_CHECK(static_cast<::uint32>(Slot) < static_cast<::uint32>(ECppOpSlot::Count));
            void(*Raw)(void) = Slots[static_cast<::uint32>(Slot)];
            if (Raw == nullptr)
            {
                return nullptr;
            }
            return reinterpret_cast<T>(Raw);
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.8 §11.3). The static_asserts here ARE
    // the ABI contract. Any layout change breaks every constinit
    // FCppStructOpsFakeVTable instance engine-wide.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FCppStructOpsFakeVTable) == 136,
                  "FCppStructOpsFakeVTable ABI lock: must be exactly 136 bytes "
                  "(8-byte header + 16 slots * 8 bytes = 136; Rev 3 FIX-R2-MAJ-2). "
                  "See XCore-4b §7.2 + §11.3.");
    static_assert(alignof(FCppStructOpsFakeVTable) == 8,
                  "FCppStructOpsFakeVTable ABI lock: 8-byte alignment per §7.2 alignas(8)");

    // Member offsets locked per §11.3.
    static_assert(offsetof(FCppStructOpsFakeVTable, Capabilities)    == 0,
                  "FCppStructOpsFakeVTable ABI lock: Capabilities at offset 0");
    static_assert(offsetof(FCppStructOpsFakeVTable, _reservedHeader) == 4,
                  "FCppStructOpsFakeVTable ABI lock: _reservedHeader at offset 4");
    static_assert(offsetof(FCppStructOpsFakeVTable, Slots)           == 8,
                  "FCppStructOpsFakeVTable ABI lock: Slots[16] starts at offset 8 "
                  "(immediately after the 8-byte header)");
    static_assert(sizeof(FCppStructOpsFakeVTable::Slots) == 16 * 8,
                  "FCppStructOpsFakeVTable ABI lock: Slots[16] is 16 * 8 = 128 bytes");

    // Type traits.
    static_assert(::std::is_standard_layout_v<FCppStructOpsFakeVTable>,
                  "FCppStructOpsFakeVTable must be standard layout (offsetof must be "
                  "well-defined; constinit-friendly aggregate initialisation)");
    static_assert(::std::is_trivially_copyable_v<FCppStructOpsFakeVTable>,
                  "FCppStructOpsFakeVTable must be trivially copyable (no virtual methods)");
    static_assert(::std::is_trivially_destructible_v<FCppStructOpsFakeVTable>,
                  "FCppStructOpsFakeVTable must be trivially destructible (constinit + "
                  ".rodata residency requires no per-instance teardown)");

} // namespace XCore::Reflect
