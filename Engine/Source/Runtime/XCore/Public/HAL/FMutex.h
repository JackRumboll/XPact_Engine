// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMutex.h -- non-recursive mutex (FConditionVariable's pair partner).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + fix M-5
// (non-recursive variant for FConditionVariable pairing).
//
// FMutex is the non-recursive companion to FCriticalSection (Section
// 8.1). A thread that already holds an FMutex and calls Lock again
// deadlocks (or asserts in Debug, depending on platform OS behaviour).
//
// Required by FConditionVariable (Section 8.1 fix M-5): pthread_cond_
// wait on a recursive mutex held at recursion depth > 1 is UB at the
// POSIX level -- the condvar internally unlocks the mutex once, but a
// recursive mutex held N times needs N unlocks to fully release. The
// condvar's unlock-then-block sequence is therefore incorrect when
// the mutex is recursive. POSIX documents the UB; XPact closes it by
// concept-constraining FConditionVariable::Wait to admit FMutex only.
//
// Backing primitive per platform:
//   * Win64:    Win32 SRWLock (Slim Reader/Writer Lock). SRWLock is
//               natively non-recursive (acquiring twice from the
//               same thread deadlocks).
//   * Linux:    pthread_mutex_t initialised with PTHREAD_MUTEX_NORMAL.
//   * Android:  same as Linux (Bionic pthread).
//
// Hot-reload (Section 8.5): NO virtual methods; POD-like wrapper.
// static_assert pinning the size + alignment.
//
// =====================================================================
//
// IMPLEMENTATION NOTES:
//
// Same Public-header pattern as FCriticalSection: opaque storage
// buffer; .cpp performs placement-new of the platform handle.
//
// Sizes (verified against platform SDK headers):
//   * Win64 SRWLOCK:                  8 bytes / 8-byte alignment.
//   * Linux  pthread_mutex_t (glibc): 40 bytes / 8-byte alignment.
//   * Android pthread_mutex_t:        4 bytes / 4-byte alignment.
//
// CHOSEN BUFFER SIZE: 48 bytes / 8-byte alignment.
//   * Covers all three handle sizes with headroom.
//   * 8-byte alignment is sufficient.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "HAL/FTimespan.h"

#include <cstddef>

namespace XCore::HAL
{

class FMutex
{
public:
    FMutex() noexcept;
    ~FMutex() noexcept;

    FMutex(const FMutex&)            = delete;
    FMutex& operator=(const FMutex&) = delete;
    FMutex(FMutex&&)                 = delete;
    FMutex& operator=(FMutex&&)      = delete;

    // -----------------------------------------------------------------
    // Lock -- acquire the mutex; blocks if held by another thread.
    //
    // NON-RECURSIVE: a thread that already holds the mutex and calls
    // Lock will deadlock. This is by design -- FConditionVariable
    // requires a non-recursive mutex for correct semantics.
    //
    // Win64: AcquireSRWLockExclusive.
    // POSIX: pthread_mutex_lock on PTHREAD_MUTEX_NORMAL.
    // -----------------------------------------------------------------
    void Lock() noexcept;

    // -----------------------------------------------------------------
    // TryLock -- attempt to acquire without blocking.
    //
    // Win64: TryAcquireSRWLockExclusive.
    // POSIX: pthread_mutex_trylock returning 0.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLock() noexcept;

    // -----------------------------------------------------------------
    // TryLockFor -- bounded-wait variant (Rev 1 audit HIGH-2
    // close-out).
    //
    // Returns true if the mutex was acquired within the timeout, false
    // if the timeout expired without acquiring.
    //
    // Per-platform behaviour:
    //
    //   * POSIX: native pthread_mutex_timedlock with
    //            absolute-deadline CLOCK_REALTIME timespec. Precise to
    //            OS scheduler granularity (~1 ms or better).
    //
    //   * Win64: SRWLock does NOT support timed acquire natively. The
    //            implementation degrades to a TryAcquireSRWLockExclusive
    //            + spin-yield loop bounded by FPlatformTime::Seconds()
    //            with graded Sleep(0)/Sleep(1) backoff. Wait
    //            granularity is ~1 ms on Win64; sub-ms timeouts behave
    //            as non-blocking TryLock attempts in tight succession.
    //
    // Timeout semantics: a Timeout with TotalMicroseconds() <= 0 is a
    // non-blocking TryLock (no spin; one TryLock attempt).
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryLockFor(FTimespan Timeout) noexcept;

