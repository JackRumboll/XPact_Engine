// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectSatbQueue.h -- per-thread SATB queue (XCoreXObject Rev 4 §5.6
// + Rev 2 FIX-A-MED-37).
// =====================================================================
//
// XCoreXObject Rev 4 §4.3 (SATB barrier semantics) + §5.6 (SATB queue
// management) + Rev 2 FIX-A-MED-37 / MAJOR-A30 (SATB queue bound
// during quiesce).
//
// THIS HEADER PROVIDES:
//
//   * FXObjectSatbQueue -- the 256-entry per-thread circular buffer.
//   * GetThreadSatbQueue() -- TLS accessor (TModuleSafeThreadLocal).
//
// PHASE 5.f scope: 256 entries * 8 bytes * N threads = 2 KB per thread;
// 16 KB on an 8-thread machine; 32 KB on a 16-thread machine. The
// memory bound is documented in spec §5.6 trailing prose.
//
// =====================================================================
//
// QUEUE SEMANTICS (per spec §5.6):
//
// The mutator pushes OLD reference values to its thread-local queue
// from the write barrier (XGCWriteBarrier.h). When the queue fills:
//
//   * If g_XGCAcceptDrains is TRUE (the normal case): drain the queue
//     into the global SATB log (FXObjectGlobalSatbLog) and reset to
//     empty. The drain is amortised: a 256-entry drain happens once
//     every ~256 stores per thread.
//
//   * If g_XGCAcceptDrains is FALSE (hot-reload quiesce): block the
//     writer thread on XGCWaitForDrainsAccepted() until the flag is
//     set again. The block is bounded by §11 acceptance criterion (e)
//     cascade hot-patch timeout (<120 s nominal / <150 s kill).
//
// The MARK PHASE (Phase 5.g) drains the queue at end-of-mark via
// DrainTo so any in-flight SATB entries are accounted for before the
// mark phase terminates.
//
// =====================================================================
//
// THREAD-LOCAL STORAGE (per Rev 2 FIX-A-CRIT-TC1 pattern + spec §5.6).
//
// The TLS access goes through TModuleSafeThreadLocal<FXObjectSatbQueue>
// from XCore-4a. This avoids the hot-reload TLS-stale-slot hazard:
// when an XCore module unloads, every still-alive thread's slot is
// freed; a fresh load installs a new slot. Raw `thread_local` storage
// has been used in earlier phases as a footgun (see
// TModuleSafeThreadLocal.h:6-16 commentary); we use the principled
// mechanism here.
//
// LAZY-INIT.
//
// The TLS slot is allocated on first access (TModuleSafeThreadLocal::
// Get lazy-creates a fresh FXObjectSatbQueue on FMemory's Stat tag).
// Subsequent calls on the same thread return the same queue.
//
// =====================================================================
//
// WAIT-FREE PUSH (common-case fast path).
//
// Push is wait-free when the queue has room: a single relaxed atomic
// increment on m_count gates the slot index; the entry write itself
// is non-atomic (the slot is owned exclusively by the calling thread
// because the queue is per-thread).
//
// The spin-block path activates ONLY when m_count == kCapacity. The
// drain is performed by the producer thread (the same thread that
// just hit capacity); the drain releases the queue's slots before
// returning.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/TModuleSafeThreadLocal.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore { class XObject; }

namespace XCore
{
    // -----------------------------------------------------------------
    // FXObjectSatbQueue -- per-thread 256-entry SATB queue.
    //
    // 256 entries chosen per spec §5.6 (locked decision O3 in spec
    // §15 (Open Choices Index)): "256 entries; fits in 2 KB which fits
    // comfortably in L1". Trade-off rationale:
    //   * 128 entries: drains too frequently under bursty writes.
    //   * 512 entries: 4 KB/thread; on a 16-thread machine 64 KB,
    //                  approaches the §5.6 nominal bound.
    //   * 256 entries: 2 KB/thread; fits in one L1 cache way.
    //
    // The queue has NO sequence-of-pushes ordering guarantee: the
    // global SATB log receives entries in arbitrary order (per the
    // SATB algorithm, the order doesn't matter -- only the SET of OLD
    // values matters).
    //
    // LAYOUT (compact per Prime Directive):
    //   * m_entries  : 256 * 8 = 2048 bytes
    //   * m_count    : atomic uint32 = 4 bytes
    //   * total ~2052 bytes per queue; trailing alignment pads to
    //     2056 bytes on 8-byte alignof.
    //
    // NO virtual methods. Trivially destructible (the atomic member's
    // destructor is trivial on every supported platform).
    // -----------------------------------------------------------------
    class alignas(8) FXObjectSatbQueue
    {
    public:
        // Per-thread queue capacity (spec O3).
        static constexpr ::std::size_t kCapacity = 256;

        // -------------------------------------------------------------
        // Construction.
        //
        // Default ctor zero-initialises m_count. The entry slots
        // contain garbage (Push overwrites them before Drain reads).
        // The slots are intentionally uninitialised for the hot-path
        // ctor cost (256 pointer initialisations would be ~256 cycles
        // per thread creation; the queue is over-allocated and Drain
        // reads only the slots Push has written to).
        // -------------------------------------------------------------
        FXObjectSatbQueue() noexcept
            : m_count(0)
        {
            // m_entries intentionally uninitialised; slots are
            // overwritten by Push before Drain reads them.
        }

