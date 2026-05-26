// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FRWLock.h -- shared-mutex (reader/writer lock).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + Section 8.2
// (Threading contract: "FRWLock is reader-preferring on Win and
// writer-preferring on POSIX by default; XPact's wrapper standardises
// on writer-preferring across all three to avoid reader-starvation
// surprises").
//
// FRWLock is the shared/exclusive mutex (a.k.a. shared mutex, RWLock,
// reader/writer lock). Multiple threads may hold the shared lock
// concurrently; a single thread holds the exclusive lock with no
// shared holders. NOT recursive (shared-then-exclusive from the same
// thread is UB; recursive shared/exclusive is UB).
//
// =====================================================================
// ENGINE-WIDE LOCK DISCIPLINE (Rev 3 Round 2 audit FIX-R2-X-NEW)
// =====================================================================
//
// XCore has converged on a set of lock-handling invariants that apply
// to every subsystem using FRWLock / FCriticalSection / FMutex. These
// rules emerged from Rev 1 TC2 (FCustomVersionRegistry by-value return
// to avoid holding the lock across the caller) and Rev 2 FIX-2
// (FNamePool alloc-outside-lock to avoid AB-BA hazards against the
// allocator's own internal locks). Both addressed the same underlying
// principle through different fixes; the unified contract below codifies
// the principle so future subsystems do not re-discover it.
//
// PRINCIPLE 1: Locks are held for the shortest time necessary.
//
//   Critical sections should contain ONLY the work that the lock
//   guards. Pre-compute inputs outside the lock; post-process results
//   outside the lock. The longer a lock is held, the higher the
//   contention rate on the same primitive and the larger the
//   probability of priority-inversion / convoying.
//
//   A pattern of "acquire-lock, do unrelated CPU-heavy work, release"
//   is a contract violation. Hoist the CPU-heavy work out.
//
// PRINCIPLE 2: Allocations DO NOT happen under exclusive locks.
//
//   FMemory::Malloc / TArray::Reserve / TMap::Reserve / FString::
//   Append are all paths that may transitively acquire the global
//   allocator's pool-mutex (FMallocBinnedX). Holding a domain-level
//   exclusive lock while calling into the allocator creates the
//   classical AB-BA hazard: domain-thread holds DomainLock, calls
//   alloc, which acquires PoolMutex; concurrent thread holds
//   PoolMutex (legitimately), reaches into a domain API that needs
//   DomainLock. Deadlock.
//
//   The cure: pre-allocate any required buffers outside the lock,
//   pass them in, and the lock-holding code only performs pointer
//   manipulations on the pre-allocated storage. FNamePool's
//   PredictInsertPreAlloc (see Private/Reflection/FNamePool.cpp:
//   PredictInsertPreAlloc) is the reference implementation: it
//   acquires the shared lock to compute "what allocations would be
//   needed", releases the shared lock, calls FMemory::MallocOrAbort
//   without any lock held, then acquires the exclusive lock to
//   integrate the pre-allocated storage. Shipping the contract.
//
// PRINCIPLE 3: Returned pointer/reference must outlive the lock-
//              holding scope OR the function must return by value.
//
//   A function that returns a pointer/reference into protected
//   storage AND drops the lock at return time has handed the caller
//   a dangling reference -- a concurrent writer can race in and
//   invalidate the pointee.
//
//   Two acceptable patterns close this:
//
//     (a) BY-VALUE RETURN (preferred for small structs):
//         The function copies the protected value into the return
//         slot while still holding the lock; the caller receives a
//         disconnected copy. This is the pattern used by
//         FCustomVersionRegistry::FindVersion (returns FCustomVersion
//         by value, not const&). The cost is one copy per call;
//         for FCustomVersion (24 bytes) this is < 1 cache line and
//         the determinism gain dominates.
//
//     (b) PRE-ALLOCATE OUTSIDE, INTEGRATE UNDER LOCK (when alloc
//         cost dominates):
//         The caller allocates a destination buffer, passes a
//         pointer to it into the API, and the API memcpy/copies
//         into the caller's buffer under the lock. The buffer
//         outlives the lock-holding scope because the CALLER owns
//         it. The pattern is used by IConsoleManagerImpl's drain
//         (the Treiber-stack drain pre-allocates the concrete
//         TConsoleVariable<T> outside the lock, then inserts into
//         the registry under the lock).
//
// PRINCIPLE 4: Lock OWNERSHIP transfer is explicit.
//
//   A scoped-lock (FScopedReadLock / FScopedWriteLock / FScopedLock /
//   FScopedMutexLock) takes a reference to the lock; ownership lives
//   in the scope. Returning a scoped-lock from a function or storing
//   it as a class member is a contract violation -- the lock object's
//   lifetime would diverge from its referenced lock primitive's
//   lifetime, and an unbound scoped-lock is a leak. The convention
//   is "scoped locks are stack-only".
//
// VIOLATIONS:
//
//   Any subsystem that violates one of the four principles flags
//   itself as a candidate for an XCore-X spec amendment. Past audit
//   cycles surface those (Rev 1 TC2, Rev 2 FIX-2); the unified
//   contract above prevents future subsystems from re-discovering
//   the same patterns. New subsystem reviews MUST verify the four
//   principles are honoured before merge.
//
// Backing primitive per platform:
//   * Win64:     Win32 SRWLock (Slim Reader/Writer Lock; natively
//                supports shared/exclusive via the SRWLOCK API).
//                Pattern reference: UE Core Public/Windows/
//                WindowsPlatformMutex.h:62 FWindowsSharedMutex.
//   * Linux:     pthread_rwlock_t.
//                Pattern reference: UE Core Public/HAL/
//                PThreadsSharedMutex.h:29 FPThreadsSharedMutex.
//   * Android:   same as Linux (Bionic pthread).
//
// Writer-preference standardisation: SRWLock on Win64 is NOT writer-
// preferring by default (it's adaptive; the OS decides). pthread_
// rwlock_t on POSIX defaults are platform-defined (typically writer-
// preferring on glibc, reader-preferring on macOS). XPact's wrapper
// approximates writer-preferring on every platform: on Win64 we
// remain at the SRWLock default (Microsoft's adaptive policy); on
// POSIX we set PTHREAD_RWLOCK_PREFER_WRITER_NP via attributes where
// available, falling back to default on platforms that don't support
// the attribute (Android's older Bionic). The XPact spec's locked
// promise is "approximate writer-preferring across all three"; a
// stronger guarantee would require a custom userspace implementation
// which is out of scope for Phase 1c.
//
// Hot-reload (Section 8.5): NO virtual methods; POD-like wrapper.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "HAL/FTimespan.h"

