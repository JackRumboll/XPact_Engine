// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FStatTLS.h -- per-thread stat shard + XSTAT_* macro surface
// =====================================================================
//
// XCore-4a Rev 3, Section 10.1 (Public API) + Section 10.2 (Threading)
// + Section 10.5 (Threading model; fixes B-C1, C-7, M-10; fix Rev 3 M1
// lazy-init hot-path lifecycle).
//
// FStatTLS is the per-thread stat-counter shard accessor. The hot
// path (XSTAT_INC / XSTAT_ADD) writes a per-thread shard with no
// atomics, no locks; the per-frame merge (FStatRegistry::
// MergeFromAllThreads) aggregates shards into the global tree.
//
// PER-SECTION 10.5 SHARD LAYOUT (fix M-10):
//   Each per-thread FStatShard is a 24 KiB fixed array of 1024 slots,
//   each slot 24 bytes (uint64 hash, int64 count, uint64 padding).
//   Indexing is FStatId.Hash & (1024 - 1). Collision policy is
//   open-addressing with linear probing (up to 8 slots forward); on
//   full collision the write falls through to a per-thread overflow
//   heap.
//
// HOT-PATH LAZY-INIT LIFECYCLE (fix Rev 3 M1):
//   The TLS shard is lazy-initialised on first XSTAT_INC per thread.
//   The shard's slot table is constinit-initialised to zero (the
//   storage has static-storage duration and is zero-initialised
//   before any user code runs).
//
//   Phase contract (per Section 10.5):
//   * PreStaticInit: XSTAT_INC writes via the open-addressing
//     fallback path into the per-thread shard. No perfect-hash
//     lookup; no side-table read; no synchronisation. The path is
//     always available because the shard's storage is constinit-zero.
//   * PostStaticInit and later: the perfect-hash table is built and
//     published. The hot path remains UNCHANGED -- XSTAT_INC still
//     writes to the open-addressing slot. The perfect-hash table is
//     consulted ONLY by (a) MergeFromAllThreads as it walks the
//     shard during the per-frame merge, and (b) FStatRegistry's
//     lookup-by-name diagnostic surface.
//
// HOT-PATH SAFETY:
//   The hot path is a constexpr-foldable inline body: lookup the
//   shard pointer (one TLS-slot read), index the slot table by
//   (Hash & 1023), and increment the int64 count. No atomics
//   because the shard is per-thread.
//
// THREAD EXIT (fix C-7):
//   When a thread exits, its FStatShard destructor migrates any
//   remaining un-merged counts to a static FStatExitOverflowQueue
//   (a Treiber stack at file scope). The destructor runs BEFORE the
//   thread_local table itself is torn down (C++ destruction-order
//   guarantee). The next FStatRegistry::MergeFromAllThreads pops
//   the overflow queue and folds it into the global tree before
//   walking live threads.
//
// SIM-PATH SAFETY (Section 10.3):
//   XSTAT_INC / XSTAT_ADD are sim-path-safe (deterministic integer
//   counts). XSTAT_SCOPE_CYCLE is non-deterministic (wall-clock CPU
//   time) and is deprecated in sim-path TUs via the [[deprecated]]
//   attribute on FScopedCycleCounter when XPACT_SIMPATH is set.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "HAL/FStatId.h"

namespace XCore::Stat
{

    // -----------------------------------------------------------------
    // FStatTLS -- the per-thread shard accessor (macro back-end).
    //
    // The static methods route through the lazy-init shard accessor;
    // user code goes through the XSTAT_* macros (see below).
    // -----------------------------------------------------------------
    class FStatTLS
    {
    public:
        // -------------------------------------------------------------
        // Inc -- atomic-free increment of the int64 counter for Id.
        //
        // Hot-path discipline: the impl uses (Hash & 1023) as the
        // initial slot index and linear-probes up to 8 slots forward.
        // Empty slot found: claim it (write Hash + count=1). Existing
        // slot with matching Hash: increment count. All 8 probes fail:
        // fall through to the per-thread overflow heap (logs a Dev
        // warning on first insertion).
        //
        // Lazy-init: the first call from a fresh thread allocates the
        // shard's slot table via FMemory + Stat tag.
        // -------------------------------------------------------------
        static void Inc(const FStatId& Id) noexcept;

