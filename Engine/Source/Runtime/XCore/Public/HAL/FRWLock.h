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
