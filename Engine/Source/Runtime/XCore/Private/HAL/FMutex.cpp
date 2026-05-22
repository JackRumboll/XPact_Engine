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

#endif  // XPACT_PLATFORM_WIN64

} // namespace XCore::HAL
