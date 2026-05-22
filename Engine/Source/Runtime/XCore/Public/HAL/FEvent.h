// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FEvent.h -- waitable event primitive (auto-reset or manual-reset).
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + Section 8.6
// row 2 (UE-divergence: FScopedEvent + ad-hoc helpers scattered ->
// XPact FEventPtr unique_ptr-based RAII).
//
// FEvent is a thread synchronization primitive. A thread may wait on
// the event (blocking until triggered) or trigger it (waking one or
// all waiters depending on reset mode). Two reset modes:
//
//   * AutoReset:   a Trigger wakes EXACTLY ONE waiter and immediately
//                  resets to untriggered. If multiple waiters are
//                  blocked, only one wakes; the others stay blocked
//                  until the next Trigger.
//   * ManualReset: a Trigger wakes ALL current waiters and leaves the
//                  event in the triggered state. Subsequent waits
//                  return immediately until Reset() is called.
//
// Backing primitive per platform:
//   * Win64:    Win32 CreateEventW + SetEvent + WaitForSingleObject.
//   * Linux:    pthread condvar + pthread mutex + bool flag.
//   * Android:  same as Linux.
//
// Pattern reference: UE Core Public/HAL/Event.h:21 FEvent is the
// abstract interface UE uses; per-platform .cpp delivers the body.
// XPact's API is similar in shape but the type is concrete (not
// virtual). The UE virtual interface exists because UE's allocator
// allocates platform-specific subclasses; XPact's concrete type
// embeds the platform handle directly in opaque storage, achieving
// the same flexibility without the virtual dispatch.
//
// UE-divergence (Section 8.6 row 2): UE has FScopedEvent in
// HAL/Event.h:136 (a stack-allocated event that auto-pools) plus
// FEventRef and FSharedEventRef. The pattern is fragmented across
// multiple types; XPact's single FEventPtr (unique_ptr-based RAII)
// covers every use case. The pool-vs-allocate choice is left to
// FEvent::CreateAutoReset / CreateManualReset, which may internally
// pool events (a Phase 1d optimisation; Phase 1c allocates fresh
// each time).
//
// Hot-reload (Section 8.5): NO virtual methods on the wrapper. The
// FEvent type itself has only static factory methods + non-virtual
// instance methods; the platform handle is opaque storage.
//
// =====================================================================
//
// CONSTRUCTION PATTERN:
//
// FEvent has NO public constructor. Use the static factory:
//   FEvent* E = FEvent::CreateAutoReset();   // or CreateManualReset
//   E->Trigger();
//   E->Wait();
//   FEvent::Destroy(E);
//
// Or use the RAII unique_ptr wrapper:
//   FEventPtr E(FEvent::CreateAutoReset());  // auto-destroyed
//   E->Trigger();
//   E->Wait();
//
// The factory-based approach lets the implementation hide its storage
// strategy: Phase 1c allocates via FMemory::MallocOrAbort; Phase 1d
// may switch to an event pool to avoid per-FEvent OS-handle creation
// cost.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <memory>

namespace XCore::HAL
{

class FEvent
{
public:
    // -----------------------------------------------------------------
    // CreateAutoReset / CreateManualReset -- factory methods.
    //
    // Returns a heap-allocated FEvent. Caller owns the lifetime; use
    // Destroy() or FEventPtr to release.
    //
    // AutoReset: Trigger wakes exactly one waiter and resets.
    // ManualReset: Trigger wakes all waiters; Reset() to clear.
    //
    // Returns nullptr only if the OS event handle creation fails
    // catastrophically (extremely rare; under typical OOM the
    // allocator's MallocOrAbort would have aborted first).
    // -----------------------------------------------------------------
    [[nodiscard]] static FEvent* CreateAutoReset()   noexcept;
    [[nodiscard]] static FEvent* CreateManualReset() noexcept;

    // -----------------------------------------------------------------
    // Destroy -- release a previously-created FEvent.
    //
    // Safe to call with a null pointer (no-op). Must NOT be called
    // while threads are waiting on the event (UB; the OS handle is
    // closed under their feet).
    // -----------------------------------------------------------------
    static void Destroy(FEvent* Event) noexcept;

    // Non-copyable / non-movable. The OS handle is non-copyable; the
    // factory hands out heap pointers exclusively.
    FEvent(const FEvent&)            = delete;
    FEvent& operator=(const FEvent&) = delete;
    FEvent(FEvent&&)                 = delete;
    FEvent& operator=(FEvent&&)      = delete;

