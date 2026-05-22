// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformProcess.h -- abstract platform-process surface.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL). Per-OS concrete
// implementations land in Phase 1b.
//
// Pattern reference: UE Core `GenericPlatformProcess.h` defines
// `FGenericPlatformProcess` with a much larger surface (CreateProc,
// WaitForProc, PipeReader/Writer, SetCurrentWorkingDir, etc.). XPact's
// XCore-4a surface is intentionally minimal: only the process
// identification + thread yield + sleep primitives. Process creation
// (FProcHandle, spawn-and-reap) belongs to a later module (XSystemProc;
// post-XCore-4a per Master Plan) and is NOT included here.
//
// Sim-path discipline (Section 8.3): the sim-path is single-threaded
// per master plan; all of FPlatformProcess EXCEPT the thread-identity
// pair (`GetCurrentProcessId`, `GetCurrentThreadId`) is sim-path-
// banned. Specifically:
//   - `Sleep`: not sim-path-safe (wall-clock sleep; non-deterministic
//     wake time). The sim-path overlay header (Phase 1e) decorates it
//     with [[deprecated("not sim-path-safe; ...")]].
//   - `YieldThread`: same -- yield is a scheduler hint that produces
//     non-deterministic ordering. Sim-path code does not yield.
//   - `GetExecutablePath`: sim-path-safe (purely path-string-reading;
//     deterministic given an identical install image).
//   - `GetCurrentProcessId` / `GetCurrentThreadId`: sim-path-safe
//     because the sim-path is single-threaded and runs in a single
//     process per session.
//
// Abstract surface only -- NO .cpp bodies in Phase 1a.
//
// =====================================================================

#include "Macros/XCoreFwd.h"  // FString forward declaration (XCore namespace)

#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// Alias FString from the XCore namespace into the XCore::HAL scope
// used by this header. Section 11 places FString in namespace XCore;
// XCoreFwd.h's forward declaration honours that placement.
// ---------------------------------------------------------------------

using ::XCore::FString;

// ---------------------------------------------------------------------
// FPlatformProcess -- abstract platform-process surface.
//
// Class shape mirrors FPlatformMisc: static-only, no virtuals, no
// instantiation. The per-OS .cpp bodies in Phase 1b provide bodies
// per platform.
// ---------------------------------------------------------------------

class FPlatformProcess
{
public:
    // -----------------------------------------------------------------
    // Process ID of the current process.
    //
    // Phase 1b implementation:
    //   - Win64: GetCurrentProcessId() (Win32 API; returns DWORD)
    //   - Linux: getpid() (POSIX; pid_t -> uint32_t cast)
    //   - Android: getpid() (Bionic libc; same as Linux)
    //
    // Returns uint32_t -- pid_t is up to 32 bits on every supported
    // platform; widening to uint32_t avoids signed-pid-comparison bugs.
    // -----------------------------------------------------------------
    static uint32_t GetCurrentProcessId() noexcept;

    // -----------------------------------------------------------------
    // Path to the currently-running executable (UTF-8 FString).
    //
    // Phase 1b implementation:
    //   - Win64: GetModuleFileNameW(NULL, ...) -> UTF-16 -> UTF-8 via
    //            FUTF16String::ToFString. Returns the absolute path
    //            including the .exe suffix.
    //   - Linux: readlink("/proc/self/exe", ...). Returns the absolute
    //            path of the executable (NOT argv[0] which can be
    //            arbitrary).
    //   - Android: same as Linux -- /proc/self/exe is implemented on
    //              Android Bionic too.
    //
    // Sim-path-safe: deterministic given an identical install image.
    // -----------------------------------------------------------------
    static FString GetExecutablePath();

    // -----------------------------------------------------------------
    // Block the calling thread for the given duration (seconds).
    //
    // Phase 1b implementation:
    //   - Win64: Sleep(static_cast<DWORD>(Seconds * 1000.0)) -- the
    //            Win32 Sleep API; subject to the system timer
    //            resolution (timeBeginPeriod can lower it).
    //   - Linux: nanosleep(...) with EINTR-retry loop; converts the
    //            float seconds to a timespec.
    //   - Android: same as Linux.
    //
    // Argument is `float` per Section 7.1 spec (matches UE's signature
    // and the common use-case of "sleep a few millis"); the float-to-
    // duration conversion happens in the per-OS body.
    //
    // SIM-PATH DEPRECATION (Phase 1e overlay): the sim-path overlay
    // header decorates this method with
    //     [[deprecated("not sim-path-safe; sim-path is
    //                   single-threaded -- no thread can block")]]
    // -----------------------------------------------------------------
    static void Sleep(float Seconds) noexcept;

    // -----------------------------------------------------------------
    // Thread ID of the calling thread.
    //
    // Phase 1b implementation:
    //   - Win64: GetCurrentThreadId() (Win32 API; returns DWORD)
    //   - Linux: gettid() (Linux 2.4.11+ syscall; returns pid_t for the
    //            kernel-level thread)
    //   - Android: gettid() (Bionic libc wrapper)
    //
    // NOT pthread_self() -- pthread_self returns a pthread_t opaque
    // handle that is not numerically meaningful and not safely
    // convertible across the C# interop. The kernel TID is the right
    // value for cross-language thread identification.
    // -----------------------------------------------------------------
    static uint32_t GetCurrentThreadId() noexcept;

    // -----------------------------------------------------------------
    // Scheduler-yield hint for the calling thread.
    //
    // Phase 1b implementation:
    //   - Win64: SwitchToThread() (Win32 API; lets the scheduler pick
    //            another ready thread on the same processor)
    //   - Linux: sched_yield() (POSIX; same semantics)
    //   - Android: same as Linux.
    //
    // SIM-PATH DEPRECATION (Phase 1e overlay): the sim-path overlay
    // header decorates this method with
    //     [[deprecated("not sim-path-safe; sim-path is
    //                   single-threaded -- no thread to yield to")]]
    //
    // Note: this is NOT a spin-wait primitive (no PAUSE / YIELD
    // instruction). For lock-free queue back-off, use the threading
    // primitives in Section 8 (Phase 1c).
    // -----------------------------------------------------------------
    static void YieldThread() noexcept;
};

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Implement FPlatformProcess::GetCurrentProcessId per OS
//     (GetCurrentProcessId / getpid).
//   - Implement FPlatformProcess::GetExecutablePath per OS
//     (GetModuleFileNameW / readlink /proc/self/exe).
//   - Implement FPlatformProcess::Sleep per OS (Win32 Sleep /
//     nanosleep with EINTR retry).
//   - Implement FPlatformProcess::GetCurrentThreadId per OS
//     (GetCurrentThreadId / gettid).
//   - Implement FPlatformProcess::YieldThread per OS (SwitchToThread /
//     sched_yield).
// =====================================================================
