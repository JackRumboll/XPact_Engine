// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FScriptStruct.h -- the 136-byte CppStructOps-bearing FStruct subclass
// (XCore-4b §7.2 + §11.3; FIX-R2-MAJ-2; XCoreXObject Phase 5.a' Rev
// 13.9 cascade).
// =====================================================================
//
// REV 13.9 CASCADE (XCoreXObject Phase 5.a' Contract prerequisite):
//
//   FScriptStruct cascades +8 bytes from FStruct's RefSchema appendage
//   (FStruct 112 -> 120). FScriptStruct's own body (Capabilities +
//   _padCapabilities + CppOpsTable = 16 bytes) is unchanged; sizeof
//   grows from 128 to 136. All FScriptStruct-specific offsets shift
//   by +8.
//
// =====================================================================
//
// XCore-4b Rev 3, Section 7.2 ("FScriptStruct: adopt FakeVTable pattern
// + expand Capabilities to 32 bits") + Section 11.3 layout row
// `FScriptStruct: 120 bytes (104 FStruct + 16 ICppStructOps FakeVTable
// pattern)`.
//
// SPEC DRIFT (Phase 4b.5 audit-corrected; see FStruct.h SPEC DRIFT
// NOTICE for the root cause): FStruct = 112 (not 104) because
// XCore-4a TArray = 24 (not 16). FScriptStruct (Phase 4b.5) = 112 + 16
// = 128. Phase 5.a' Rev 13.9 cascade: FStruct = 120; FScriptStruct =
// 120 + 16 = 136. The ICppStructOps 16-byte body (Capabilities@104 ->
// @112 -> @120 etc.) shifts by +16 relative to the spec; per-member
// relative offsets within the body are unchanged.
//
// FScriptStruct extends FStruct with an ICppStructOps handler table.
// It is the runtime descriptor for non-XObject reflected structs:
// engine-shipped value types (FVector, FRotator, FTransform,
// FLinearColor) AND user-defined structs declared via
// XSTRUCT(...) { ... }.
//
// Rev 3 (FIX-R2-MAJ-2): the per-instance ICppStructOps surface is
// reduced from Rev 2's inlined ~27 function pointers to a single
// FakeVTable pointer pointing at a per-subtype handler table in
// `.rodata`. The pattern mirrors FProperty's FFakeVTable (FIX-2).
// Per-instance footprint collapses to 16 bytes (4-byte Capabilities
// + 4-byte pad + 8-byte FakeVTable pointer).
//
// LAYOUT (Phase 5.a' Rev 13.9 cascade; spec said 120 with FStruct=104,
// Phase 4b.5 had 128 with FStruct=112, now 136 with FStruct=120):
//
//   struct alignas(8) FScriptStruct : FStruct {
//       // FStruct base @ 0-119 (120 bytes; Phase 5.a' Rev 13.9: 112+8 RefSchema)
//       uint32                          Capabilities;     // 120  +4
//       uint32                          _padCapabilities; // 124  +4
//       const FCppStructOpsFakeVTable*  CppOpsTable;      // 128  +8
//   };
//
// sizeof(FScriptStruct) == 136.
//
// XPACT_FSCRIPTSTRUCT_LAYOUT_TAG (Phase 5.a' Rev 13.9 cascade; v5):
//   "FScriptStruct-v5 (Contract Rev 13.9 cascade): 120 FStruct base
//    (with appended RefSchema@112) + 16 ICppStructOps FakeVTable
//    pattern = 136 bytes; per-subtype FCppStructOpsFakeVTable in .rodata
//    at 136 bytes (8-byte header + 16 handler slots) unchanged."
//
// HOT-RELOAD SAFETY:
//
//   * No virtual methods (FStruct has none; the dispatch surface is
//     the per-subtype FCppStructOpsFakeVTable in .rodata).
//   * Standard-layout NOT asserted (FStruct inherits from no base
//     but has many data members; FScriptStruct adds 3 more, which
//     precludes formal standard-layout per [class]/7 -- single
//     inheritance with both base + derived carrying members is
//     not standard-layout).
//   * Trivially-copyable NOT asserted (FStruct carries std::atomic
//     + TArray which are not trivially copyable; FScriptStruct
//     inherits the non-triviality).
//
// CAPABILITIES BITMASK + DISPATCH:
//
//   The per-instance Capabilities mirror the FakeVTable's
//   Capabilities (the spec calls out that they SHOULD be consistent,
//   but allows them to diverge in pathological cases where the
//   FakeVTable has every slot populated but the runtime per-instance
//   wants to gate dispatch -- e.g., a struct that declares
//   HasNetSerializer but the runtime wants to disable for a specific
//   instance subset). At Phase 4b.5 the two MUST agree; XHT codegen
//   populates both from the same source-of-truth at .gen.cpp emit.
//
//   Dispatch via:
//
//     if (StructDesc->HasCapability(ECppStructOpsCapability::HasNetSerializer)) {
//         const auto Fn = StructDesc->CppOpsTable->GetSlot<FNetSerializeFn>(
//             ECppOpSlot::NetSerialize);
//         if (Fn) {
//             const bool OK = Fn(Ar, Map, Value);
//             ...
//         }
//     }
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FCppStructOpsFakeVTable.h"  // ECppStructOpsCapability + slot table
#include "Reflection/FStruct.h"                   // FStruct base (full type by inheritance)

