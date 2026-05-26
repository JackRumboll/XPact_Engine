// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FEvent.cpp -- waitable event bodies (Win64 + POSIX).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + Section 8.6
// row 2 + Rev 1 audit MS3 close-out (raw new/delete in
// Create/Destroy routed through FMemory + FMemTag::Threading per
// Prime Directive / spec section 4.1: "every allocation carries a 2-byte
// tag; the allocator's per-block header carries the tag").
//
// Implements the FEvent wrapper declared in Public/HAL/FEvent.h.
// FEvent supports auto-reset and manual-reset modes; the factory
// methods CreateAutoReset / CreateManualReset construct the
// appropriate variant.
//
// Platform implementations:
//   * Win64:    CreateEventW + SetEvent + ResetEvent + WaitForSingleObject.
//   * POSIX:    pthread mutex + condvar + bool flag + reset mode.
//
// FEvent INSTANCE ALLOCATION (Rev 1 audit MS3 close-out):
//
// The factory methods (Create*) and Destroy route the FEvent
// instance allocation through FMemory::MallocOrAbort + placement-
// new + explicit destructor + FMemory::Free with the
// FMemTag::Threading attribution tag. The previous raw
// operator-new path bypassed FMallocBinnedX entirely, defeating
// the spec section 4.1 always-on attribution contract.
//
// =====================================================================

#include "HAL/FEvent.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"

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
// Win64 storage layout:
//   bytes [0..7]   HANDLE      -- CreateEventW return.
//   bytes [8]      bool        -- bManualReset (cached for diagnostics).
//   bytes [9..127] reserved.
//
// CreateEventW with bManualReset = TRUE produces a manual-reset event;
// FALSE produces an auto-reset event. The OS handles the reset
// semantics; we only need to remember bManualReset for diagnostics.
// ---------------------------------------------------------------------

namespace
{
    struct FWinEventStorage
    {
        HANDLE Handle;
        bool   bManualReset;
    };
    static_assert(sizeof(FWinEventStorage) <= 128,
                  "FWinEventStorage exceeds FEvent storage buffer");

    [[nodiscard]] FWinEventStorage* StorageOf(void* Storage) noexcept
    {
        return static_cast<FWinEventStorage*>(Storage);
    }
}

FEvent::FEvent() noexcept
{
    // Default-construct the storage; factory methods will overwrite.
    ::new (static_cast<void*>(m_storage)) FWinEventStorage{ nullptr, false };
}

FEvent::~FEvent() noexcept
{
    HANDLE H = StorageOf(m_storage)->Handle;
    if (H != nullptr)
    {
        ::CloseHandle(H);
    }
}

FEvent* FEvent::CreateAutoReset() noexcept
{
    // Rev 1 audit MS3 close-out: route through FMemory::MallocOrAbort
    // with FMemTag::Threading instead of raw operator new (spec section 4.1
    // always-on attribution; previous raw-new path bypassed
    // FMallocBinnedX and the per-tag accounting).
    void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
        sizeof(FEvent),
        alignof(FEvent),
        ::XCore::HAL::FMemTag::Threading);
    FEvent* E = ::new (Storage) FEvent;
    FWinEventStorage* S = StorageOf(E->m_storage);
    S->Handle       = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
    S->bManualReset = false;
    if (S->Handle == nullptr)
    {
        E->~FEvent();
        ::XCore::HAL::FMemory::Free(Storage);
        return nullptr;
    }
    return E;
}

FEvent* FEvent::CreateManualReset() noexcept
{
    // Rev 1 audit MS3 close-out: see CreateAutoReset.
    void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
        sizeof(FEvent),
        alignof(FEvent),
        ::XCore::HAL::FMemTag::Threading);
    FEvent* E = ::new (Storage) FEvent;
    FWinEventStorage* S = StorageOf(E->m_storage);
    S->Handle       = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    S->bManualReset = true;
    if (S->Handle == nullptr)
    {
        E->~FEvent();
        ::XCore::HAL::FMemory::Free(Storage);
        return nullptr;
    }
    return E;
}

void FEvent::Destroy(FEvent* Event) noexcept
{
    if (Event != nullptr)
    {
        // Rev 1 audit MS3 close-out: explicit destructor + FMemory::Free
        // matched to the placement-new + MallocOrAbort in Create*.
        Event->~FEvent();
        ::XCore::HAL::FMemory::Free(static_cast<void*>(Event));
    }
}

void FEvent::Trigger() noexcept
{
    ::SetEvent(StorageOf(m_storage)->Handle);
}

void FEvent::Reset() noexcept
{
    ::ResetEvent(StorageOf(m_storage)->Handle);
}

