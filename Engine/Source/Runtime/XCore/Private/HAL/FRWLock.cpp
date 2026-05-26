// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FRWLock.cpp -- shared-mutex bodies (Win64 + POSIX).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + Section 8.2.
//
// Implements the FRWLock wrapper declared in Public/HAL/FRWLock.h.
//
// Platform handles:
//   * Win64:    SRWLOCK (natively supports shared + exclusive).
//   * Linux:    pthread_rwlock_t.
//   * Android:  same as Linux.
//
// Per Section 8.2: "XPact's wrapper standardises on writer-preferring
// across all three to avoid reader-starvation surprises." We
// approximate writer-preferring on POSIX via
// PTHREAD_RWLOCK_PREFER_WRITER_NP (glibc extension) when available;
// fall back to defaults on platforms that don't support the
// attribute. SRWLock on Win64 has no documented preference setting --
// it's adaptive.
//
// =====================================================================

#include "HAL/FRWLock.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"
#include "HAL/FPlatformTime.h"
#include "HAL/FTimespan.h"

#if XPACT_PLATFORM_WIN64
    #ifndef WIN32_LEAN_AND_MEAN
        #define WIN32_LEAN_AND_MEAN
    #endif
    #ifndef NOMINMAX
        #define NOMINMAX
    #endif
    #include <Windows.h>
#else
    #include <pthread.h>
    #include <time.h>
    #include <errno.h>
#endif

#include <new>

namespace XCore::HAL
{

#if XPACT_PLATFORM_WIN64

// ---------------------------------------------------------------------
// Win64 implementation: SRWLOCK with both shared and exclusive APIs.
//
// Pattern reference: UE Core Public/Windows/WindowsPlatformMutex.h:62
// FWindowsSharedMutex.
// ---------------------------------------------------------------------

static_assert(sizeof(SRWLOCK) <= 64,
              "SRWLOCK exceeds FRWLock's opaque storage buffer size.");

namespace
{
    [[nodiscard]] SRWLOCK* HandleOf(void* Storage) noexcept
    {
        return static_cast<SRWLOCK*>(Storage);
    }
}

FRWLock::FRWLock() noexcept
{
    SRWLOCK* Handle = ::new (static_cast<void*>(m_storage)) SRWLOCK{};
    ::InitializeSRWLock(Handle);
}

FRWLock::~FRWLock() noexcept
{
    // SRWLOCK has no destructor; storage released with wrapper.
}

void FRWLock::LockShared() noexcept
{
    ::AcquireSRWLockShared(HandleOf(m_storage));
}

void FRWLock::UnlockShared() noexcept
{
    ::ReleaseSRWLockShared(HandleOf(m_storage));
}

void FRWLock::LockExclusive() noexcept
{
    ::AcquireSRWLockExclusive(HandleOf(m_storage));
}

void FRWLock::UnlockExclusive() noexcept
{
    ::ReleaseSRWLockExclusive(HandleOf(m_storage));
}

bool FRWLock::TryLockShared() noexcept
{
    return ::TryAcquireSRWLockShared(HandleOf(m_storage)) != 0;
}

bool FRWLock::TryLockExclusive() noexcept
{
    return ::TryAcquireSRWLockExclusive(HandleOf(m_storage)) != 0;
}

// ---------------------------------------------------------------------
// Win64 timed-lock helpers (Rev 1 audit HIGH-2 close-out).
//
// SRWLock has no native timed-acquire API. We emulate by polling
// TryAcquire* in a graded-backoff loop bounded by
// FPlatformTime::Seconds() + Timeout. The graded backoff is the
// standard Win64 idiom:
//   * spin (CPU pause via YieldProcessor) for the first ~K iterations,
//   * Sleep(0) (yield to ready-state threads on the same core) for
//     the next ~K iterations,
//   * Sleep(1) thereafter (a true sleep of one OS scheduling quantum,
//     ~1 ms on default Windows).
//
// The granularity is ~1 ms in the Sleep(1) tier; sub-millisecond
// timeouts get one or two TryLock attempts in the spin / Sleep(0)
// tiers and then return false.
//
// Phase 2+ scope: replace with WaitOnAddress on the SRWLock's
// internal pointer; would give microsecond-grade precision. Out of
// scope for Rev 1 because it requires the WaitOnAddress facility's
// well-defined interaction with the SRWLock's internal state, which
// Microsoft does not document publicly.
// ---------------------------------------------------------------------
namespace
{
    constexpr int kSpinIterations  = 64;
    constexpr int kYieldIterations = 32;

