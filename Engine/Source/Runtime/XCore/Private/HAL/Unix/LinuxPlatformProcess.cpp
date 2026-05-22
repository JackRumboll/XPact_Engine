// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// LinuxPlatformProcess.cpp -- Linux bodies for FPlatformProcess.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL).
//
// Pattern references:
//   * getpid() / gettid(): POSIX / Linux APIs respectively. Per the
//     dispatch instruction we use gettid() directly (glibc 2.30+); on
//     older glibc we fall back to syscall(SYS_gettid). The conditional
//     compile below handles both.
//   * Sleep: nanosleep(2) with EINTR retry. POSIX-standard;
//     deterministic-as-best-effort sleep precision.
//   * YieldThread: sched_yield(2). POSIX-standard.
//   * GetExecutablePath: readlink("/proc/self/exe", ...). The kernel-
//     provided canonical executable path; bypasses argv[0] ambiguity.
//
// =====================================================================

#include "HAL/FPlatformProcess.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_LINUX selector

#if XPACT_PLATFORM_LINUX

#include <cerrno>
#include <cstdint>
#include <ctime>           // nanosleep, timespec
#include <sched.h>         // sched_yield
#include <sys/syscall.h>   // SYS_gettid fallback
#include <sys/types.h>     // pid_t
#include <unistd.h>        // getpid, readlink

// gettid() availability: glibc 2.30+ ships it as a libc function. Older
// glibc requires the syscall form. The conditional below handles both
// without forcing a libc version requirement.
#if defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 30))
    #define XPACT_HAS_GETTID_FN 1
#else
    #define XPACT_HAS_GETTID_FN 0
#endif

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetCurrentProcessId -- getpid(2).
//
// POSIX-standard pid_t -> uint32_t cast. The cast is value-preserving
// on every supported Linux platform (pid_t is 32-bit).
// ---------------------------------------------------------------------
::std::uint32_t FPlatformProcess::GetCurrentProcessId() noexcept
{
    return static_cast<::std::uint32_t>(::getpid());
}

// ---------------------------------------------------------------------
// GetCurrentThreadId -- gettid() / SYS_gettid syscall.
//
// Returns the kernel-level TID (a small integer suitable for cross-
// language identification). NOT pthread_self() which is opaque.
// ---------------------------------------------------------------------
::std::uint32_t FPlatformProcess::GetCurrentThreadId() noexcept
{
#if XPACT_HAS_GETTID_FN
    return static_cast<::std::uint32_t>(::gettid());
#else
    return static_cast<::std::uint32_t>(::syscall(SYS_gettid));
#endif
}

// ---------------------------------------------------------------------
// Sleep -- nanosleep with EINTR retry.
//
// Convert float-seconds to a timespec; nanosleep blocks until the
// duration elapses. EINTR returns are retried with the remaining
// duration (the second timespec arg captures the unused remainder).
//
// Negative or NaN inputs become a no-op (matches the documented
// behaviour of FPlatformProcess::Sleep across all three platforms).
// ---------------------------------------------------------------------
void FPlatformProcess::Sleep(float Seconds) noexcept
{
    if (!(Seconds > 0.0f))
    {
        return;
    }

    const double TotalSeconds = static_cast<double>(Seconds);
    timespec Req;
    Req.tv_sec  = static_cast<time_t>(TotalSeconds);
    Req.tv_nsec = static_cast<long>((TotalSeconds - static_cast<double>(Req.tv_sec)) * 1e9);

    timespec Rem;
    while (::nanosleep(&Req, &Rem) == -1 && errno == EINTR)
    {
        Req = Rem;  // continue with remaining duration
    }
}

// ---------------------------------------------------------------------
// YieldThread -- sched_yield(2).
//
// POSIX-standard scheduler-yield. The return value is discarded.
// ---------------------------------------------------------------------
void FPlatformProcess::YieldThread() noexcept
{
    (void)::sched_yield();
}

// ---------------------------------------------------------------------
// GetExecutablePath -- readlink("/proc/self/exe", ...).
//
// PHASE 1b GATING: see Win64 implementation. The platform path
// (readlink) is exposed via the test shim below.
// ---------------------------------------------------------------------
namespace
{
    ::std::size_t WriteExecutablePathUtf8(char* OutBuf, ::std::size_t OutBufBytes) noexcept
    {
        if (OutBuf == nullptr || OutBufBytes < 2)
        {
            return 0;
        }
        const ssize_t N = ::readlink("/proc/self/exe", OutBuf, OutBufBytes - 1);
        if (N <= 0)
        {
            return 0;
        }
        OutBuf[N] = '\0';
        return static_cast<::std::size_t>(N);
    }
}

#if defined(XPACT_PHASE_1G_FSTRING_AVAILABLE)
FString FPlatformProcess::GetExecutablePath()
{
    char Buf[4096];
    (void)WriteExecutablePathUtf8(Buf, sizeof(Buf));
    return FString{};
}
#endif // XPACT_PHASE_1G_FSTRING_AVAILABLE

extern "C" ::std::size_t XPACT_TEST_GetExecutablePathUtf8(
    char* OutBuf, ::std::size_t OutBufBytes) noexcept
{
    return WriteExecutablePathUtf8(OutBuf, OutBufBytes);
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_LINUX
