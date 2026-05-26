// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FCriticalSection.h -- recursive mutex (always-recursive cross-platform).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives), Section 8.2
// (Threading contract), Section 8.5 (Hot-Reload ABI), Section 8.6
// (UE-divergence row 1: "FCriticalSection non-recursive on Win,
// recursive on POSIX" -> XPact "always recursive").
//
// FCriticalSection is the recursive-mutex primitive. Same thread may
// Lock() N times; must Unlock() N times to release. On all three
// platforms the underlying primitive is natively recursive:
//
//   * Win64:     Win32 CRITICAL_SECTION (natively recursive; see
//                Windows docs "Critical Section Objects").
//   * Linux:     pthread_mutex_t initialised with
//                PTHREAD_MUTEX_RECURSIVE attribute.
//   * Android:   same as Linux (Bionic pthread).
//
// The choice to use CRITICAL_SECTION on Win64 rather than SRWLock +
// thread-id-counter tracking is deliberate. SRWLock is non-recursive
// at the OS level; making it recursive in userspace requires an
// embedded thread-id + recursion-counter pair which is complex AND
// (because the wrapper would need its own atomic operations on every
// Lock/Unlock) measurably slower than CRITICAL_SECTION's native
// path. Spec body Section 8.2: "FCriticalSection always recursive on
// every platform (Win CRITICAL_SECTION is naturally recursive)."
//
// UE-divergence (Section 8.6 row 1): UE's FCriticalSection is
// non-recursive on Win (via SRWLock; see UE Core HAL/Windows/
// WindowsPlatformMutex.h line 62 "FWindowsSharedMutex" which uses
// SRWLOCK) and recursive on POSIX (via PTHREAD_MUTEX_RECURSIVE in
// HAL/PThreadsRecursiveMutex.h:32). The cross-platform behaviour
// mismatch is a Prime Directive violation per the spec body. XPact
// fixes this by making the type ALWAYS recursive on every platform;
// callers that genuinely need non-recursive semantics use FMutex
// (Section 8.1 fix M-5) instead.
//
// Hot-reload (Section 8.5): NO virtual methods; POD-like wrapper
// around an OS handle. The wrapper size + layout are pinned via
// static_assert; any change is an explicit ABI bump.
//
// =====================================================================
//
// IMPLEMENTATION NOTES:
//
// The Public header MUST NOT include <Windows.h> (heavyweight; pulls
// half a million lines of NTDLL declarations) or <pthread.h>
// (POSIX-only). Instead, we declare an opaque storage buffer
// (`alignas(N) ::std::byte m_storage[N]`) sized to the platform's
// CRITICAL_SECTION / pthread_mutex_t. The .cpp performs a
// placement-new of the platform handle into the buffer.
//
// Sizes (verified against platform SDK headers):
//   * Win64 CRITICAL_SECTION:        40 bytes / 8-byte alignment.
//   * Linux  pthread_mutex_t (glibc): 40 bytes / 8-byte alignment.
//   * Android pthread_mutex_t:        4 bytes / 4-byte alignment
//     (Bionic; older NDK).
//     -- The smaller size is acceptable because the union takes the
//        MAX of the three sizes; Android's smaller handle wastes a
//        few bytes but never overflows.
//
// We pick the conservative MAX as the buffer size so the same Public
// header works on every platform without per-platform layout drift.
//
// CHOSEN BUFFER SIZE: 64 bytes / 16-byte alignment.
//   * Provides headroom for future platform-handle growth.
//   * One cache-line padding (64 bytes is exactly XPACT_CACHE_LINE_SIZE
//     on x86_64) so two adjacent FCriticalSections don't false-share.
//   * 16-byte alignment is sufficient for every supported handle type
//     and matches the larger of the platform alignments.
//
// The static_assert in the .cpp verifies sizeof(platform-handle) <=
// kStorageSize at compile time per platform; if a future platform
// SDK bumps the handle size beyond 64, the build fails at the
// platform .cpp with a clear "platform handle too large" diagnostic.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "HAL/FTimespan.h"

#include <cstddef>

namespace XCore::HAL
{

class FCriticalSection
{
public:
    FCriticalSection() noexcept;
    ~FCriticalSection() noexcept;

    // Non-copyable / non-movable. The underlying OS handle (CRITICAL_
    // SECTION on Win64, pthread_mutex_t on POSIX) is itself
    // non-copyable -- moving a held mutex is undefined behaviour at
    // the OS level. The deletes propagate that constraint into the
    // wrapper.
    FCriticalSection(const FCriticalSection&)            = delete;
    FCriticalSection& operator=(const FCriticalSection&) = delete;
    FCriticalSection(FCriticalSection&&)                 = delete;
    FCriticalSection& operator=(FCriticalSection&&)      = delete;

    // -----------------------------------------------------------------
    // Lock -- acquire the mutex; blocks if held by another thread.
    //
    // Same-thread recursion permitted: a thread that already holds
    // the mutex may call Lock again, incrementing the internal
    // recursion counter. Each Lock must be paired with an Unlock to
    // release.
    //
    // Win64: EnterCriticalSection.
    // POSIX: pthread_mutex_lock on PTHREAD_MUTEX_RECURSIVE.
    // -----------------------------------------------------------------
    void Lock() noexcept;