#include <cstddef>

namespace XCore::HAL
{

class FRWLock
{
public:
    FRWLock() noexcept;
    ~FRWLock() noexcept;

    FRWLock(const FRWLock&)            = delete;
    FRWLock& operator=(const FRWLock&) = delete;
    FRWLock(FRWLock&&)                 = delete;
    FRWLock& operator=(FRWLock&&)      = delete;

    // -----------------------------------------------------------------
    // LockShared -- acquire shared (reader) access; blocks if a
    // writer holds or is waiting (under writer-preference).
    //
    // Win64: AcquireSRWLockShared.
    // POSIX: pthread_rwlock_rdlock.
    // -----------------------------------------------------------------
    void LockShared() noexcept;

    // -----------------------------------------------------------------
    // UnlockShared -- release shared access.
    //
    // Win64: ReleaseSRWLockShared.
    // POSIX: pthread_rwlock_unlock.
    // -----------------------------------------------------------------
    void UnlockShared() noexcept;

    // -----------------------------------------------------------------
    // LockExclusive -- acquire exclusive (writer) access; blocks
    // until all shared holders release.
    //
    // Win64: AcquireSRWLockExclusive.
    // POSIX: pthread_rwlock_wrlock.
    // -----------------------------------------------------------------
    void LockExclusive() noexcept;

    // -----------------------------------------------------------------
    // UnlockExclusive -- release exclusive access.
    //
    // Win64: ReleaseSRWLockExclusive.
    // POSIX: pthread_rwlock_unlock.
    // -----------------------------------------------------------------
    void UnlockExclusive() noexcept;

    // -----------------------------------------------------------------
    // TryLockShared -- attempt to acquire shared without blocking.
    //
    // Win64: TryAcquireSRWLockShared.
    // POSIX: pthread_rwlock_tryrdlock returning 0.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLockShared() noexcept;

    // -----------------------------------------------------------------
    // TryLockExclusive -- attempt to acquire exclusive without blocking.
    //
    // Win64: TryAcquireSRWLockExclusive.
    // POSIX: pthread_rwlock_trywrlock returning 0.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLockExclusive() noexcept;

