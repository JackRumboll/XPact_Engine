// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatTLS.cpp -- per-thread shard implementation (Section 10.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 10.5 (Threading model; fix B-C1 module-safe
// TLS; fix C-7 thread-exit shard handoff; fix M-10 open-addressing
// + perfect-hash table; fix Rev 3 M1 hot-path lazy-init lifecycle).
//
// Implements:
//   * FStatTLS::Inc / Add / AddCycles    -- the hot-path routers.
//   * FScopedCycleCounter (ctor/dtor)    -- RAII cycle counter.
//   * FStatShard's destructor            -- thread-exit handoff.
//   * The static FStatExitOverflowQueue  -- Treiber stack.
//
// =====================================================================

#include "HAL/FStatTLS.h"

#include "HAL/FMemory.h"                // Phase 1g fix F-3: FMemory::IsAlive guard
#include "HAL/FPlatformTime.h"          // FPlatformTime::Cycles64
#include "HAL/FStatShard.h"
#include "HAL/TModuleSafeThreadLocal.h" // XPACT_TLS_MODULE_SAFE backing
#include "Macros/XPactMacros.h"

#include <atomic>

namespace XCore::Stat
{

    // -----------------------------------------------------------------
    // The per-thread shard.
    //
    // Per Section 10.5 + Section 13.1 fix B-C1: route through
    // XPACT_TLS_MODULE_SAFE on hot-reloadable DLL targets (Win64 /
    // Android via TModuleSafeThreadLocal<FStatShard>) and through
    // native thread_local on Linux server.
    //
    // Phase 1g (Fix M-4): the TModuleSafeThreadLocal Platform HAL
    // surface (FPlatformTLS::AllocSlot with per-thread destructor)
    // is in place. The macro emits one of:
    //   * Win64 / Android: ::XCore::HAL::TModuleSafeThreadLocal<FStatShard> g_shard;
    //                     accessed as `g_shard->Inc(Hash)`. Lazy-alloc
    //                     per thread; OS-driven destructor on thread
    //                     exit routes the shard's contents to the
    //                     Treiber overflow stack via FStatShard::~ctor
    //                     (which runs from the destructor callback).
    //   * Linux:          thread_local FStatShard g_shard;
    //                     accessed as `g_shard.Inc(Hash)`.
    //
    // The g_shard symbol exposes both forms via the GetShard() inline
    // helper below; the helper returns FStatShard& on every platform
    // so the call sites read uniformly.
    // -----------------------------------------------------------------
    namespace
    {
        XPACT_TLS_MODULE_SAFE(FStatShard, g_shard);

        // Inline helper that abstracts over the macro's two forms.
        // On the slot-allocator platforms (Win64/Android) `g_shard` is
        // a TModuleSafeThreadLocal<FStatShard>; `Get()` returns the
        // per-thread pointer (lazy-allocates on first call).
        // On Linux `g_shard` is a plain thread_local FStatShard; we
        // return its address directly.
        //
        // TODO(Phase 2 SIOF): on Win64/Android the g_shard global's
        // non-constexpr ctor (FPlatformTLS::AllocSlot) runs at
        // static-storage-duration init. If a constinit constructor in
        // another TU calls XSTAT_INC BEFORE g_shard's ctor runs (a
        // SIOF -- static init order fiasco), the Get() path reads
        // m_slotIdx as zero-initialized (slot 0, which Win32 typically
        // owns for the C runtime). The fix is a Schwarz-counter
        // pattern that forces g_shard's ctor to run before any other
        // TU's static-storage-duration init. The Phase 1g MIN-3 test
        // (ConstinitCtor.cpp) does NOT trigger this on Linux because
        // native thread_local is zero-init-safe; the Win64/Android
        // path may need the counter wrapper.
        XPACT_FORCEINLINE FStatShard& GetShard() noexcept
        {
#if XPACT_PLATFORM_WIN64 || XPACT_PLATFORM_ANDROID
            return *g_shard.Get();
#else
            return g_shard;
#endif
        }
    }

    // -----------------------------------------------------------------
    // The static FStatExitOverflowQueue -- Treiber stack at file scope.
    //
    // Per Section 10.5 fix C-7: "When a thread exits, its FStatShard
    // destructor migrates any remaining un-merged counts to static
    // FStatExitOverflowQueue (a Treiber stack at file scope) via
    // compare_exchange_weak(memory_order_acq_rel)."
    //
    // constinit-initialised to nullptr; safe to push from a thread-
    // exit destructor (TLS-storage-during-tear-down site).
    // -----------------------------------------------------------------
    constinit ::std::atomic<FStatExitOverflowEntry*> g_StatExitOverflowHead{nullptr};

