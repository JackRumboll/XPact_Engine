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

#endif  // XPACT_PLATFORM_WIN64

} // namespace XCore::HAL
