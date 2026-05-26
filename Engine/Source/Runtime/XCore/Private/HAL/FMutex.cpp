// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMutex.cpp -- non-recursive mutex bodies (Win64 + POSIX).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + fix M-5.
//
// Implements the FMutex wrapper declared in Public/HAL/FMutex.h.
// FMutex is the NON-RECURSIVE companion to FCriticalSection; required
// by FConditionVariable per fix M-5 (pthread_cond_wait on a recursive
// mutex held at depth > 1 is UB).
//
// Platform handles:
//   * Win64:    SRWLOCK (Slim Reader/Writer Lock; non-recursive).
//               Pattern reference: UE Core Public/Windows/
//               WindowsPlatformMutex.h:62 FWindowsSharedMutex (which
//               uses SRWLOCK in exclusive-only mode).
//   * Linux:    pthread_mutex_t with PTHREAD_MUTEX_NORMAL attribute.
//   * Android:  same as Linux.
//
// =====================================================================

#include "HAL/FMutex.h"

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
// Win64 implementation: SRWLOCK in exclusive-only mode.
//
// SRWLock is natively non-recursive: a thread that acquires it
// exclusively and attempts to re-acquire deadlocks. This is the
// FMutex contract by design.
//
// SRWLOCK is 8 bytes (one pointer); easily fits in our 48-byte
// opaque storage buffer.
// ---------------------------------------------------------------------

static_assert(sizeof(SRWLOCK) <= 48,
              "SRWLOCK exceeds FMutex's opaque storage buffer size; "
              "bump FMutex::kStorageSize and coordinate the ABI bump.");

namespace
{
    [[nodiscard]] SRWLOCK* HandleOf(void* Storage) noexcept
    {
        return static_cast<SRWLOCK*>(Storage);
    }
}

FMutex::FMutex() noexcept
{
    SRWLOCK* Handle = ::new (static_cast<void*>(m_storage)) SRWLOCK{};
    ::InitializeSRWLock(Handle);
}

FMutex::~FMutex() noexcept
{
    // SRWLOCK has no destructor / no DeleteSRWLock equivalent --
    // it's a simple POD. The storage is released with the wrapper.
}

void FMutex::Lock() noexcept
{
    ::AcquireSRWLockExclusive(HandleOf(m_storage));
}

bool FMutex::TryLock() noexcept
{
    return ::TryAcquireSRWLockExclusive(HandleOf(m_storage)) != 0;
}

void FMutex::Unlock() noexcept
{
    ::ReleaseSRWLockExclusive(HandleOf(m_storage));
}

// ---------------------------------------------------------------------
// Win64 timed-lock helper (Rev 1 audit HIGH-2 close-out).
//
// SRWLock has no native timed-acquire API. Same emulation pattern as
// FRWLock/FCriticalSection: TryAcquire + graded-backoff spin bounded
// by FPlatformTime::Seconds() + Timeout.
// ---------------------------------------------------------------------
namespace
{
    constexpr int kSpinIterationsFM  = 64;
    constexpr int kYieldIterationsFM = 32;

    [[nodiscard]] bool WaitDeadlineExpiredFM(double DeadlineSeconds) noexcept
    {
        return ::XCore::HAL::FPlatformTime::Seconds() >= DeadlineSeconds;
    }
}

bool FMutex::TryLockFor(FTimespan Timeout) noexcept
{
    if (TryLock())
    {
        return true;
    }

    if (Timeout.TotalMicroseconds() <= 0)
    {
        return false;
    }

    const double TimeoutSeconds = static_cast<double>(Timeout.TotalMicroseconds()) * 1e-6;
    const double Deadline       = ::XCore::HAL::FPlatformTime::Seconds() + TimeoutSeconds;

    for (int i = 0; i < kSpinIterationsFM; ++i)
    {
        if (TryLock())
        {
            return true;
        }
        ::YieldProcessor();
        if (WaitDeadlineExpiredFM(Deadline))
        {
            return false;
        }
    }

    for (int i = 0; i < kYieldIterationsFM; ++i)
    {
        if (TryLock())
        {
            return true;
        }
        ::Sleep(0);
        if (WaitDeadlineExpiredFM(Deadline))
        {
            return false;
        }
    }

    while (!WaitDeadlineExpiredFM(Deadline))
    {
        if (TryLock())
        {
            return true;
        }
        ::Sleep(1);
    }
    return TryLock();
}

#else  // POSIX

// ---------------------------------------------------------------------
// POSIX implementation: pthread_mutex_t with PTHREAD_MUTEX_NORMAL.
//
// PTHREAD_MUTEX_NORMAL is the strict-non-recursive type: a thread
// that re-locks deadlocks (or returns EDEADLK on some platforms; the
// behaviour is implementation-defined, but UB-with-deadlock is the
// safe default to assume).
//
// We could also use PTHREAD_MUTEX_DEFAULT which on glibc maps to
// NORMAL but on other POSIX systems may map to ERRORCHECK or
// RECURSIVE; we use NORMAL explicitly to guarantee the behaviour.
// ---------------------------------------------------------------------

static_assert(sizeof(pthread_mutex_t) <= 48,
              "pthread_mutex_t exceeds FMutex's opaque storage buffer "
              "size; bump FMutex::kStorageSize and coordinate the ABI "
              "bump.");

namespace
{
    [[nodiscard]] pthread_mutex_t* HandleOf(void* Storage) noexcept
    {
        return static_cast<pthread_mutex_t*>(Storage);
    }
}

FMutex::FMutex() noexcept
{
    pthread_mutex_t* Handle = ::new (static_cast<void*>(m_storage)) pthread_mutex_t{};

    pthread_mutexattr_t Attrs;
    pthread_mutexattr_init(&Attrs);
    pthread_mutexattr_settype(&Attrs, PTHREAD_MUTEX_NORMAL);
    pthread_mutex_init(Handle, &Attrs);
    pthread_mutexattr_destroy(&Attrs);
}

FMutex::~FMutex() noexcept
{
    pthread_mutex_destroy(HandleOf(m_storage));
}

void FMutex::Lock() noexcept
{
    pthread_mutex_lock(HandleOf(m_storage));
}

bool FMutex::TryLock() noexcept
{
    return pthread_mutex_trylock(HandleOf(m_storage)) == 0;
}

void FMutex::Unlock() noexcept
{
    pthread_mutex_unlock(HandleOf(m_storage));
}

// ---------------------------------------------------------------------
// POSIX timed-lock helper (Rev 1 audit HIGH-2 close-out).
//
// pthread_mutex_timedlock with absolute-deadline timespec
// (CLOCK_REALTIME).
// ---------------------------------------------------------------------
bool FMutex::TryLockFor(FTimespan Timeout) noexcept
{
    if (Timeout.TotalMicroseconds() <= 0)
    {
        return TryLock();
    }

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

    const int Result = pthread_mutex_timedlock(HandleOf(m_storage), &Deadline);
    return Result == 0;
}

#endif  // XPACT_PLATFORM_WIN64

} // namespace XCore::HAL
