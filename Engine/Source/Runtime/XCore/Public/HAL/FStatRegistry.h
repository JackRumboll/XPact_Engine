// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FStatRegistry.h -- global stat registry + frame-merge (Section 10.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 10.1 (Public API) + Section 10.5 (Threading
// model; perfect-hash build-time table + thread-exit shard handoff).
//
// FStatRegistry is the global stat tree. The per-thread shards
// (FStatShard, see Private/HAL/FStatShard.h) hold raw counts; the
// registry aggregates them once per frame via MergeFromAllThreads.
//
// PER-SECTION 10.5 INVARIANTS:
//
//   * Merge runs on a sync point (between Tick phases) walking each
//     thread's shard into the global tree. The hot path is lock-free;
//     correctness is guaranteed by the sync point plus the
//     thread-exit overflow queue.
//
//   * The perfect-hash side table maps FStatId.Hash -> a fixed slot
//     in the global tree. Built at module init from a generated
//     header that XBT emits during the XSTAT_DECL scan. The hash
//     table is keyed by the generated-header content's BLAKE3 hash
//     so a stale plugin against a fresh table fails the patch's
//     manifest-validation pass.
//
//   * Thread exit hands off any remaining un-merged counts to a
//     static FStatExitOverflowQueue (a Treiber stack at file scope).
//     MergeFromAllThreads pops the overflow queue BEFORE walking
//     live threads.
//
// SIM-PATH SAFETY: the registry's read surface is sim-path-safe (the
// aggregated counts are deterministic given identical call sequences).
// The Snapshot accessor produces a deterministic snapshot for replay
// comparison.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "HAL/FStatId.h"

namespace XCore::Stat
{
    // Forward declaration; full type lives below.
    class FStatSnapshot;

    // -----------------------------------------------------------------
    // FStatRegistry -- the global stat registry.
    //
    // All methods are static; the registry's storage is a singleton
    // held by a function-local static in the .cpp.
    // -----------------------------------------------------------------
    class FStatRegistry
    {
    public:
        // -------------------------------------------------------------
        // MergeFromAllThreads -- per-frame aggregate.
        //
        // Per Section 10.2: "MergeFromAllThreads, called once per
        // frame between Tick and Render, aggregates shards into the
        // global tree. Shards are 16 KiB fixed; an overflow path
        // (rare) takes a global lock and asserts in Debug.
        // Correctness is guaranteed by the sync point (no Tick code
        // running during Merge)."
        //
        // Order per Section 10.5 (fix C-7):
        //   1. Pop the FStatExitOverflowQueue (Treiber stack) and
        //      fold each thread-exit shard's remainders into the
        //      tree.
        //   2. Walk every live thread's shard via the global thread-
        //      list snapshot.
        //   3. Reset each live shard's counts to zero (post-merge;
        //      the next frame's deltas start from zero).
        //
        // The Merge MUST run on a known sync point; concurrent
        // XSTAT_INC during Merge is UB. The TaskGraph (Phase 1g)
        // will guarantee this; Phase 1f's body is correct standalone
        // when the caller respects the sync-point contract.
        // -------------------------------------------------------------
        static void MergeFromAllThreads() noexcept;

        // -------------------------------------------------------------
        // Snapshot -- copy the current global tree into Out.
        //
        // Out is filled with the (FStatId.Hash, int64 count) pairs
        // for every stat with a non-zero count. The result is
        // deterministic across same-input runs.
        //
        // Phase 1f: FStatSnapshot is a minimal wrapper around the
        // global tree's pair array (see below); Phase 1g may grow
        // the surface for the profiler dump.
        // -------------------------------------------------------------
        static void Snapshot(FStatSnapshot& Out) noexcept;

        // -------------------------------------------------------------
        // __Initialize -- the PostStaticInit perfect-hash build hook.
        //
        // Called by XEngineInit::Phase_PostStaticInit. Builds the
        // perfect-hash table from the XBT-emitted generated header
        // (Phase 1g; Phase 1f's body is a no-op stub because the
        // generated-header pipeline doesn't ship until then).
        //
        // The double-underscore prefix flags this as engine-internal
        // bootstrap code; user code MUST NOT call.
        //
        // Per fix Rev 3 M1: the hot path (XSTAT_INC) runs correctly
        // whether or not __Initialize has been called. The perfect-
        // hash table is consulted ONLY by MergeFromAllThreads and
        // by the diagnostic surface (lookup-by-name).
        // -------------------------------------------------------------
        static void __Initialize() noexcept;
    };

    // -----------------------------------------------------------------
    // FStatSnapshot -- a snapshot of the global stat tree.
    //
    // Minimal Phase 1f surface; Phase 1g may grow for profiler dump.
    //
    // The snapshot holds pairs of (Hash, count) for every stat with
    // a non-zero count. The Hash is the FStatId.Hash; the matching
    // Name + Group string pointers are looked up via the perfect-
    // hash side table (Phase 1g; in Phase 1f the snapshot is hash-
    // keyed only).
    // -----------------------------------------------------------------
    class FStatSnapshot
    {
    public:
        struct FEntry
        {
            ::uint64 Hash;
            ::int64  Count;
        };

        FStatSnapshot() noexcept;
        ~FStatSnapshot() noexcept;

        FStatSnapshot(const FStatSnapshot&)            = delete;
        FStatSnapshot& operator=(const FStatSnapshot&) = delete;
        FStatSnapshot(FStatSnapshot&&)                 = delete;
        FStatSnapshot& operator=(FStatSnapshot&&)      = delete;

        // -------------------------------------------------------------
        // Lookup -- O(n) linear search by hash. Phase 1f minimal
        // surface; Phase 1g grows to O(1) via the perfect-hash table.
        //
        // Returns the count for the given Hash, or 0 if not found.
        // -------------------------------------------------------------
        [[nodiscard]] ::int64 Lookup(::uint64 Hash) const noexcept;

        // -------------------------------------------------------------
        // GetEntryCount -- number of non-zero entries in the snapshot.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 GetEntryCount() const noexcept;

        // -------------------------------------------------------------
        // GetEntry -- access an entry by index.
        //
        // Caller-discipline: index must be in [0, GetEntryCount()).
        // The internal storage is a heap-allocated array; the
        // entries are NOT necessarily sorted (Phase 1f).
        // -------------------------------------------------------------
        [[nodiscard]] const FEntry& GetEntry(::int32 Index) const noexcept;

        // -------------------------------------------------------------
        // __AddEntry -- engine-internal append.
        //
        // Called by FStatRegistry::Snapshot; user code MUST NOT call.
        // -------------------------------------------------------------
        void __AddEntry(::uint64 Hash, ::int64 Count) noexcept;

        // -------------------------------------------------------------
        // __Reset -- clear all entries.
        //
        // Called by FStatRegistry::Snapshot before populating;
        // user code MUST NOT call.
        // -------------------------------------------------------------
        void __Reset() noexcept;

    private:
        // Heap-allocated array of entries. Grows on demand via
        // FMemory + Stat tag.
        FEntry* m_entries;
        ::int32 m_count;
        ::int32 m_capacity;
    };

} // namespace XCore::Stat