    // -----------------------------------------------------------------
    // FStatTLS::Inc / Add / AddCycles -- the hot-path routers.
    //
    // The macro surface (XSTAT_INC / XSTAT_ADD / XSTAT_SCOPE_CYCLE)
    // calls these. The body delegates to the per-thread shard's
    // open-addressing slot scan.
    // -----------------------------------------------------------------
    void FStatTLS::Inc(const FStatId& Id) noexcept
    {
        GetShard().Inc(Id.Hash);
    }

    void FStatTLS::Add(const FStatId& Id, ::int64 Delta) noexcept
    {
        GetShard().Add(Id.Hash, Delta);
    }

    void FStatTLS::AddCycles(const FStatId& Id, ::uint64 Cycles) noexcept
    {
        GetShard().AddCycles(Id.Hash, Cycles);
    }

    // -----------------------------------------------------------------
    // FScopedCycleCounter -- the RAII cycle-counter helper.
    //
    // Constructor reads the cycle counter; destructor reads again and
    // forwards the delta to AddCycles.
    // -----------------------------------------------------------------
    FScopedCycleCounter::FScopedCycleCounter(const FStatId& Id) noexcept
        : m_id(Id)
        , m_startCycles(::XCore::HAL::FPlatformTime::Cycles64())
    {
    }

    FScopedCycleCounter::~FScopedCycleCounter() noexcept
    {
        const ::uint64 EndCycles = ::XCore::HAL::FPlatformTime::Cycles64();
        const ::uint64 Delta = EndCycles - m_startCycles;
        FStatTLS::AddCycles(m_id, Delta);
    }

    // -----------------------------------------------------------------
    // FStatShard::AddOverflow -- the rare-path fallback.
    //
    // Linear search through the fixed-size 16-entry overflow table.
    // Found: increment. Not found and table not full: append. Not
    // found and table full: drop (Phase 1g wires a Dev-warning
    // surface; Phase 1f drops silently per the documented trade).
    // -----------------------------------------------------------------
    void FStatShard::AddOverflow(::uint64 Hash, ::int64 Delta) noexcept
    {
        for (::int32 i = 0; i < m_overflowCount; ++i)
        {
            if (m_overflowEntries[i].Hash == Hash)
            {
                m_overflowEntries[i].Count += Delta;
                return;
            }
        }
        if (m_overflowCount < kStatShardOverflowCap)
        {
            m_overflowEntries[m_overflowCount].Hash    = Hash;
            m_overflowEntries[m_overflowCount].Count   = Delta;
            m_overflowEntries[m_overflowCount].Padding = 0;
            ++m_overflowCount;
            return;
        }
        // Overflow-of-overflow: silent drop in Phase 1f.
        //
        // TODO(Phase 1g): wire FOutputDevice / FLog warning here.
        // The expected stat-name population (<=10K total; <=8
        // colliding per slot at the 1024-slot table) keeps this path
        // statistically unreachable; the warning is a defense-in-
        // depth surface.
    }

