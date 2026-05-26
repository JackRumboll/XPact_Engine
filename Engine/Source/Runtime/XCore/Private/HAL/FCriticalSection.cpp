// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCriticalSection.cpp -- recursive mutex bodies (Win64 + POSIX).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + Section 8.2
// (always-recursive across platforms).
//
// Implements the FCriticalSection wrapper declared in
// Public/HAL/FCriticalSection.h. The wrapper holds a 64-byte opaque
// storage buffer; this .cpp performs placement-new of the platform
// handle into that buffer and routes Lock/TryLock/Unlock to the
// platform primitive.
//
// Platform handles:
//   * Win64:    CRITICAL_SECTION (natively recursive).
//   * Linux:    pthread_mutex_t with PTHREAD_MUTEX_RECURSIVE attribute.
//   * Android:  same as Linux (Bionic pthread).
//
// =====================================================================

#include "HAL/FCriticalSection.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"
#include "HAL/FPlatformTime.h"
#include "HAL/FTimespan.h"

#if XPACT_PLATFORM_WIN64
    // Windows.h defines a forest of macros that conflict with C++
    // identifier names elsewhere in the engine (e.g., `min` / `max`,
    // `GetMessage`, `CreateWindow`). The NOMINMAX + lean-and-mean
    // pattern is standard.
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
// Win64 implementation: CRITICAL_SECTION.
//
// Pattern reference: UE Core Public/Windows/WindowsPlatformMutex.h:25
// (FWindowsRecursiveMutex constructor) -- uses
// InitializeCriticalSectionAndSpinCount(&cs, 4000). We adopt the same
// 4000-iteration spin count to match UE's tuned behaviour (UE chose
// this value based on long-running profiling of game-thread
// contention; it amortises the syscall cost of contended Lock without
// burning excessive CPU under uncontended hold-and-release).
// ---------------------------------------------------------------------

static_assert(sizeof(CRITICAL_SECTION) <= 64,
              "CRITICAL_SECTION exceeds FCriticalSection's opaque storage "
              "buffer size; bump FCriticalSection::kStorageSize and "
              "coordinate the ABI bump with XPACT_GC_ROOT_ABI_TAG.");

namespace
{
    [[nodiscard]] CRITICAL_SECTION* HandleOf(void* Storage) noexcept
    {
        return static_cast<CRITICAL_SECTION*>(Storage);
    }
}

FCriticalSection::FCriticalSection() noexcept
{
    // Placement-new the CRITICAL_SECTION into our opaque storage. The
    // CRITICAL_SECTION type has no virtual methods and no
    // destructor-needs-running state; placement-new initialises the
    // fields the Init... call will overwrite.
    //
    // We initialise via InitializeCriticalSectionAndSpinCount because
    // it's the API Microsoft documents for production critical
    // sections. The 4000 spin count matches UE's tuned value.
    CRITICAL_SECTION* Handle = ::new (static_cast<void*>(m_storage)) CRITICAL_SECTION{};
    ::InitializeCriticalSectionAndSpinCount(Handle, 4000);
}

FCriticalSection::~FCriticalSection() noexcept
{
    ::DeleteCriticalSection(HandleOf(m_storage));
    // No need to call the CRITICAL_SECTION destructor explicitly --
    // it's a POD type and DeleteCriticalSection releases the kernel
    // resources. The placement-new'd memory is the wrapper's
    // m_storage buffer; lifetime ends with the wrapper.
}

void FCriticalSection::Lock() noexcept
{
    ::EnterCriticalSection(HandleOf(m_storage));
}

bool FCriticalSection::TryLock() noexcept
{
    return ::TryEnterCriticalSection(HandleOf(m_storage)) != 0;
}

void FCriticalSection::Unlock() noexcept
{
    ::LeaveCriticalSection(HandleOf(m_storage));
}