bool FEvent::Wait(float TimeoutSeconds) noexcept
{
    DWORD WaitMs;
    if (TimeoutSeconds < 0.0f)
    {
        WaitMs = INFINITE;
    }
    else
    {
        // Convert seconds to milliseconds; clamp at MAXDWORD - 1
        // because INFINITE is reserved.
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

    const DWORD Result = ::WaitForSingleObject(StorageOf(m_storage)->Handle, WaitMs);
    return Result == WAIT_OBJECT_0;
}

#else  // POSIX

// ---------------------------------------------------------------------
// POSIX storage layout: condvar-based event.
//
// We allocate a pthread_mutex_t + pthread_cond_t + bool flag +
// bool bManualReset in the opaque storage. Trigger sets the flag
// under the mutex and signals (auto-reset) or broadcasts (manual-
// reset). Wait blocks on the condvar until the flag is true; auto-
// reset clears the flag inside Wait after observing it.
//
// The POSIX condvar pattern requires explicit spurious-wakeup
// handling -- pthread_cond_wait may return without a signal. Our
// Wait loop re-checks the flag after every wake.
// ---------------------------------------------------------------------

namespace
{
    struct FPosixEventStorage
    {
        pthread_mutex_t Mutex;
        pthread_cond_t  Cond;
        bool            bFlag;
        bool            bManualReset;
    };
    static_assert(sizeof(FPosixEventStorage) <= 128,
                  "FPosixEventStorage exceeds FEvent storage buffer");

    [[nodiscard]] FPosixEventStorage* StorageOf(void* Storage) noexcept
    {
        return static_cast<FPosixEventStorage*>(Storage);
    }
}

FEvent::FEvent() noexcept
{
    FPosixEventStorage* S = ::new (static_cast<void*>(m_storage)) FPosixEventStorage{};
    pthread_mutex_init(&S->Mutex, nullptr);
    pthread_cond_init(&S->Cond, nullptr);
    S->bFlag        = false;
    S->bManualReset = false;
}

FEvent::~FEvent() noexcept
{
    FPosixEventStorage* S = StorageOf(m_storage);
    pthread_cond_destroy(&S->Cond);
    pthread_mutex_destroy(&S->Mutex);
}

FEvent* FEvent::CreateAutoReset() noexcept
{
    // Rev 1 audit MS3 close-out: route through FMemory::MallocOrAbort
    // with FMemTag::Threading instead of raw operator new (spec section 4.1
    // always-on attribution).
    void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
        sizeof(FEvent),
        alignof(FEvent),
        ::XCore::HAL::FMemTag::Threading);
    FEvent* E = ::new (Storage) FEvent;
    StorageOf(E->m_storage)->bManualReset = false;
    return E;
}

FEvent* FEvent::CreateManualReset() noexcept
{
    // Rev 1 audit MS3 close-out: see CreateAutoReset.
    void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
        sizeof(FEvent),
        alignof(FEvent),
        ::XCore::HAL::FMemTag::Threading);
    FEvent* E = ::new (Storage) FEvent;
    StorageOf(E->m_storage)->bManualReset = true;
    return E;
}

void FEvent::Destroy(FEvent* Event) noexcept
{
    if (Event != nullptr)
    {
        // Rev 1 audit MS3 close-out: explicit destructor + FMemory::Free.
        Event->~FEvent();
        ::XCore::HAL::FMemory::Free(static_cast<void*>(Event));
    }
}

void FEvent::Trigger() noexcept
{
    FPosixEventStorage* S = StorageOf(m_storage);
    pthread_mutex_lock(&S->Mutex);
    S->bFlag = true;
    if (S->bManualReset)
    {
        // Wake all waiters; flag stays true until Reset().
        pthread_cond_broadcast(&S->Cond);
    }
    else
    {
        // Auto-reset: wake one waiter. Wait() will clear the flag
        // after observing it (which preserves the auto-reset
        // semantics: the next Wait will block until the next
        // Trigger).
        pthread_cond_signal(&S->Cond);
    }
    pthread_mutex_unlock(&S->Mutex);
}

void FEvent::Reset() noexcept
{
    FPosixEventStorage* S = StorageOf(m_storage);
    pthread_mutex_lock(&S->Mutex);
    S->bFlag = false;
    pthread_mutex_unlock(&S->Mutex);
}

bool FEvent::Wait(float TimeoutSeconds) noexcept
{
    FPosixEventStorage* S = StorageOf(m_storage);
    pthread_mutex_lock(&S->Mutex);

    bool Result = false;

    if (TimeoutSeconds < 0.0f)
    {
        // Wait forever; loop to handle spurious wakeups.
        while (!S->bFlag)
        {
            pthread_cond_wait(&S->Cond, &S->Mutex);
        }
        Result = true;
        if (!S->bManualReset)
        {
            S->bFlag = false;  // auto-reset consumption
        }
    }
    else
    {
        // Compute absolute deadline = now + TimeoutSeconds. Use
        // CLOCK_REALTIME to match pthread_cond_timedwait's expected
        // clock domain (most POSIX systems default to CLOCK_REALTIME;
        // an alternative is to set pthread_condattr_setclock to
        // CLOCK_MONOTONIC at init time. CLOCK_REALTIME is simpler
        // and matches the platform default; not subject to wall-
        // clock adjustments on the millisecond scale typical for
        // our timeouts).
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

        // Wait until flag is set OR deadline expires.
        int Err = 0;
        while (!S->bFlag && Err != ETIMEDOUT)
        {
            Err = pthread_cond_timedwait(&S->Cond, &S->Mutex, &Deadline);
        }

        if (S->bFlag)
        {
            Result = true;
            if (!S->bManualReset)
            {
                S->bFlag = false;
            }
        }
    }

    pthread_mutex_unlock(&S->Mutex);
    return Result;
}

#endif  // XPACT_PLATFORM_WIN64

} // namespace XCore::HAL