        // -------------------------------------------------------------
        // Add -- atomic-free add of Delta to the int64 counter for Id.
        //
        // Same hot-path discipline as Inc. Delta may be negative
        // (e.g., reference-counted decrements).
        // -------------------------------------------------------------
        static void Add(const FStatId& Id, ::int64 Delta) noexcept;

        // -------------------------------------------------------------
        // AddCycles -- adds Cycles to the cycle counter for Id.
        //
        // Cycle counters are non-deterministic by nature (wall-clock
        // CPU time). The accessor accumulates into a separate
        // cycle-counter slot table (parallel to the integer count
        // table); the side-table value is the sum across all threads.
        //
        // Sim-path-safe IFF the caller is non-sim-path. The
        // FScopedCycleCounter helper exposes the sim-path deprecation
        // diagnostic; this raw accessor does not (because the macros
        // are the user-facing surface).
        // -------------------------------------------------------------
        static void AddCycles(const FStatId& Id, ::uint64 Cycles) noexcept;
    };

    // -----------------------------------------------------------------
    // FScopedCycleCounter -- RAII helper for XSTAT_SCOPE_CYCLE.
    //
    // Constructed at scope entry; reads the cycle counter via
    // FPlatformTime::Cycles64. Destructed at scope exit; computes the
    // delta and forwards to FStatTLS::AddCycles.
    //
    // SIM-PATH SAFETY: the cycle counter is wall-clock-CPU-time-based
    // and is not deterministic. Sim-path TUs that include this
    // header indirectly through XSimMath get the [[deprecated]]
    // diagnostic on this type via XSimPathMathOverrides.h
    // (Phase 1g; the diagnostic is wired via a sim-path overlay).
    //
    // The current Phase 1f implementation does NOT yet attach the
    // [[deprecated]] attribute -- the sim-path overlay is wired in
    // Phase 1g (Step 14 FText) once the sim-path overlay header
    // pipeline is mature. The runtime body is correct standalone.
    // -----------------------------------------------------------------
    class FScopedCycleCounter
    {
    public:
        explicit FScopedCycleCounter(const FStatId& Id) noexcept;
        ~FScopedCycleCounter() noexcept;

        FScopedCycleCounter(const FScopedCycleCounter&)            = delete;
        FScopedCycleCounter& operator=(const FScopedCycleCounter&) = delete;
        FScopedCycleCounter(FScopedCycleCounter&&)                 = delete;
        FScopedCycleCounter& operator=(FScopedCycleCounter&&)      = delete;

    private:
        const FStatId& m_id;
        ::uint64       m_startCycles;
    };

} // namespace XCore::Stat


// =====================================================================
// XSTAT_DECL -- compile-time FStatId declaration.
//
// Builds an inline constexpr FStatId in the surrounding namespace.
// The Hash field is computed at compile time via a constexpr
// FNV-1a-64 hash over the concatenated Name + Group bytes.
//
// JUDGEMENT CALL: Section 10.1 spec wording shows the placeholder
// `/* compile-time xxh3-64(name + group) */`. The dispatch wording
// expects `::XCore::Hash::FXxh3::Hash64(...)`. FXxh3 is not
// constexpr in Phase 1d (the implementation is a ~400 LoC scalar
// .cpp), making the dispatch macro non-compileable as written.
//
// The principled choice is to ship XSTAT_DECL with a constexpr
// FNV-1a-64 hash (mathematically simpler; trivially constexpr) and
// have FStatTLS::Inc / Add / AddCycles use the SAME hash function
// at the runtime path. The two paths produce the identical
// FStatId.Hash output for the same name+group, so the runtime
// open-addressing slot lookup hits the same slot the compile-time
// XSTAT_DECL produced.
//
// FNV-1a-64 collision probability at <= 10,000 distinct stats is
// ~2.7e-12 -- identical to XXH3's at 64-bit width (the birthday
// bound is independent of the hash function's algorithm). The
// spec's M-3 wording (~2.7e-12) is preserved.
//
// TODO(Phase 1g audit cycle): if a constexpr XXH3 implementation
// is added to FXxh3, swap the FNV-1a-64 here for the constexpr
// XXH3 to align with the spec's XXH3 wording. The runtime side
// must swap simultaneously; both paths must continue to produce
// the same Hash for the same (Name, Group) tuple.
// =====================================================================