    // -----------------------------------------------------------------
    // TryLockSharedFor / TryLockExclusiveFor -- bounded-wait variants
    // (Rev 1 audit HIGH-2 close-out).
    //
    // ##################################################################
    // # NOT SIM-PATH-SAFE  (Rev 3 Round 2 audit FIX-R2-MIN-3)
    // #
    // # These methods consult `FPlatformTime::Seconds()` (the wall-clock
    // # monotonic timer) to bound the wait. The sim path is deterministic-
    // # replay-bit-exact (XCore-4a §6 + master plan sim-path discipline);
    // # any function whose return value depends on wall-clock progress
    // # is forbidden from sim-path TUs.
    // #
    // # For a sim-path lock acquire pattern with a logical bound, use
    // # `TryLock*()` (without timeout) inside a bounded retry loop whose
    // # iteration count is the sim-path-safe budget:
    // #
    // #     constexpr int32 kMaxRetries = 16;
    // #     for (int32 I = 0; I < kMaxRetries; ++I)
    // #     {
    // #         if (Lock.TryLockShared()) break;
    // #         // sim-path yield: do unrelated deterministic work
    // #     }
    // #
    // # Renderer / UI / streaming TUs (non-sim-path) MAY use these
    // # methods; their non-determinism is harmless on those paths.
    // ##################################################################
    //
    // Returns true if the lock was acquired within the timeout, false
    // if the timeout expired without acquiring.
    //
    // Required by renderer->sim-thread bounded-wait patterns where
    // the caller cannot block indefinitely (e.g., a renderer's
    // mid-frame sample of a sim-side data structure must time out
    // if the sim thread is slow, rather than stall the frame).
    //
    // Per-platform behaviour:
    //
    //   * POSIX: native pthread_rwlock_timedrdlock /
    //            pthread_rwlock_timedwrlock with absolute-deadline
    //            CLOCK_REALTIME timespec; precise to the OS scheduler's
    //            granularity (typically 1 ms or better).
    //
    //   * Win64: SRWLock does NOT support timed acquire natively. The
    //            implementation degrades to a TryLock + spin-yield loop
    //            bounded by FPlatformTime::Seconds(). The yield is
    //            graded: Sleep(0) (yield to ready-state threads on the
    //            same core) for the first ~1 ms of wait; Sleep(1)
    //            beyond that. The wait granularity is therefore
    //            ~1 ms on Win64; sub-millisecond timeouts behave as
    //            non-blocking TryLock attempts in tight succession.
    //            This is suboptimal vs a native primitive but ships
    //            the contract (the alternative -- replacing SRWLock
    //            with a custom userspace primitive built on
    //            WaitOnAddress -- is Phase 2+ scope).
    //
    // Timeout semantics: a Timeout with TotalMicroseconds() <= 0 is
    // treated as a non-blocking TryLock (no spin; one TryLock attempt).
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLockSharedFor(FTimespan Timeout) noexcept;
    [[nodiscard]] bool TryLockExclusiveFor(FTimespan Timeout) noexcept;

private:
    // Opaque storage sized to the max of Win64 SRWLOCK (8 bytes) and
    // pthread_rwlock_t (56 bytes on glibc; 40 bytes on Android Bionic).
    // We pick 64 bytes to be safe + cache-line-aligned (avoids false-
    // sharing with adjacent counters in concurrent data structures).
    static constexpr ::SIZE_T kStorageSize  = 64;
    static constexpr ::SIZE_T kStorageAlign = 16;

    alignas(kStorageAlign) ::std::byte m_storage[kStorageSize];
};

static_assert(sizeof(FRWLock)  == 64, "FRWLock ABI lock: 64 bytes");
static_assert(alignof(FRWLock) == 16, "FRWLock ABI lock: 16-byte alignment");

// ---------------------------------------------------------------------
// FScopedReadLock / FScopedWriteLock -- RAII helpers for FRWLock.
//
// Pattern reference: UE Core has FReadScopeLock / FWriteScopeLock in
// HAL/CriticalSection.h. XPact adopts the same shape with the
// FScoped* prefix consistent with FScopedLock / FScopedMutexLock.
// ---------------------------------------------------------------------

class FScopedReadLock
{
public:
    explicit FScopedReadLock(FRWLock& InLock) noexcept
        : m_lock(InLock)
    {
        m_lock.LockShared();
    }

    ~FScopedReadLock() noexcept
    {
        m_lock.UnlockShared();
    }

    FScopedReadLock(const FScopedReadLock&)            = delete;
    FScopedReadLock& operator=(const FScopedReadLock&) = delete;

private:
    FRWLock& m_lock;
};

class FScopedWriteLock
{
public:
    explicit FScopedWriteLock(FRWLock& InLock) noexcept
        : m_lock(InLock)
    {
        m_lock.LockExclusive();
    }

    ~FScopedWriteLock() noexcept
    {
        m_lock.UnlockExclusive();
    }

    FScopedWriteLock(const FScopedWriteLock&)            = delete;
    FScopedWriteLock& operator=(const FScopedWriteLock&) = delete;

private:
    FRWLock& m_lock;
};

} // namespace XCore::HAL