    [[nodiscard]] bool WaitDeadlineExpired(double DeadlineSeconds) noexcept
    {
        return ::XCore::HAL::FPlatformTime::Seconds() >= DeadlineSeconds;
    }
}

bool FRWLock::TryLockSharedFor(FTimespan Timeout) noexcept
{
    // Fast path: try once non-blocking.
    if (TryLockShared())
    {
        return true;
    }

    if (Timeout.TotalMicroseconds() <= 0)
    {
        return false;
    }

    const double TimeoutSeconds = static_cast<double>(Timeout.TotalMicroseconds()) * 1e-6;
    const double Deadline       = ::XCore::HAL::FPlatformTime::Seconds() + TimeoutSeconds;

    // Tier 1: tight spin with YieldProcessor (CPU PAUSE on x86_64).
    for (int i = 0; i < kSpinIterations; ++i)
    {
        if (TryLockShared())
        {
            return true;
        }
        ::YieldProcessor();
        if (WaitDeadlineExpired(Deadline))
        {
            return false;
        }
    }

    // Tier 2: Sleep(0) -- yield to ready threads on the same core.
    for (int i = 0; i < kYieldIterations; ++i)
    {
        if (TryLockShared())
        {
            return true;
        }
        ::Sleep(0);
        if (WaitDeadlineExpired(Deadline))
        {
            return false;
        }
    }

    // Tier 3: Sleep(1) until deadline. The granularity floor is ~1 ms.
    while (!WaitDeadlineExpired(Deadline))
    {
        if (TryLockShared())
        {
            return true;
        }
        ::Sleep(1);
    }
    // Final attempt after the deadline expired.
    return TryLockShared();
}

bool FRWLock::TryLockExclusiveFor(FTimespan Timeout) noexcept
{
    if (TryLockExclusive())
    {
        return true;
    }

    if (Timeout.TotalMicroseconds() <= 0)
    {
        return false;
    }

    const double TimeoutSeconds = static_cast<double>(Timeout.TotalMicroseconds()) * 1e-6;
    const double Deadline       = ::XCore::HAL::FPlatformTime::Seconds() + TimeoutSeconds;

    for (int i = 0; i < kSpinIterations; ++i)
    {
        if (TryLockExclusive())
        {
            return true;
        }
        ::YieldProcessor();
        if (WaitDeadlineExpired(Deadline))
        {
            return false;
        }
    }

    for (int i = 0; i < kYieldIterations; ++i)
    {
        if (TryLockExclusive())
        {
            return true;
        }
        ::Sleep(0);
        if (WaitDeadlineExpired(Deadline))
        {
            return false;
        }
    }

    while (!WaitDeadlineExpired(Deadline))
    {
        if (TryLockExclusive())
        {
            return true;
        }
        ::Sleep(1);
    }
    return TryLockExclusive();
}

#else  // POSIX

// ---------------------------------------------------------------------
// POSIX implementation: pthread_rwlock_t.
//
// Pattern reference: UE Core Public/HAL/PThreadsSharedMutex.h:29
// FPThreadsSharedMutex.
//
// Writer-preference: PTHREAD_RWLOCK_PREFER_WRITER_NP is a glibc
// extension (NP = "non-portable"). On platforms that define it
// (Linux glibc) we set the preference. On Android (Bionic) and
// other POSIX targets that may lack the symbol, we fall through to
// the default policy. The static_assert chain below picks the
// right code path at compile time.
// ---------------------------------------------------------------------

static_assert(sizeof(pthread_rwlock_t) <= 64,
              "pthread_rwlock_t exceeds FRWLock's opaque storage buffer "
              "size.");

namespace
{
    [[nodiscard]] pthread_rwlock_t* HandleOf(void* Storage) noexcept
    {
        return static_cast<pthread_rwlock_t*>(Storage);
    }
}

FRWLock::FRWLock() noexcept
{
    pthread_rwlock_t* Handle = ::new (static_cast<void*>(m_storage)) pthread_rwlock_t{};

    pthread_rwlockattr_t Attrs;
    pthread_rwlockattr_init(&Attrs);

#if defined(PTHREAD_RWLOCK_PREFER_WRITER_NP)
    // glibc extension; sets writer preference to avoid reader-
    // starvation under heavy reader load.
    pthread_rwlockattr_setkind_np(&Attrs, PTHREAD_RWLOCK_PREFER_WRITER_NP);
#endif

    pthread_rwlock_init(Handle, &Attrs);
    pthread_rwlockattr_destroy(&Attrs);
}

FRWLock::~FRWLock() noexcept
{
    pthread_rwlock_destroy(HandleOf(m_storage));
}

void FRWLock::LockShared() noexcept
{
    pthread_rwlock_rdlock(HandleOf(m_storage));
}

void FRWLock::UnlockShared() noexcept
{
    pthread_rwlock_unlock(HandleOf(m_storage));
}

void FRWLock::LockExclusive() noexcept
{
    pthread_rwlock_wrlock(HandleOf(m_storage));
}

void FRWLock::UnlockExclusive() noexcept
{
    pthread_rwlock_unlock(HandleOf(m_storage));
}

bool FRWLock::TryLockShared() noexcept
{
    return pthread_rwlock_tryrdlock(HandleOf(m_storage)) == 0;
}

bool FRWLock::TryLockExclusive() noexcept
{
    return pthread_rwlock_trywrlock(HandleOf(m_storage)) == 0;
}

// ---------------------------------------------------------------------
// POSIX timed-lock helpers (Rev 1 audit HIGH-2 close-out).
//
// pthread_rwlock_timedrdlock + pthread_rwlock_timedwrlock take an
// absolute-deadline timespec (CLOCK_REALTIME by default unless the
// rwlockattr is set otherwise). We compose now-plus-timeout in
// CLOCK_REALTIME to match the rwlock's default clock domain.
// ---------------------------------------------------------------------
namespace
{
    [[nodiscard]] struct timespec ComputeAbsoluteDeadline(::XCore::HAL::FTimespan Timeout) noexcept
    {
        struct timespec Deadline;
        clock_gettime(CLOCK_REALTIME, &Deadline);

        const ::int64 TotalMicros = Timeout.TotalMicroseconds();
        const ::int64 WholeSecs   = TotalMicros / 1000000;
        const ::int64 SubMicros   = TotalMicros - (WholeSecs * 1000000);
        const ::int64 SubNanos    = SubMicros * 1000;

        Deadline.tv_sec  += static_cast<time_t>(WholeSecs);
        Deadline.tv_nsec += static_cast<long>(SubNanos);
        if (Deadline.tv_nsec >= 1000000000L)
        {
            Deadline.tv_sec  += 1;
            Deadline.tv_nsec -= 1000000000L;
        }
        return Deadline;
    }
}

bool FRWLock::TryLockSharedFor(FTimespan Timeout) noexcept
{
    if (Timeout.TotalMicroseconds() <= 0)
    {
        return TryLockShared();
    }
    const struct timespec Deadline = ComputeAbsoluteDeadline(Timeout);
    const int Result = pthread_rwlock_timedrdlock(HandleOf(m_storage), &Deadline);
    // Returns 0 on success; ETIMEDOUT on deadline expiry; other errno
    // values on bug. We treat any non-zero return as "did not acquire"
    // and return false; the OS surface differentiates ETIMEDOUT vs
    // bug-class errors but our public contract is binary acquire/fail.
    return Result == 0;
}

bool FRWLock::TryLockExclusiveFor(FTimespan Timeout) noexcept
{
    if (Timeout.TotalMicroseconds() <= 0)
    {
        return TryLockExclusive();
    }
    const struct timespec Deadline = ComputeAbsoluteDeadline(Timeout);
    const int Result = pthread_rwlock_timedwrlock(HandleOf(m_storage), &Deadline);
    return Result == 0;
}

#endif  // XPACT_PLATFORM_WIN64

} // namespace XCore::HAL