#include <cstddef>      // offsetof

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FScriptStruct -- 120-byte FStruct subclass with ICppStructOps.
    //
    // Per spec §7.2: alignas(8). NO virtual methods.
    //
    // FScriptStruct inherits FStruct's non-copyable / non-movable
    // posture (the std::atomic UnversionedSchema field + TArray
    // ObjectRefProperties forbid value-copy).
    // -----------------------------------------------------------------
    struct alignas(8) FScriptStruct : public FStruct
    {
        // ---- ICppStructOps per-instance (offsets 120-135 after Rev
        //      13.9 cascade; 16 bytes) ----
        //
        // Phase 5.a' Rev 13.9 cascade: every FScriptStruct-specific
        // offset shifts +8 from the Phase 4b.5 baseline (FStruct base
        // 112 -> 120 via the Rev 13.9 RefSchema appendage). The
        // 16-byte ICppStructOps body itself is structurally unchanged.

        // Bitmask of populated handlers + capability-only declarations.
        // See ECppStructOpsCapability for bit assignments. The mask is
        // 32 bits (24 active + 8 reserved per Rev 3 FIX-R2-MED-1).
        ::uint32                              Capabilities;       // 120  +4

        // Pad to 8-byte boundary for CppOpsTable.
        ::uint32                              _padCapabilities;   // 124  +4

        // Pointer to the per-subtype FCppStructOpsFakeVTable in
        // `.rodata`. nullptr is structurally invalid (every
        // FScriptStruct subtype emits a CppOpsTable, even the no-op
        // "plain struct" case which gets a zero-Capabilities all-null
        // table).
        const FCppStructOpsFakeVTable*        CppOpsTable;        // 128  +8

        // -------------------------------------------------------------
        // Construction.
        //
        // FScriptStruct constructors delegate to FStruct's identity-
        // taking ctor. The Capabilities + CppOpsTable slots are set
        // by the explicit ctor; the default ctor zero-initialises.
        //
        // FScriptStruct inherits FStruct's deleted copy/move ops.
        // -------------------------------------------------------------

        FScriptStruct() noexcept
            : FStruct()
            , Capabilities(0U)
            , _padCapabilities(0U)
            , CppOpsTable(nullptr)
        {
        }

        FScriptStruct(FName InName, const FStruct* InSuper,
                      ::uint32 InCapabilities,
                      const FCppStructOpsFakeVTable* InCppOpsTable) noexcept
            : FStruct(InName, InSuper)
            , Capabilities(InCapabilities)
            , _padCapabilities(0U)
            , CppOpsTable(InCppOpsTable)
        {
        }

        // -------------------------------------------------------------
        // Accessors.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE ::uint32 GetCapabilities() const noexcept
        {
            return Capabilities;
        }

        [[nodiscard]] XPACT_FORCEINLINE const FCppStructOpsFakeVTable*
            GetCppOpsTable() const noexcept
        {
            return CppOpsTable;
        }

        // -------------------------------------------------------------
        // HasCapability -- predicate over the per-instance Capabilities.
        //
        // The per-instance bitmask is the authoritative source of
        // truth for dispatch gating. Callers should ALWAYS probe
        // HasCapability(X) before dispatching via Slots[X] to avoid
        // null-deref when an FScriptStruct legitimately doesn't
        // populate a handler.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE bool HasCapability(
            ECppStructOpsCapability Cap) const noexcept
        {
            return (Capabilities & static_cast<::uint32>(Cap)) != 0U;
        }

        // -------------------------------------------------------------
        // GetSlot<T> -- typed slot accessor.
        //
        // Forwards to CppOpsTable->GetSlot<T>(Slot). Returns nullptr
        // if CppOpsTable is null (structurally invalid; defensive
        // posture for robustness against partially-initialised
        // descriptors).
        // -------------------------------------------------------------
        template <typename T>
        [[nodiscard]] XPACT_FORCEINLINE T GetSlot(ECppOpSlot Slot) const noexcept
        {
            if (CppOpsTable == nullptr)
            {
                return nullptr;
            }
            return CppOpsTable->GetSlot<T>(Slot);
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (Phase 4b.5 audit-corrected baseline 128 + Phase 5.a'
    // Contract Rev 13.9 cascade per XCoreXObject Rev 4 §11.2 = 136 total).
    //
    // The cascade: FStruct base grew 112 -> 120 via the appended
    // RefSchema pointer; FScriptStruct's own 16-byte body offsets shift
    // by +8 wholesale.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FScriptStruct) == 136,
                  "FScriptStruct ABI lock (Phase 5.a' Rev 13.9 cascade): "
                  "136 bytes = 120 FStruct base (with appended RefSchema "
                  "@ FStruct.112) + 16 ICppStructOps FakeVTable pattern. "
                  "Phase 4b.5 baseline was 128 (112 FStruct + 16 body); "
                  "Rev 13.9 micro-bump cascades +8 from FStruct growth.");
    static_assert(alignof(FScriptStruct) == 8,
                  "FScriptStruct ABI lock: 8-byte alignment per §7.2 alignas(8)");

    // Member offsets locked per the Phase 5.a' Rev 13.9 cascade (each
    // FScriptStruct-specific offset shifts +8 from the Phase 4b.5
    // baseline because FStruct base grew by +8 via the Rev 13.9
    // RefSchema appendage).
    static_assert(offsetof(FScriptStruct, Capabilities)     == 120,
                  "FScriptStruct ABI lock (Rev 13.9 cascade): Capabilities "
                  "at offset 120 (Phase 4b.5: 112; +8 shift from FStruct "
                  "base growing to 120 via Rev 13.9 RefSchema appendage)");
    static_assert(offsetof(FScriptStruct, _padCapabilities) == 124,
                  "FScriptStruct ABI lock (Rev 13.9 cascade): _padCapabilities "
                  "at offset 124 (Phase 4b.5: 116)");
    static_assert(offsetof(FScriptStruct, CppOpsTable)      == 128,
                  "FScriptStruct ABI lock (Rev 13.9 cascade): CppOpsTable "
                  "at offset 128 (Phase 4b.5: 120)");

} // namespace XCore::Reflect