    // -----------------------------------------------------------------
    // Unlock -- release the mutex.
    //
    // Win64: ReleaseSRWLockExclusive.
    // POSIX: pthread_mutex_unlock.
    // -----------------------------------------------------------------
    void Unlock() noexcept;

    // -----------------------------------------------------------------
    // GetPlatformHandle -- internal access for FConditionVariable.
    //
    // FConditionVariable's Wait/WaitFor needs to pass the mutex's
    // native handle to pthread_cond_wait / SleepConditionVariableSRW.
    // Exposing the opaque-handle pointer here lets the condvar
    // implementation cast it back to the platform type inside the
    // .cpp without exposing platform types in the Public surface.
    //
    // Pointer is stable for the lifetime of the mutex. Marked
    // internal-only by virtue of returning void*; user code shouldn't
    // touch this.
    // -----------------------------------------------------------------
    [[nodiscard]] void* GetPlatformHandle() noexcept
    {
        return static_cast<void*>(m_storage);
    }

private:
    static constexpr ::SIZE_T kStorageSize  = 48;
    static constexpr ::SIZE_T kStorageAlign = 8;

    alignas(kStorageAlign) ::std::byte m_storage[kStorageSize];
};

static_assert(sizeof(FMutex)  == 48, "FMutex ABI lock: 48 bytes");
static_assert(alignof(FMutex) == 8,  "FMutex ABI lock: 8-byte alignment");

// ---------------------------------------------------------------------
// XIsNonRecursiveMutex concept (fix M-5).
//
// FConditionVariable::Wait is concept-constrained to admit only
// non-recursive mutexes. The concept is satisfied by FMutex (which
// is non-recursive by design) and NOT satisfied by FCriticalSection
// (which is recursive). Compile-time enforcement closes the
// pthread_cond_wait-with-recursive-mutex UB pairing.
//
// Implementation: a free variable template `kIsNonRecursiveMutexV<T>`
// that defaults to `false` and is specialised to `true` for the
// non-recursive mutex types. FCriticalSection has NO specialisation,
// so it remains false and fails the concept. This mirrors the
// standard library's trait-specialisation pattern (is_trivial_v, etc.)
// rather than requiring the mutex type to expose a member trait.
//
// To add a new non-recursive mutex type T:
//     template<> inline constexpr bool kIsNonRecursiveMutexV<T> = true;
// in the header where T is declared.
// ---------------------------------------------------------------------

template<typename T>
inline constexpr bool kIsNonRecursiveMutexV = false;

template<>
inline constexpr bool kIsNonRecursiveMutexV<FMutex> = true;

template<typename T>
concept XIsNonRecursiveMutex = kIsNonRecursiveMutexV<T>;

// ---------------------------------------------------------------------
// FScopedMutexLock -- RAII helper for FMutex.
//
// Same pattern as FScopedLock for FCriticalSection. Named distinctly
// so the call site is unambiguous about WHICH mutex type is held.
// ---------------------------------------------------------------------

class FScopedMutexLock
{
public:
    explicit FScopedMutexLock(FMutex& InMutex) noexcept
        : m_mutex(InMutex)
    {
        m_mutex.Lock();
    }

    ~FScopedMutexLock() noexcept
    {
        m_mutex.Unlock();
    }

    FScopedMutexLock(const FScopedMutexLock&)            = delete;
    FScopedMutexLock& operator=(const FScopedMutexLock&) = delete;
    FScopedMutexLock(FScopedMutexLock&&)                 = delete;
    FScopedMutexLock& operator=(FScopedMutexLock&&)      = delete;

private:
    FMutex& m_mutex;
};

} // namespace XCore::HAL
