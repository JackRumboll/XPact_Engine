// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FScriptStruct.h -- the 120-byte CppStructOps-bearing FStruct subclass
// (XCore-4b §7.2 + §11.3; FIX-R2-MAJ-2).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.2 ("FScriptStruct: adopt FakeVTable pattern
// + expand Capabilities to 32 bits") + Section 11.3 layout row
// `FScriptStruct: 120 bytes (104 FStruct + 16 ICppStructOps FakeVTable
// pattern)`.
//
// SPEC DRIFT (Phase 4b.5 audit-corrected; see FStruct.h SPEC DRIFT
// NOTICE for the root cause): FStruct = 112 (not 104) because
// XCore-4a TArray = 24 (not 16). FScriptStruct = 112 + 16 = 128.
// The ICppStructOps 16-byte body (Capabilities@104 -> @112 etc.)
// shifts by +8 relative to the spec; per-member relative offsets
// within the body are unchanged.
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
// LAYOUT (Phase 4b.5 audit-corrected; spec said 120 with FStruct=104,
// actually 128 with FStruct=112):
//
//   struct alignas(8) FScriptStruct : FStruct {
//       // FStruct base @ 0-111 (112 bytes; was spec-asserted 104)
//       uint32                          Capabilities;     // 112  +4
//       uint32                          _padCapabilities; // 116  +4
//       const FCppStructOpsFakeVTable*  CppOpsTable;      // 120  +8
//   };
//
// sizeof(FScriptStruct) == 128.
//
// XPACT_FSCRIPTSTRUCT_LAYOUT_TAG (Rev 3 §11.6):
//   "FScriptStruct-v3: 104 FStruct base + 16 ICppStructOps FakeVTable
//    pattern = 120 bytes; per-subtype FCppStructOpsFakeVTable in .rodata
//    at 136 bytes (8-byte header + 16 handler slots)"
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
        // ---- ICppStructOps per-instance (offsets 104-119; 16 bytes) ----

        // Bitmask of populated handlers + capability-only declarations.
        // See ECppStructOpsCapability for bit assignments. The mask is
        // 32 bits (24 active + 8 reserved per Rev 3 FIX-R2-MED-1).
        ::uint32                              Capabilities;       // 112  +4

        // Pad to 8-byte boundary for CppOpsTable.
        ::uint32                              _padCapabilities;   // 116  +4

        // Pointer to the per-subtype FCppStructOpsFakeVTable in
        // `.rodata`. nullptr is structurally invalid (every
        // FScriptStruct subtype emits a CppOpsTable, even the no-op
        // "plain struct" case which gets a zero-Capabilities all-null
        // table).
        const FCppStructOpsFakeVTable*        CppOpsTable;        // 120  +8

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
    // ABI locks (Phase 4b.5 audit-corrected; see SPEC DRIFT notice above).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FScriptStruct) == 128,
                  "FScriptStruct ABI lock (audit-corrected): 128 bytes "
                  "(112 FStruct base + 16 ICppStructOps FakeVTable pattern). "
                  "Spec Rev 3 §7.2 declared 120 with FStruct=104; FStruct is "
                  "actually 112 (audit per FStruct.h SPEC DRIFT NOTICE).");
    static_assert(alignof(FScriptStruct) == 8,
                  "FScriptStruct ABI lock: 8-byte alignment per §7.2 alignas(8)");

    // Member offsets locked per the audit-corrected layout.
    static_assert(offsetof(FScriptStruct, Capabilities)     == 112,
                  "FScriptStruct ABI lock (audit-corrected): Capabilities at "
                  "offset 112 (spec said 104; +8 shift from FStruct=112)");
    static_assert(offsetof(FScriptStruct, _padCapabilities) == 116,
                  "FScriptStruct ABI lock (audit-corrected): _padCapabilities "
                  "at offset 116 (spec said 108)");
    static_assert(offsetof(FScriptStruct, CppOpsTable)      == 120,
                  "FScriptStruct ABI lock (audit-corrected): CppOpsTable at "
                  "offset 120 (spec said 112)");

} // namespace XCore::Reflect