    // -----------------------------------------------------------------
    // FStatShard::~FStatShard -- the thread-exit handoff (fix C-7).
    //
    // Walks the slot table; any slot with a non-zero count + Hash
    // is packaged into an FStatExitOverflowEntry and pushed onto the
    // static Treiber stack.
    //
    // The destructor runs as part of thread-local tear-down. The
    // shard's storage is still readable at this point (C++ standard
    // destruction-order guarantee for thread_local objects with non-
    // trivial dtors); the Treiber-stack push uses heap-allocated
    // FStatExitOverflowEntry nodes (FMemory + Stat tag) which
    // outlive the shard.
    // -----------------------------------------------------------------
    FStatShard::~FStatShard() noexcept
    {
        // Phase 1g fix F-3: shutdown-ordering guard.
        //
        // The migration below routes un-merged counts through
        // PushThreadExitEntry -> FMemory::MallocOrAbort, which is only
        // valid while the allocator is alive. If this destructor is
        // called AFTER FMemory::__Shutdown has cleared g_AllocatorAlive
        // (a real risk on a thread whose teardown beats the engine's
        // shutdown ordering), MallocOrAbort would either abort the
        // process during shutdown or return memory on a torn-down
        // allocator. Either is worse than dropping the counts: at
        // process shutdown, un-merged counts are by definition
        // unobservable (no live thread can read them).
        if (!::XCore::HAL::FMemory::IsAlive())
        {
            return;
        }

        // Walk main slots.
        for (::int32 i = 0; i < kStatShardSlotCount; ++i)
        {
            FStatSlot& Slot = m_slots[i];
            if (Slot.Hash == 0)
            {
                continue;
            }
            if (Slot.Count == 0 && Slot.Padding == 0)
            {
                continue;
            }
            PushThreadExitEntry(Slot.Hash, Slot.Count, Slot.Padding);
        }

        // Walk overflow entries.
        for (::int32 i = 0; i < m_overflowCount; ++i)
        {
            const FStatSlot& Slot = m_overflowEntries[i];
            if (Slot.Hash == 0)
            {
                continue;
            }
            if (Slot.Count == 0 && Slot.Padding == 0)
            {
                continue;
            }
            PushThreadExitEntry(Slot.Hash, Slot.Count, Slot.Padding);
        }
    }

    // -----------------------------------------------------------------
    // PushThreadExitEntry -- the Treiber-stack push.
    //
    // Heap-allocates an FStatExitOverflowEntry via FMemory + Stat
    // tag (the nodes outlive the thread that produces them; freed
    // by MergeFromAllThreads after fold-in). The CAS uses
    // memory_order_acq_rel for symmetry with IConsoleManager's
    // Treiber stack.
    //
    // Visible to FStatShard's destructor; static here so it doesn't
    // leak into the public header.
    // -----------------------------------------------------------------
    void FStatShard::PushThreadExitEntry(::uint64 Hash, ::int64 Count, ::uint64 Cycles) noexcept
    {
        // Allocation: FMemory + Stat tag. The entry size is small (24
        // bytes + next pointer = 32 bytes); allocation overhead is
        // amortised over the thread's lifetime.
        FStatExitOverflowEntry* Entry = static_cast<FStatExitOverflowEntry*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                sizeof(FStatExitOverflowEntry),
                alignof(FStatExitOverflowEntry),
                ::XCore::HAL::FMemTag::Stat));

        Entry->Hash   = Hash;
        Entry->Count  = Count;
        Entry->Cycles = Cycles;

        FStatExitOverflowEntry* Expected = g_StatExitOverflowHead.load(::std::memory_order_relaxed);
        do
        {
            Entry->Next = Expected;
        }
        while (!g_StatExitOverflowHead.compare_exchange_weak(
                    Expected, Entry,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_relaxed));
    }

    // -----------------------------------------------------------------
    // __PopThreadExitEntry -- pops one node from the Treiber stack.
    //
    // Called by FStatRegistry::MergeFromAllThreads in a loop until
    // the stack drains. Engine-internal; not exposed in the public
    // header.
    // -----------------------------------------------------------------
    FStatExitOverflowEntry* __PopThreadExitEntry() noexcept
    {
        FStatExitOverflowEntry* Expected = g_StatExitOverflowHead.load(::std::memory_order_acquire);
        while (Expected != nullptr)
        {
            FStatExitOverflowEntry* NewHead = Expected->Next;
            if (g_StatExitOverflowHead.compare_exchange_weak(
                    Expected, NewHead,
                    ::std::memory_order_acquire,
                    ::std::memory_order_acquire))
            {
                Expected->Next = nullptr;
                return Expected;
            }
        }
        return nullptr;
    }

    // -----------------------------------------------------------------
    // __GetCurrentThreadShard -- engine-internal accessor used by
    // FStatRegistry::MergeFromAllThreads.
    //
    // Returns the calling thread's shard. The shard's thread_local
    // storage makes this call thread-safe.
    //
    // PHASE 1F LIMITATION: Phase 1f's Merge sweeps only the calling
    // thread's shard; the multi-thread merge is wired in Phase 1g
    // once the thread registry (FThreadRegistry) ships.
    //
    // TODO(Phase 1g): wire the thread registry so Merge can iterate
    // every live thread's shard.
    // -----------------------------------------------------------------
    FStatShard* __GetCurrentThreadShard() noexcept
    {
        return &GetShard();
    }

} // namespace XCore::Stat
