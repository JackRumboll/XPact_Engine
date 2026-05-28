// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XStackRootScaffolding.h -- precise stack-root forward-decls
// (XCoreXObject Rev 4 §5.3 + §5.4; Phase 5.e).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5.3 ("Precise stack roots from XIL2CPP-
// emitted stack maps") + Section 5.4 ("Precise stack scanning"). Phase
// 5.e ships the C++ side forward declarations + the registration API
// stubs; the XIL2CPP integration that EMITS the FStackRootMap records
// from C# function metadata lands at System 6.
//
// =====================================================================
// PROTOCOL OVERVIEW
// =====================================================================
//
// Every XIL2CPP-transpiled C# function emits a per-function
// FStackRootMap into .rodata at module-init time. The map's records
// describe, for each safe-point in the function body, the byte
// offsets within the stack frame that hold XObject references at
// that safe-point.
//
// At GC mark phase, the collector walks each suspended thread's stack
// frames. For each frame:
//   1. Look up the FStackRootMap for the function (via the function's
//      address-to-map index, populated at module-init).
//   2. Compute the safe-point's PC offset relative to the function
//      start (the return-address-to-callsite distance).
//   3. Find the FStackRootEntry whose ProgramCounterOffset matches.
//   4. Enumerate the SlotCount XObject* slots starting at
//      StackOffset from the frame base; push each non-null slot onto
//      the gray queue.
//
// Phase 5.e provides:
//   * The struct shapes (FStackRootMap + FStackRootEntry) so XIL2CPP
//     can target them at System 6.
//   * The RegisterStackRootMap / UnregisterStackRootMap API so each
//     XIL2CPP-emitted per-module init function can publish its
//     per-function maps to the process-global registry.
//   * A process-global TArray-backed registry of all active maps.
//
// Phase 5.g (collector mark phase) consumes the registry: walks every
// suspended thread's stack, looks up the matching map, and visits
// each safe-point's live references.
//
// =====================================================================
// FStackRootMap / FStackRootEntry LAYOUT
// =====================================================================
//
//   FStackRootEntry @ 16 bytes:
//     * ProgramCounterOffset@0 (4 bytes) -- byte offset within the
//                                            function body at which
//                                            this safe-point lives.
//     * StackOffset@4          (4 bytes) -- byte offset within the
//                                            stack frame to the first
//                                            XObject* slot.
//     * SlotCount@8            (2 bytes) -- number of consecutive
//                                            XObject* slots.
//     * _pad@10                (6 bytes) -- pad to 16 bytes;
//                                            consumers MUST NOT touch.
//
//   FStackRootMap @ 24 bytes:
//     * FunctionAddress@0      (8 bytes) -- start address of the
//                                            transpiled function body.
//     * SafepointCount@8       (4 bytes) -- number of FStackRootEntry
//                                            records.
//     * _pad@12                (4 bytes) -- pad to 8-byte alignment.
//     * Safepoints@16          (8 bytes) -- pointer to the
//                                            FStackRootEntry array.
//
// Both structs are POD-shaped + trivially-copyable + trivially-
// destructible. NO virtual methods.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FMemory.h"            // ForEachStackRootMap heap fallback
#include "HAL/FMemTag.h"            // FMemTag::Reflection

#include <cstddef>          // offsetof
#include <cstdint>
#include <type_traits>      // is_trivially_copyable_v

namespace XCore::Detail
{

    // -----------------------------------------------------------------
    // FStackRootEntry -- one safe-point's live-reference description.
    //
    // alignas(4) -- the largest field is uint32; ABI total is 16 bytes
    // including pad.
    // -----------------------------------------------------------------
    struct alignas(4) FStackRootEntry
    {
        // --- ProgramCounterOffset (offset 0; 4 bytes) ---
        //
        // Byte offset within the function body at which this safe-point
        // lives. Computed at XIL2CPP emit time as the distance from the
        // function's start address to the safe-point's call-site /
        // back-edge / function-prologue location.
        //
        // At GC mark, the collector reads each suspended thread's
        // current return address, subtracts the function's start
        // address, and looks for the matching ProgramCounterOffset.
        ::std::uint32_t  ProgramCounterOffset;       //  0  +4

        // --- StackOffset (offset 4; 4 bytes) ---
        //
        // Byte offset within the stack frame to the first XObject*
        // slot at this safe-point. Computed at XIL2CPP emit time
        // relative to the frame base (typically negative for stack-
        // local variables below the frame pointer; positive for args
        // above the frame pointer).
        //
        // Signed int32: stack offsets are commonly negative on most
        // platforms' stack-growth-direction conventions. (We use an
        // int32 stored in the underlying uint32 to preserve byte
        // layout while allowing the consumer to reinterpret_cast for
        // arithmetic.)
        ::std::int32_t   StackOffset;                //  4  +4