    // -----------------------------------------------------------------
    // TryLock -- attempt to acquire without blocking.
    //
    // Returns true if the mutex was acquired (either because it was
    // unheld or because the calling thread already holds it), false
    // otherwise. As with Lock, every successful TryLock must be
    // paired with an Unlock.
    //
    // Win64: TryEnterCriticalSection.
    // POSIX: pthread_mutex_trylock returning 0.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLock() noexcept;

    // -----------------------------------------------------------------
    // TryLockFor -- bounded-wait variant (Rev 1 audit HIGH-2
    // close-out).
    //
    // ##################################################################
    // # NOT SIM-PATH-SAFE  (Rev 3 Round 2 audit FIX-R2-MIN-3)
    // #
    // # Bounded-wait variants consult `FPlatformTime::Seconds()` on
    // # Win64 (and CLOCK_REALTIME on POSIX) to determine acquisition.
    // # The sim path requires deterministic-replay-bit-exactness;
    // # wall-clock-dependent acquisition is forbidden in sim-path TUs.
    // #
    // # For a sim-path lock acquire with a logical bound, use the
    // # `TryLock()` (no-timeout) variant inside a bounded retry loop.
    // # Renderer / UI / streaming TUs (non-sim-path) MAY use this
    // # method without restriction.
    // ##################################################################
    //
    // Returns true if the mutex was acquired within the timeout, false
    // if the timeout expired without acquiring. Recursive semantics
    // are preserved: a thread that already holds the mutex acquires
    // immediately (no wait, no timeout consumption) and increments
    // the recursion counter.
    //
    // Per-platform behaviour:
    //
    //   * POSIX: native pthread_mutex_timedlock with
    //            absolute-deadline CLOCK_REALTIME timespec. Precise
    //            to OS scheduler granularity (~1 ms or better).
    //
    //   * Win64: CRITICAL_SECTION does NOT support timed acquire
    //            natively (TryEnterCriticalSection is non-blocking
    //            only). The implementation degrades to a TryLock +
    //            spin-yield loop bounded by FPlatformTime::Seconds()
    //            with graded Sleep(0)/Sleep(1) backoff. Wait
    //            granularity is ~1 ms on Win64; sub-ms timeouts behave
    //            as non-blocking TryLock attempts in tight succession.
    //
    // Timeout semantics: a Timeout with TotalMicroseconds() <= 0 is
    // a non-blocking TryLock (no spin; one TryLock attempt).
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLockFor(FTimespan Timeout) noexcept;

    // -----------------------------------------------------------------
    // Unlock -- release the mutex (decrement recursion counter).
    //
    // The mutex is fully released when the recursion counter reaches
    // zero. Calling Unlock without a matching Lock is undefined
    // behaviour at the OS level; XPACT_CHECK_SLOW could catch this in
    // Debug but the cost of tracking the recursion depth in userspace
    // is unjustified -- the OS catches it via the pthread error
    // return path or the CRITICAL_SECTION's debug-CRT check.
    //
    // Win64: LeaveCriticalSection.
    // POSIX: pthread_mutex_unlock.
    // -----------------------------------------------------------------
    void Unlock() noexcept;

private:
    // Opaque storage for the platform handle. The size is the max of
    // Win64 CRITICAL_SECTION (40 bytes), glibc pthread_mutex_t (40
    // bytes), and Android pthread_mutex_t (4 bytes), rounded up to
    // one cache line for false-sharing-padding and future headroom.
    static constexpr ::SIZE_T kStorageSize  = 64;
    static constexpr ::SIZE_T kStorageAlign = 16;

    alignas(kStorageAlign) ::std::byte m_storage[kStorageSize];
};

// ABI lock per Section 8.5. The wrapper's size + layout are the
// contract; never change. A platform that needs a larger handle bumps
// kStorageSize -- which is a MAJOR ABI bump and must be coordinated
// with XPACT_GC_ROOT_ABI_TAG (Macros/XPactMacros.h).
static_assert(sizeof(FCriticalSection)  == 64,
              "FCriticalSection ABI lock: 64 bytes (one cache line)");
static_assert(alignof(FCriticalSection) == 16,
              "FCriticalSection ABI lock: 16-byte alignment");

// ---------------------------------------------------------------------
// FScopedLock -- RAII helper for FCriticalSection.
//
// Pattern reference: UE Core HAL/CriticalSection.h had FScopeLock as
// a separate type. We ship FScopedLock here as a thin helper; the
// .h-only definition is sufficient since the type is value-only.
//
// Typical usage:
//   FCriticalSection MyMutex;
//   {
//       FScopedLock Lock(MyMutex);
//       // critical section
//   }  // Unlock on scope exit
// ---------------------------------------------------------------------

class FScopedLock
{
public:
    explicit FScopedLock(FCriticalSection& InMutex) noexcept
        : m_mutex(InMutex)
    {
        m_mutex.Lock();
    }

    ~FScopedLock() noexcept
    {
        m_mutex.Unlock();
    }

    FScopedLock(const FScopedLock&)            = delete;
    FScopedLock& operator=(const FScopedLock&) = delete;
    FScopedLock(FScopedLock&&)                 = delete;
    FScopedLock& operator=(FScopedLock&&)      = delete;

private:
    FCriticalSection& m_mutex;
};

} // namespace XCore::HAL