    // -----------------------------------------------------------------
    // Trigger -- signal the event.
    //
    // AutoReset: wakes one waiter (the OS chooses; typically FIFO
    // but not guaranteed) and resets to untriggered.
    // ManualReset: wakes all current waiters; subsequent Wait()s
    // return immediately until Reset().
    //
    // Win64: SetEvent.
    // POSIX: pthread_mutex_lock + flag = true + pthread_cond_signal
    //        (AutoReset) or pthread_cond_broadcast (ManualReset) +
    //        pthread_mutex_unlock.
    // -----------------------------------------------------------------
    void Trigger() noexcept;

    // -----------------------------------------------------------------
    // Reset -- clear the triggered state (ManualReset only).
    //
    // On an AutoReset event, calling Reset() is a no-op (the event
    // auto-resets on every Trigger anyway). The method exists on
    // both for API uniformity.
    //
    // Win64: ResetEvent.
    // POSIX: pthread_mutex_lock + flag = false + pthread_mutex_unlock.
    // -----------------------------------------------------------------
    void Reset() noexcept;

    // -----------------------------------------------------------------
    // Wait -- block until triggered (or timeout).
    //
    // TimeoutSeconds < 0: wait forever.
    // TimeoutSeconds = 0: poll (return immediately with true if
    //                     triggered, false otherwise).
    // TimeoutSeconds > 0: wait up to N seconds.
    //
    // Returns true if the event was triggered before the timeout,
    // false if the timeout expired.
    //
    // Spurious wakeups: implementations MUST loop internally so the
    // caller receives true only when the event was genuinely
    // triggered. POSIX pthread_cond_wait spuriously wakes; the
    // implementation must verify the flag and re-wait.
    //
    // Win64: WaitForSingleObject with computed timeout.
    // POSIX: pthread_cond_timedwait with computed absolute deadline.
    // -----------------------------------------------------------------
    [[nodiscard]] bool Wait(float TimeoutSeconds = -1.0f) noexcept;

protected:
    // The constructor is protected so only the factory methods can
    // create instances. The destructor is also protected; Destroy()
    // is the public release path.
    FEvent() noexcept;
    ~FEvent() noexcept;

private:
    // Opaque storage for platform handle + flag state.
    //
    // Layout per platform:
    //   * Win64:    HANDLE (8 bytes) + bManualReset (1 byte) + padding.
    //   * POSIX:    pthread_mutex_t + pthread_cond_t + flag + reset-mode.
    //               Sizes vary: glibc pthread_mutex_t = 40 bytes;
    //               glibc pthread_cond_t = 48 bytes; total ~96 bytes
    //               with flag + mode.
    //
    // We size for the POSIX worst case + headroom: 128 bytes / 16-byte
    // alignment. The static_assert in the .cpp verifies per-platform.
    static constexpr ::SIZE_T kStorageSize  = 128;
    static constexpr ::SIZE_T kStorageAlign = 16;

    alignas(kStorageAlign) ::std::byte m_storage[kStorageSize];
};

static_assert(sizeof(FEvent)  == 128, "FEvent ABI lock: 128 bytes");
static_assert(alignof(FEvent) == 16,  "FEvent ABI lock: 16-byte alignment");

// ---------------------------------------------------------------------
// FEventDeleter -- custom deleter for FEventPtr.
//
// std::unique_ptr's default deleter calls operator delete; FEvent's
// destruction requires Destroy() (which both releases the OS handle
// and frees the heap memory via FMemory::Free). The deleter routes
// to Destroy().
// ---------------------------------------------------------------------

struct FEventDeleter
{
    void operator()(FEvent* E) const noexcept
    {
        FEvent::Destroy(E);
    }
};

// ---------------------------------------------------------------------
// FEventPtr -- RAII unique_ptr wrapper.
//
// Section 8.6 row 2 divergence: UE has FEventRef, FSharedEventRef,
// FScopedEvent scattered; XPact's single FEventPtr handles every
// use case via the standard unique_ptr surface.
//
//   FEventPtr E(FEvent::CreateAutoReset());
//   E->Trigger();
//   if (!E->Wait(5.0f)) { /* timeout */ }
//   // automatic destruction on scope exit
// ---------------------------------------------------------------------

using FEventPtr = ::std::unique_ptr<FEvent, FEventDeleter>;

} // namespace XCore::HAL