        // --- SlotCount (offset 8; 2 bytes) ---
        //
        // Number of consecutive XObject* slots starting at StackOffset.
        // Typical C# function safe-point: 1-5 slots. The uint16
        // ceiling (65 535) is far beyond any realistic per-safe-point
        // count.
        ::std::uint16_t  SlotCount;                  //  8  +2

        // --- _pad (offset 10; 6 bytes) ---
        //
        // Pad to 16-byte total. Consumers MUST NOT read or write this
        // region. XIL2CPP MUST emit zero-initialised pad bytes for
        // bitwise-stable hashing of the FStackRootEntry array (if a
        // future GC introspection layer hashes them).
        ::std::uint8_t   _pad[6];                    // 10  +6
    };

    // ABI locks for FStackRootEntry.
    static_assert(sizeof(FStackRootEntry) == 16,
                  "FStackRootEntry ABI lock: 16 bytes per Phase 5.e §5.4 shape.");
    static_assert(alignof(FStackRootEntry) == 4,
                  "FStackRootEntry alignment lock: 4-byte aligned.");
    static_assert(offsetof(FStackRootEntry, ProgramCounterOffset) == 0,
                  "FStackRootEntry ABI lock: ProgramCounterOffset at offset 0.");
    static_assert(offsetof(FStackRootEntry, StackOffset)         == 4,
                  "FStackRootEntry ABI lock: StackOffset at offset 4.");
    static_assert(offsetof(FStackRootEntry, SlotCount)           == 8,
                  "FStackRootEntry ABI lock: SlotCount at offset 8.");
    static_assert(offsetof(FStackRootEntry, _pad)                == 10,
                  "FStackRootEntry ABI lock: _pad at offset 10.");
    static_assert(::std::is_trivially_copyable_v<FStackRootEntry>,
                  "FStackRootEntry must be trivially copyable.");

    // -----------------------------------------------------------------
    // FStackRootMap -- per-function safe-point table.
    //
    // alignas(8) -- the FunctionAddress + Safepoints pointer fields
    // require 8-byte alignment.
    //
    // The struct is POD-shaped + trivially-copyable + trivially-
    // destructible. XIL2CPP emits one FStackRootMap per transpiled
    // function into the module's .rodata; the per-module init function
    // calls RegisterStackRootMap once per map at module-load time.
    // -----------------------------------------------------------------
    struct alignas(8) FStackRootMap
    {
        // --- FunctionAddress (offset 0; 8 bytes) ---
        //
        // Start address of the transpiled C# function in the loaded
        // module's text segment. The GC mark phase uses this to match
        // a suspended thread's PC range to the owning function's map.
        //
        // The address is the START of the function body (the symbol's
        // entry-point); the per-safe-point ProgramCounterOffset values
        // are byte offsets FROM this address.
        const void*              FunctionAddress;     //  0  +8

        // --- SafepointCount (offset 8; 4 bytes) ---
        //
        // Number of FStackRootEntry records in the Safepoints array.
        // Typical XIL2CPP-emitted function: 1-3 safepoints per spec
        // §5.4 trailing prose.
        ::std::uint32_t          SafepointCount;       //  8  +4

        // --- _pad (offset 12; 4 bytes) ---
        //
        // Pad to align Safepoints@16 on 8 bytes. Consumers MUST NOT
        // touch.
        ::std::uint32_t          _pad;                 // 12  +4

        // --- Safepoints (offset 16; 8 bytes) ---
        //
        // Pointer to the FStackRootEntry array. The array lives in
        // .rodata alongside the FStackRootMap struct (both emitted by
        // XIL2CPP at the same time).
        const FStackRootEntry*   Safepoints;           // 16  +8
    };

    // ABI locks for FStackRootMap.
    static_assert(sizeof(FStackRootMap) == 24,
                  "FStackRootMap ABI lock: 24 bytes per Phase 5.e §5.4 shape.");
    static_assert(alignof(FStackRootMap) == 8,
                  "FStackRootMap alignment lock: 8-byte aligned.");
    static_assert(offsetof(FStackRootMap, FunctionAddress) ==  0,
                  "FStackRootMap ABI lock: FunctionAddress at offset 0.");
    static_assert(offsetof(FStackRootMap, SafepointCount)  ==  8,
                  "FStackRootMap ABI lock: SafepointCount at offset 8.");
    static_assert(offsetof(FStackRootMap, _pad)            == 12,
                  "FStackRootMap ABI lock: _pad at offset 12.");
    static_assert(offsetof(FStackRootMap, Safepoints)      == 16,
                  "FStackRootMap ABI lock: Safepoints at offset 16.");
    static_assert(::std::is_trivially_copyable_v<FStackRootMap>,
                  "FStackRootMap must be trivially copyable.");

