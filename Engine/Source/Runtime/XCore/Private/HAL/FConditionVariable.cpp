// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FConditionVariable.cpp -- condvar bodies (Win64 + POSIX).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + fix M-5
// (Wait takes FMutex&, NOT FCriticalSection&).
//
// Implements the FConditionVariable wrapper declared in
// Public/HAL/FConditionVariable.h.
//
// Platform handles:
//   * Win64:    CONDITION_VARIABLE + SleepConditionVariableSRW.
//   * Linux:    pthread_cond_t + pthread_cond_wait.
//   * Android:  same as Linux.
//
// =====================================================================

#include "HAL/FConditionVariable.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"

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
    #include <errno.h>
    #include <time.h>
#endif

#include <new>

namespace XCore::HAL
{

#if XPACT_PLATFORM_WIN64

// ---------------------------------------------------------------------
// Win64 implementation: CONDITION_VARIABLE + SleepConditionVariableSRW.
//
// FMutex (Win64) wraps an SRWLOCK; SleepConditionVariableSRW is the
// matching condvar primitive. We pass the SRWLOCK handle obtained
// from FMutex::GetPlatformHandle().
// ---------------------------------------------------------------------

static_assert(sizeof(CONDITION_VARIABLE) <= 48,
              "CONDITION_VARIABLE exceeds FConditionVariable storage "
              "buffer size.");

namespace
{
    [[nodiscard]] CONDITION_VARIABLE* HandleOf(void* Storage) noexcept
    {
        return static_cast<CONDITION_VARIABLE*>(Storage);
    }

    [[nodiscard]] SRWLOCK* MutexHandle(FMutex& M) noexcept
    {
        return static_cast<SRWLOCK*>(M.GetPlatformHandle());
    }
}

FConditionVariable::FConditionVariable() noexcept
{
    CONDITION_VARIABLE* Handle =
        ::new (static_cast<void*>(m_storage)) CONDITION_VARIABLE{};
    ::InitializeConditionVariable(Handle);
}

FConditionVariable::~FConditionVariable() noexcept
{
    // CONDITION_VARIABLE has no destructor / no DeleteConditionVariable
    // -- the storage is released with the wrapper.
}

void FConditionVariable::Wait(FMutex& M) noexcept
{
    ::SleepConditionVariableSRW(
        HandleOf(m_storage),
        MutexHandle(M),
        INFINITE,
        0);  // 0 flags = exclusive lock mode (SRWLOCK was acquired
             // via AcquireSRWLockExclusive in FMutex::Lock).
}

bool FConditionVariable::WaitFor(FMutex& M, float TimeoutSeconds) noexcept
{
    DWORD WaitMs;
    if (TimeoutSeconds < 0.0f)
    {
        WaitMs = INFINITE;
    }
    else
    {
        const double MsDouble = static_cast<double>(TimeoutSeconds) * 1000.0;
        if (MsDouble >= static_cast<double>(MAXDWORD))
        {
            WaitMs = MAXDWORD - 1;
        }
        else if (MsDouble <= 0.0)
        {
            WaitMs = 0;
        }
        else
        {
            WaitMs = static_cast<DWORD>(MsDouble);
        }
    }

    const BOOL Ok = ::SleepConditionVariableSRW(
        HandleOf(m_storage),
        MutexHandle(M),
        WaitMs,
        0);
    return Ok != 0;
}

void FConditionVariable::NotifyOne() noexcept
{
    ::WakeConditionVariable(HandleOf(m_storage));
}

void FConditionVariable::NotifyAll() noexcept
{
    ::WakeAllConditionVariable(HandleOf(m_storage));
}

#else  // POSIX

// ---------------------------------------------------------------------
// POSIX implementation: pthread_cond_t.
//
// FMutex (POSIX) wraps a pthread_mutex_t with PTHREAD_MUTEX_NORMAL;
// pthread_cond_wait pairs with it natively. The non-recursive
// requirement is essential here -- if a caller passed a recursive
// mutex held at depth > 1, pthread_cond_wait's internal unlock
// (which only decrements the recursion counter once) would deadlock.
// The concept constraint on Wait at the header level prevents this.
// ---------------------------------------------------------------------

static_assert(sizeof(pthread_cond_t) <= 48,
              "pthread_cond_t exceeds FConditionVariable storage "
              "buffer size.");

namespace
{
    [[nodiscard]] pthread_cond_t* HandleOf(void* Storage) noexcept
    {
        return static_cast<pthread_cond_t*>(Storage);
    }

    [[nodiscard]] pthread_mutex_t* MutexHandle(FMutex& M) noexcept
    {
        return static_cast<pthread_mutex_t*>(M.GetPlatformHandle());
    }
}

FConditionVariable::FConditionVariable() noexcept
{
    pthread_cond_t* Handle =
        ::new (static_cast<void*>(m_storage)) pthread_cond_t{};
    pthread_cond_init(Handle, nullptr);
}

FConditionVariable::~FConditionVariable() noexcept
{
    pthread_cond_destroy(HandleOf(m_storage));
}

void FConditionVariable::Wait(FMutex& M) noexcept
{
    pthread_cond_wait(HandleOf(m_storage), MutexHandle(M));
}

bool FConditionVariable::WaitFor(FMutex& M, float TimeoutSeconds) noexcept
{
    if (TimeoutSeconds < 0.0f)
    {
        // Infinite wait; pthread_cond_wait returns 0 on signal,
        // non-zero on error. Treat any return as "notified" (the
        // caller re-checks the predicate).
        pthread_cond_wait(HandleOf(m_storage), MutexHandle(M));
        return true;
    }

    // Compute absolute deadline. CLOCK_REALTIME matches the default
    // clock domain for pthread_cond_timedwait (a portable choice;
    // CLOCK_MONOTONIC could be selected via pthread_condattr_setclock
    // at init time, but the wall-clock drift on the millisecond
    // scale typical for our timeouts is negligible).
    struct timespec Deadline;
    clock_gettime(CLOCK_REALTIME, &Deadline);

    const long WholeSeconds = static_cast<long>(TimeoutSeconds);
    const double Fractional = static_cast<double>(TimeoutSeconds)
                            - static_cast<double>(WholeSeconds);
    const long Nanos = static_cast<long>(Fractional * 1e9);

    Deadline.tv_sec  += WholeSeconds;
    Deadline.tv_nsec += Nanos;
    if (Deadline.tv_nsec >= 1000000000L)
    {
        Deadline.tv_sec  += 1;
        Deadline.tv_nsec -= 1000000000L;
    }

    const int Err = pthread_cond_timedwait(
        HandleOf(m_storage), MutexHandle(M), &Deadline);
    return Err == 0;  // 0 = signalled; ETIMEDOUT = timed out.
}

void FConditionVariable::NotifyOne() noexcept
{
    pthread_cond_signal(HandleOf(m_storage));
}

void FConditionVariable::NotifyAll() noexcept
{
    pthread_cond_broadcast(HandleOf(m_storage));
}

#endif  // XPACT_PLATFORM_WIN64

} // namespace XCore::HAL
