// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FConditionVariable.h -- condvar paired with FMutex (non-recursive).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + fix M-5
// (Wait takes FMutex&, NOT FCriticalSection&).
//
// FConditionVariable is the OS-backed condition variable. It pairs
// with an FMutex (non-recursive) and supports the standard
// Wait/Notify protocol:
//
//   FMutex m;
//   FConditionVariable cv;
//   // producer:
//   {
//       FScopedMutexLock lock(m);
//       // set predicate
//   }
//   cv.NotifyOne();
//   // consumer:
//   {
//       FScopedMutexLock lock(m);
//       while (!predicate)
//       {
//           cv.Wait(m);
//       }
//   }
//
// FIX M-5 (PAIRING DISCIPLINE): Wait takes FMutex& (concept-
// constrained to XIsNonRecursiveMutex). Passing an FCriticalSection
// is a COMPILE ERROR via the concept. Rationale (Section 8.2 spec
// body): "pthread_cond_wait with a recursive mutex held at recursion
// depth > 1 is undefined behaviour: the condvar implementation
// internally unlocks the mutex once, but a recursive mutex held N
// times requires N unlocks to be released. POSIX documents the UB;
// XPact prevents the call at compile time."
//
// Backing primitive per platform:
//   * Win64:    Win32 CONDITION_VARIABLE + SleepConditionVariableSRW.
//   * Linux:    pthread_cond_t + pthread_cond_wait.
//   * Android:  same as Linux.
//
// Hot-reload (Section 8.5): NO virtual methods; POD-like wrapper.
//
// =====================================================================
//
// IMPLEMENTATION NOTES:
//
// Same opaque-storage pattern as the mutex types. Sizes:
//   * Win64 CONDITION_VARIABLE:        8 bytes / 8-byte alignment.
//   * Linux  pthread_cond_t (glibc):  48 bytes / 8-byte alignment.
//   * Android pthread_cond_t:          48 bytes / 4-byte alignment.
//
// CHOSEN BUFFER SIZE: 48 bytes / 8-byte alignment.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "HAL/FMutex.h"

#include <cstddef>

namespace XCore::HAL
{

class FConditionVariable
{
public:
    FConditionVariable() noexcept;
    ~FConditionVariable() noexcept;

    FConditionVariable(const FConditionVariable&)            = delete;
    FConditionVariable& operator=(const FConditionVariable&) = delete;
    FConditionVariable(FConditionVariable&&)                 = delete;
    FConditionVariable& operator=(FConditionVariable&&)      = delete;

    // -----------------------------------------------------------------
    // Wait(FMutex&) -- atomically release the mutex and block until
    // notified; re-acquire the mutex before returning.
    //
    // PRECONDITION: the calling thread MUST hold the mutex.
    //
    // Spurious wakeups: pthread_cond_wait may spuriously return
    // even without a notify; callers MUST re-check the predicate in
    // a while loop:
    //
    //   while (!predicate) cv.Wait(m);
    //
    // We do NOT wrap-around to a predicate-functor overload because
    // the spec body's discipline ("Section 8.1: pairs with FMutex
    // for MPSC drainage") expects the caller to manage the
    // predicate explicitly. A future revision may add a predicate-
    // taking overload as a convenience.
    //
    // Win64: SleepConditionVariableSRW with INFINITE timeout.
    // POSIX: pthread_cond_wait.
    // -----------------------------------------------------------------
    void Wait(FMutex& M) noexcept;

    // -----------------------------------------------------------------
    // WaitFor(FMutex&, timeout) -- timed wait.
    //
    // TimeoutSeconds < 0: equivalent to Wait (no timeout).
    // TimeoutSeconds = 0: poll (return immediately).
    // TimeoutSeconds > 0: wait up to N seconds.
    //
    // Returns true if notified before timeout; false if timed out.
    // Spurious wakeups also return true (the function's contract is
    // "true means I might-have-been notified"); callers re-check
    // the predicate.
    //
    // Win64: SleepConditionVariableSRW with computed timeout.
    // POSIX: pthread_cond_timedwait with absolute deadline.
    // -----------------------------------------------------------------
    [[nodiscard]] bool WaitFor(FMutex& M, float TimeoutSeconds) noexcept;

    // -----------------------------------------------------------------
    // Concept-constrained template overloads.
    //
    // These are the "templated Wait" the spec body (Section 8.1)
    // names. They forward to the FMutex& overloads but ENFORCE the
    // non-recursive-mutex constraint via the XIsNonRecursiveMutex
    // concept. Passing an FCriticalSection& produces a compile error
    // pointing at the unsatisfied concept.
    //
    // The concept is defined in FMutex.h; FMutex satisfies it,
    // FCriticalSection does not.
    // -----------------------------------------------------------------
    template<typename MutexT>
        requires XIsNonRecursiveMutex<MutexT>
    void Wait(MutexT& M) noexcept
    {
        // Currently only FMutex is admissable. If future revisions add
        // other non-recursive mutex types, this template will dispatch
        // to a per-type implementation. For now, the only T satisfying
        // the concept is FMutex, so we forward to the FMutex&
        // overload via static_cast.
        Wait(static_cast<FMutex&>(M));
    }

    template<typename MutexT>
        requires XIsNonRecursiveMutex<MutexT>
    [[nodiscard]] bool WaitFor(MutexT& M, float TimeoutSeconds) noexcept
    {
        return WaitFor(static_cast<FMutex&>(M), TimeoutSeconds);
    }

    // -----------------------------------------------------------------
    // NotifyOne -- wake exactly one waiter (if any).
    //
    // If no threads are waiting, the notification is "lost" (this
    // is the POSIX semantics; the condvar doesn't queue
    // notifications). The Mesa-style pattern requires the caller to
    // hold the mutex before NotifyOne to avoid the lost-wakeup
    // race; in practice the producer side already holds the mutex
    // (to set the predicate) so NotifyOne under the lock is the
    // natural pattern.
    //
    // Win64: WakeConditionVariable.
    // POSIX: pthread_cond_signal.
    // -----------------------------------------------------------------
    void NotifyOne() noexcept;

    // -----------------------------------------------------------------
    // NotifyAll -- wake all waiters.
    //
    // Use when more than one waiter might make progress. Subject to
    // the thundering-herd problem if too many threads wake at once;
    // prefer NotifyOne unless multiple-waiter wake is genuinely
    // needed.
    //
    // Win64: WakeAllConditionVariable.
    // POSIX: pthread_cond_broadcast.
    // -----------------------------------------------------------------
    void NotifyAll() noexcept;

private:
    static constexpr ::SIZE_T kStorageSize  = 48;
    static constexpr ::SIZE_T kStorageAlign = 8;

    alignas(kStorageAlign) ::std::byte m_storage[kStorageSize];
};

static_assert(sizeof(FConditionVariable)  == 48,
              "FConditionVariable ABI lock: 48 bytes");
static_assert(alignof(FConditionVariable) == 8,
              "FConditionVariable ABI lock: 8-byte alignment");

} // namespace XCore::HAL