        // Non-copyable, non-movable: the queue is per-thread and is
        // accessed via a stable TLS-allocated pointer.
        FXObjectSatbQueue(const FXObjectSatbQueue&)            = delete;
        FXObjectSatbQueue(FXObjectSatbQueue&&)                 = delete;
        FXObjectSatbQueue& operator=(const FXObjectSatbQueue&) = delete;
        FXObjectSatbQueue& operator=(FXObjectSatbQueue&&)      = delete;

        // Trivial destructor; the slots are POD-like XObject*.
        ~FXObjectSatbQueue() noexcept = default;

        // -------------------------------------------------------------
        // Push -- enqueue an OLD reference value.
        //
        // Called from the write barrier (XGCWriteBarrier.h) when the
        // global g_XGCIsConcurrentMarkActive flag is true.
        //
        // FAST PATH (wait-free): m_count < kCapacity. Single relaxed
        // increment + slot write.
        //
        // SLOW PATH (queue full): drain into the global SATB log via
        // FXObjectGlobalSatbLog::AppendBatch, then reset m_count to 0
        // and write the new entry at slot 0.
        //
        // QUIESCE PATH (g_XGCAcceptDrains is false): block on
        // XGCWaitForDrainsAccepted() until the flag is set, then
        // proceed as in the slow path. Bounded by the cascade timeout.
        // -------------------------------------------------------------
        void Push(XObject* OldValue) noexcept;

        // -------------------------------------------------------------
        // Drain -- internal: drain into the global SATB log.
        //
        // Called by Push when the queue fills. NOT intended for direct
        // caller use; exposed here for test verification + the Phase
        // 5.g mark-phase drain at end-of-mark.
        //
        // Acquires the global SATB log's lock for the AppendBatch.
        // Resets m_count to 0 on return.
        // -------------------------------------------------------------
        void DrainToGlobalLog() noexcept;

        // -------------------------------------------------------------
        // DrainTo -- visitor-style drain.
        //
        // Visitor signature: `void(XObject* OldValue)`. The visitor is
        // invoked once per live entry in FIFO order. The queue is
        // cleared after the drain.
        //
        // FOR TESTS + Phase 5.g end-of-mark drain. NOT called from the
        // write barrier hot path.
        // -------------------------------------------------------------
        template<typename Visitor>
        void DrainTo(Visitor&& V) noexcept
        {
            const ::std::uint32_t LocalCount =
                m_count.load(::std::memory_order_acquire);
            for (::std::uint32_t I = 0; I < LocalCount; ++I)
            {
                V(m_entries[I]);
            }
            m_count.store(0u, ::std::memory_order_release);
        }

        // -------------------------------------------------------------
        // Diagnostic.
        // -------------------------------------------------------------

        // Current live-entry count. Best-effort snapshot.
        [[nodiscard]] XPACT_FORCEINLINE
        ::std::size_t Size() const noexcept
        {
            return static_cast<::std::size_t>(
                m_count.load(::std::memory_order_acquire));
        }

        // Is the queue at capacity? (The next Push will drain or
        // block.)
        [[nodiscard]] XPACT_FORCEINLINE
        bool IsFull() const noexcept
        {
            return m_count.load(::std::memory_order_acquire) >= kCapacity;
        }

    private:
        // 256 OLD-reference slots. Owned exclusively by the calling
        // thread; non-atomic writes are safe.
        XObject*                    m_entries[kCapacity];

        // Live-entry count. Atomic because the diagnostic Size() may
        // be read from another thread (e.g., a test harness verifying
        // queue state across threads). The producer-side update path
        // uses memory_order_relaxed; the diagnostic side uses
        // memory_order_acquire.
        ::std::atomic<::std::uint32_t> m_count;
    };

    // -----------------------------------------------------------------
    // Compile-time size lock for the per-thread queue.
    //
    // ABI surface: the queue is per-thread (not cross-DLL); the size
    // is documented so the §5.6 memory-pressure bound (16 KB on 8
    // threads; 32 KB on 16 threads) is self-evident.
    //
    // 256 * 8 = 2048 bytes for the entries + 4 bytes m_count + 4 bytes
    // padding (alignof 8) = 2056 bytes.
    // -----------------------------------------------------------------
    static_assert(sizeof(FXObjectSatbQueue) == 2056,
                  "FXObjectSatbQueue size lock: 256 * sizeof(XObject*) + "
                  "sizeof(atomic<uint32>) + alignment pad = 2056 bytes "
                  "(per spec §5.6 + Rev 2 FIX-A-MED-37 memory bound).");
    static_assert(alignof(FXObjectSatbQueue) == 8,
                  "FXObjectSatbQueue alignof lock: 8 (matches XObject* "
                  "slot alignment).");
    static_assert(::std::is_trivially_destructible_v<FXObjectSatbQueue>,
                  "FXObjectSatbQueue must be trivially destructible "
                  "(TLS storage is freed via FMemory::Free on thread "
                  "exit; trivial destructor matches the no-op cleanup).");

    // -----------------------------------------------------------------
    // GetThreadSatbQueue -- TLS accessor for the calling thread's
    // SATB queue.
    //
    // First call on any thread allocates a fresh FXObjectSatbQueue on
    // FMemory::Stat tag (TModuleSafeThreadLocal::Get pattern); the
    // queue persists for the thread's lifetime.
    //
    // Returns a reference (NOT a pointer) so the caller can never
    // accidentally null-check the result; the queue is guaranteed
    // live for the calling thread.
    //
    // HOT PATH. XPACT_FORCEINLINE so the typical write barrier site
    // can inline the TLS lookup + the subsequent Push call.
    // -----------------------------------------------------------------
    [[nodiscard]] FXObjectSatbQueue& GetThreadSatbQueue() noexcept;

} // namespace XCore