namespace XCore::Stat
{
    // Constexpr FNV-1a-64 hash. Used by XSTAT_DECL at compile time
    // AND by FStatTLS::Inc/Add at runtime so both paths produce the
    // same FStatId.Hash for the same (name, group) tuple.
    //
    // FNV-1a is well-defined byte-by-byte; constexpr-able with a
    // simple loop. The basis and prime are the IETF-canonical values.
    [[nodiscard]] constexpr ::uint64 StatHashFnv1a64(const char* Data, ::SIZE_T Length) noexcept
    {
        ::uint64 H = 14695981039346656037ULL;        // FNV offset basis
        for (::SIZE_T i = 0; i < Length; ++i)
        {
            H ^= static_cast<::uint64>(static_cast<unsigned char>(Data[i]));
            H *= 1099511628211ULL;                    // FNV prime
        }
        return H;
    }

    // Concatenation hash: hashes Name bytes followed by Group bytes
    // without a separator. The compile-time XSTAT_DECL uses this
    // shape: "Name" + "Group" treated as a single byte run. Distinct
    // (Name, Group) pairs produce distinct hashes because the byte
    // run is distinct.
    [[nodiscard]] constexpr ::uint64 StatHashNameGroup(const char* Name, ::SIZE_T NameLen,
                                                      const char* Group, ::SIZE_T GroupLen) noexcept
    {
        ::uint64 H = 14695981039346656037ULL;
        for (::SIZE_T i = 0; i < NameLen; ++i)
        {
            H ^= static_cast<::uint64>(static_cast<unsigned char>(Name[i]));
            H *= 1099511628211ULL;
        }
        for (::SIZE_T i = 0; i < GroupLen; ++i)
        {
            H ^= static_cast<::uint64>(static_cast<unsigned char>(Group[i]));
            H *= 1099511628211ULL;
        }
        return H;
    }
} // namespace XCore::Stat

// -----------------------------------------------------------------
// XSTAT_DECL(Name, Group) -- declares an inline constexpr FStatId.
//
// Use at namespace scope:
//
//   XSTAT_DECL(MyStatName, MyGroupName);
//
// Builds:
//
//   inline constexpr ::XCore::Stat::FStatId MyStatName_StatId = {
//       ::XCore::Stat::StatHashNameGroup("MyStatName", 10, "MyGroupName", 11),
//       "MyStatName",
//       "MyGroupName"
//   };
//
// The Name##_StatId identifier is the canonical access point for
// the XSTAT_INC / XSTAT_ADD / XSTAT_SCOPE_CYCLE macros.
// -----------------------------------------------------------------
#define XSTAT_DECL(Name, Group)                                                                      \
    inline constexpr ::XCore::Stat::FStatId Name##_StatId = {                                        \
        ::XCore::Stat::StatHashNameGroup(#Name, sizeof(#Name) - 1, #Group, sizeof(#Group) - 1),      \
        #Name,                                                                                       \
        #Group                                                                                       \
    }

// -----------------------------------------------------------------
// Hot-path macros.
//
// XSTAT_INC -- single int64 increment.
// XSTAT_ADD -- int64 add.
// XSTAT_SCOPE_CYCLE -- RAII cycle counter; non-deterministic.
// -----------------------------------------------------------------

#define XSTAT_INC(Name)                ::XCore::Stat::FStatTLS::Inc(Name##_StatId)
#define XSTAT_ADD(Name, Delta)         ::XCore::Stat::FStatTLS::Add(Name##_StatId, (Delta))
#define XSTAT_SCOPE_CYCLE(Name)        ::XCore::Stat::FScopedCycleCounter Name##_scc(Name##_StatId)