// ---------------------------------------------------------------------
// Win64 timed-lock helper (Rev 1 audit HIGH-2 close-out).
//
// CRITICAL_SECTION has no native timed-acquire API
// (TryEnterCriticalSection is non-blocking only). We emulate by
// polling TryEnterCriticalSection in a graded-backoff loop bounded
// by FPlatformTime::Seconds() + Timeout. Recursive semantics are
// preserved naturally: TryEnterCriticalSection on a thread that
// already holds the section returns immediately with the recursion
// counter incremented, so the very first TryLock in the spin loop
// succeeds without consuming any timeout.
//
// Same graded-backoff tiers as FRWLock/FMutex; see FRWLock.cpp's
// comment block for rationale.
// ---------------------------------------------------------------------
namespace
{
    constexpr int kSpinIterationsCS  = 64;
    constexpr int kYieldIterationsCS = 32;

    [[nodiscard]] bool WaitDeadlineExpiredCS(double DeadlineSeconds) noexcept
    {
        return ::XCore::HAL::FPlatformTime::Seconds() >= DeadlineSeconds;
    }
}

bool FCriticalSection::TryLockFor(FTimespan Timeout) noexcept
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

    for (int i = 0; i < kSpinIterationsCS; ++i)
    {
        if (TryLock())
        {
            return true;
        }
        ::YieldProcessor();
        if (WaitDeadlineExpiredCS(Deadline))
        {
            return false;
        }
    }

    for (int i = 0; i < kYieldIterationsCS; ++i)
    {
        if (TryLock())
        {
            return true;
        }
        ::Sleep(0);
        if (WaitDeadlineExpiredCS(Deadline))
        {
            return false;
        }
    }

    while (!WaitDeadlineExpiredCS(Deadline))
    {
        if (TryLock())
        {
            return true;
        }
        ::Sleep(1);
    }
    return TryLock();
}

#else  // POSIX (Linux + Android)

// ---------------------------------------------------------------------
// POSIX implementation: pthread_mutex_t with PTHREAD_MUTEX_RECURSIVE.
//
// Pattern reference: UE Core Public/HAL/PThreadsRecursiveMutex.h:28-36
// FPThreadsRecursiveMutex constructor:
//
//     pthread_mutexattr_t MutexAttributes;
//     pthread_mutexattr_init(&MutexAttributes);
//     pthread_mutexattr_settype(&MutexAttributes, PTHREAD_MUTEX_RECURSIVE);
//     pthread_mutex_init(&Mutex, &MutexAttributes);
//     pthread_mutexattr_destroy(&MutexAttributes);
//
// XPact's body is functionally identical; we name the local handle
// after the wrapper's storage and add the static_assert for the
// opaque-storage buffer size.
// ---------------------------------------------------------------------

static_assert(sizeof(pthread_mutex_t) <= 64,
              "pthread_mutex_t exceeds FCriticalSection's opaque storage "
              "buffer size; bump FCriticalSection::kStorageSize and "
              "coordinate the ABI bump with XPACT_GC_ROOT_ABI_TAG.");

namespace
{
    [[nodiscard]] pthread_mutex_t* HandleOf(void* Storage) noexcept
    {
        return static_cast<pthread_mutex_t*>(Storage);
    }
}

FCriticalSection::FCriticalSection() noexcept
{
    pthread_mutex_t* Handle = ::new (static_cast<void*>(m_storage)) pthread_mutex_t{};

    pthread_mutexattr_t Attrs;
    pthread_mutexattr_init(&Attrs);
    pthread_mutexattr_settype(&Attrs, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(Handle, &Attrs);
    pthread_mutexattr_destroy(&Attrs);
}

FCriticalSection::~FCriticalSection() noexcept
{
    pthread_mutex_destroy(HandleOf(m_storage));
}

void FCriticalSection::Lock() noexcept
{
    pthread_mutex_lock(HandleOf(m_storage));
}

bool FCriticalSection::TryLock() noexcept
{
    return pthread_mutex_trylock(HandleOf(m_storage)) == 0;
}

void FCriticalSection::Unlock() noexcept
{
    pthread_mutex_unlock(HandleOf(m_storage));
}

// ---------------------------------------------------------------------
// POSIX timed-lock helper (Rev 1 audit HIGH-2 close-out).
//
// pthread_mutex_timedlock takes an absolute-deadline timespec
// (CLOCK_REALTIME). Recursive semantics are preserved by the
// PTHREAD_MUTEX_RECURSIVE attribute.
// ---------------------------------------------------------------------
bool FCriticalSection::TryLockFor(FTimespan Timeout) noexcept
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