    // =================================================================
    // Registration API (Phase 5.e ships the stubs; XIL2CPP integration
    // in System 6 will populate the maps at module-load time).
    //
    // RegisterStackRootMap: appends the map pointer to the process-
    // global active-map registry. The map MUST live for the lifetime
    // of the calling module (XIL2CPP emits the map into .rodata which
    // satisfies this).
    //
    // UnregisterStackRootMap: removes the map pointer from the
    // registry. Called by the per-module finaliser at unload time
    // (XLiveCoding hot-reload code path).
    //
    // Both operate under EXCLUSIVE lock on the registry's internal
    // RWLock. Registration is rare (once per loaded module); the
    // EXCLUSIVE lock cost is amortised over the module's lifetime.
    //
    // PHASE 5.e SCOPE: the API surface + the storage + the diagnostic
    // counter ship at Phase 5.e. The actual safe-point walker that
    // CONSUMES these maps (the GC mark phase's stack-scan body) lands
    // at Phase 5.g.
    // =================================================================

    void RegisterStackRootMap(const FStackRootMap* Map) noexcept;
    void UnregisterStackRootMap(const FStackRootMap* Map) noexcept;

    // =================================================================
    // GetRegisteredStackRootMapCount -- diagnostic accessor.
    //
    // Atomic snapshot of the active map count. Phase 5.e tests use
    // this to verify the Register / Unregister round-trip; production
    // diagnostics (XLiveCoding hot-reload introspection) also call it.
    // =================================================================
    [[nodiscard]] ::std::size_t GetRegisteredStackRootMapCount() noexcept;

    // =================================================================
    // ForEachStackRootMap -- iteration API.
    //
    // Snapshot-into-buffer pattern: copies the registered-map pointer
    // table into a caller-provided buffer (stack-allocated up to 32
    // slots; heap fallback beyond), releases the SHARED lock, then
    // walks the snapshot calling the visitor.
    //
    // The snapshot approach (rather than holding the SHARED lock
    // across the visitor) avoids deadlocks if the visitor accidentally
    // calls RegisterStackRootMap / UnregisterStackRootMap (those would
    // acquire EXCLUSIVE; an in-iteration SHARED would block them).
    //
    // The visitor signature is `void (const FStackRootMap* Map)`.
    //
    // Template body in-header so each call site monomorphises.
    // =================================================================

    // Snapshot helper exposed for the in-header template body.
    // Caller-owned OutBuffer of OutCapacity entries; returns the
    // count actually available (may exceed OutCapacity -- caller
    // retries with a larger buffer).
    ::int32 GetStackRootMapSnapshot(
        const FStackRootMap** OutBuffer,
        ::int32               OutCapacity) noexcept;

    // Test-only reset (Phase 5.e test-suite). Production callers MUST
    // NOT call this. Drops every registered map.
    void __ResetStackRootRegistryForTests() noexcept;

    template <typename Visitor>
    void ForEachStackRootMap(Visitor&& V) noexcept
    {
        constexpr ::int32 kStackCap = 32;
        const FStackRootMap* StackBuffer[kStackCap];
        const ::int32 Count = GetStackRootMapSnapshot(StackBuffer, kStackCap);
        if (Count <= kStackCap)
        {
            for (::int32 I = 0; I < Count; ++I)
            {
                if (StackBuffer[I] != nullptr)
                {
                    V(StackBuffer[I]);
                }
            }
            return;
        }
        // Heap fallback (FMemory tag-attributed allocation; mirrors
        // XReflectionRuntime::IterateAllClasses' pattern).
        const FStackRootMap** HeapBuffer = static_cast<const FStackRootMap**>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(Count) * sizeof(const FStackRootMap*),
                alignof(const FStackRootMap*),
                ::XCore::HAL::FMemTag::Reflection));
        const ::int32 Actual = GetStackRootMapSnapshot(HeapBuffer, Count);
        for (::int32 I = 0; I < Actual; ++I)
        {
            if (HeapBuffer[I] != nullptr)
            {
                V(HeapBuffer[I]);
            }
        }
        ::XCore::HAL::FMemory::Free(HeapBuffer);
    }

} // namespace XCore::Detail
