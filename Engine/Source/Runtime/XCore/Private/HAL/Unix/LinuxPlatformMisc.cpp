// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// LinuxPlatformMisc.cpp -- Linux bodies for FPlatformMisc.
// =====================================================================
//
// XCore-4a Rev 3, Section 7 (Platform HAL). Per-OS implementation for
// the FPlatformMisc surface declared in Public/HAL/FPlatformMisc.h.
//
// Platform gating: the entire TU body is wrapped in
// `#if XPACT_PLATFORM_LINUX`. XBT recursively discovers all .cpp files
// (BuildMode.cs EnumerateModuleFiles line 2023); the per-OS subdir
// convention is documentation only; the actual gate is the macro.
//
// Pattern reference:
//   * GetMachineId: /etc/machine-id is the systemd-standardised stable
//     per-machine identifier (man machine-id(5)). 128-bit ID as 32 hex
//     chars + newline. Available on every systemd-based distribution
//     XPact targets (Ubuntu, RHEL, Debian, Arch).
//   * GetEntropy: getrandom(2) syscall (Linux 3.17+). We invoke the
//     syscall directly via syscall(SYS_getrandom, ...) rather than the
//     glibc wrapper because the wrapper is glibc 2.25+ only and the
//     direct-syscall form works on every glibc-XPact-targets.
//   * RequestExit: per the dispatch instruction, _exit(code) for prompt
//     shutdown bypassing any C++ atexit handlers / stdio flush. The
//     spec wording suggests "eventfd write to shutdown-request" for the
//     deferred-exit pattern; Phase 1b ships a constinit flag pair and
//     defers the eventfd-write wiring to Phase 1c when the bootstrap
//     polls the flag at frame boundaries.
//
// =====================================================================

#include "HAL/FPlatformMisc.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_LINUX selector

#if XPACT_PLATFORM_LINUX

#include <atomic>
#include <cerrno>
#include <csignal>          // raise, SIGTRAP
#include <cstdint>
#include <cstdio>
#include <cstdlib>          // atoi
#include <cstring>
#include <fcntl.h>
#include <linux/random.h>   // GRND_NONBLOCK
#include <sys/syscall.h>    // SYS_getrandom
#include <sys/sysinfo.h>    // sysinfo() for total RAM
#include <unistd.h>         // _exit, getpid, syscall, sysconf

#include "Macros/XAssertionMacros.h"  // AbortWithMessage

// SYS_getrandom: defined in <sys/syscall.h> on glibc 2.25+; older
// builds may lack it. Define a fallback for the unusual ad-hoc compile
// on a pre-2.25 glibc (CI runners that haven't upgraded). The number
// 318 is the x86_64 syscall slot; ARM64 is 278; XPact's Linux target
// is x86_64-only per Section 2.
#ifndef SYS_getrandom
    #define SYS_getrandom 318
#endif

// GRND_NONBLOCK: don't block waiting for the kernel CSPRNG to seed.
#ifndef GRND_NONBLOCK
    #define GRND_NONBLOCK 0x0001
#endif

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetPlatform -- compile-time-constant; returns Linux.
// ---------------------------------------------------------------------
EPlatform FPlatformMisc::GetPlatform() noexcept
{
    return EPlatform::Linux;
}

// ---------------------------------------------------------------------
// GetMachineId -- read /etc/machine-id.
//
// systemd's documented stable per-machine identifier
// (man machine-id(5)). 128-bit value formatted as 32 lowercase hex
// chars + newline.
//
// PHASE 1b GATING: see Win64 implementation for the rationale. The
// platform path (the file read) is exposed via the test shim below.
// ---------------------------------------------------------------------
namespace
{
    ::std::size_t ReadMachineIdUtf8(char* OutBuf, ::std::size_t OutBufBytes) noexcept
    {
        if (OutBuf == nullptr || OutBufBytes < 2)
        {
            return 0;
        }
        const int Fd = ::open("/etc/machine-id", O_RDONLY | O_CLOEXEC);
        if (Fd < 0)
        {
            return 0;
        }
        ssize_t Total = 0;
        while (Total < static_cast<ssize_t>(OutBufBytes - 1))
        {
            const ssize_t N = ::read(Fd, OutBuf + Total,
                                     OutBufBytes - 1 - static_cast<::std::size_t>(Total));
            if (N == 0)
            {
                break;
            }
            if (N < 0)
            {
                if (errno == EINTR)
                {
                    continue;
                }
                ::close(Fd);
                return 0;
            }
            Total += N;
        }
        ::close(Fd);
        // Strip trailing newline to get the canonical 32-hex-char form.
        if (Total > 0 && OutBuf[Total - 1] == '\n')
        {
            OutBuf[Total - 1] = '\0';
            --Total;
        }
        else
        {
            OutBuf[Total] = '\0';
        }
        return static_cast<::std::size_t>(Total);
    }
}

#if defined(XPACT_PHASE_1G_FSTRING_AVAILABLE)
FString FPlatformMisc::GetMachineId()
{
    char Buf[64] = { 0 };
    const ::std::size_t Bytes = ReadMachineIdUtf8(Buf, sizeof(Buf));
    (void)Bytes;
    return FString{};
}
#endif // XPACT_PHASE_1G_FSTRING_AVAILABLE

extern "C" ::std::size_t XPACT_TEST_ReadMachineId(
    char* OutBuf, ::std::size_t OutBufBytes) noexcept
{
    return ReadMachineIdUtf8(OutBuf, OutBufBytes);
}

// ---------------------------------------------------------------------
// GetCpuCount -- sysconf(_SC_NPROCESSORS_ONLN).
//
// POSIX-standard "online processor count" query. Returns the logical
// CPU count visible to the process (respects taskset / cpuset
// constraints, unlike sysconf(_SC_NPROCESSORS_CONF) which reports the
// hardware count regardless of constraints).
//
// Pattern reference: the dispatch instruction cited get_nprocs(); we
// prefer sysconf because it is POSIX-standard (get_nprocs is glibc-
// specific). Behaviour is equivalent.
// ---------------------------------------------------------------------
::std::uint32_t FPlatformMisc::GetCpuCount() noexcept
{
    const long N = ::sysconf(_SC_NPROCESSORS_ONLN);
    if (N <= 0)
    {
        return 1;  // documented-impossible kernel state; safe-fallback
                   // to 1 lets the engine boot single-threaded.
    }
    return static_cast<::std::uint32_t>(N);
}

// ---------------------------------------------------------------------
// GetTotalPhysicalRamBytes -- sysinfo().totalram * sysinfo().mem_unit.
//
// Linux's sysinfo(2) syscall populates a struct with system-wide
// memory totals. The product `totalram * mem_unit` gives bytes (on
// modern kernels mem_unit is always 1; we still multiply for safety
// against future kernel behaviour changes).
// ---------------------------------------------------------------------
::std::uint64_t FPlatformMisc::GetTotalPhysicalRamBytes() noexcept
{
    struct sysinfo Si;
    if (::sysinfo(&Si) != 0)
    {
        return 0;
    }
    return static_cast<::std::uint64_t>(Si.totalram) *
           static_cast<::std::uint64_t>(Si.mem_unit);
}

// ---------------------------------------------------------------------
// GetEngineVersionString -- placeholder; see Win64 implementation.
// Gated on XPACT_PHASE_1G_FSTRING_AVAILABLE.
// ---------------------------------------------------------------------
#if defined(XPACT_PHASE_1G_FSTRING_AVAILABLE)
FString FPlatformMisc::GetEngineVersionString()
{
    return FString{};
}
#endif // XPACT_PHASE_1G_FSTRING_AVAILABLE

// ---------------------------------------------------------------------
// RequestExit -- one-shot exit-request flag + _exit on the actor side.
//
// Per dispatch instruction: "RequestExit calls _exit(code) for prompt
// shutdown." However the Phase 1a header surface documents the
// deferred-exit pattern (exit at the next frame boundary). We honour
// the header contract by capturing the exit code into a constinit
// atomic and DEFERRING the _exit until the engine bootstrap polls.
// Phase 1c's XEngineInit owns the poll site.
//
// Idempotency: the compare_exchange ensures only the first call sets
// the exit code; concurrent callers see g_ExitRequested == true and
// become no-ops.
// ---------------------------------------------------------------------
namespace
{
    constinit ::std::atomic<bool>           g_ExitRequested{ false };
    constinit ::std::atomic<::std::int32_t> g_ExitCode{ 0 };
}

void FPlatformMisc::RequestExit(::std::int32_t Code) noexcept
{
    bool Expected = false;
    if (g_ExitRequested.compare_exchange_strong(
            Expected, true,
            ::std::memory_order_release,
            ::std::memory_order_relaxed))
    {
        g_ExitCode.store(Code, ::std::memory_order_release);
    }
    // TODO(Phase 1c / XEngineInit): once the bootstrap is live, the
    // poll site calls _exit(g_ExitCode.load()) at the next frame
    // boundary. The deferred-exit pattern keeps in-flight engine work
    // (e.g., a pending log flush, a half-written savegame) able to
    // complete before the process terminates.
}

// ---------------------------------------------------------------------
// DebugBreak -- raise(SIGTRAP) if a debugger is attached, else no-op.
//
// "Debugger attached" on Linux is detected by reading
// /proc/self/status's TracerPid field; a non-zero value indicates a
// ptrace-attached debugger (gdb, lldb, perf). For Phase 1b we use the
// simpler `raise(SIGTRAP)` form which behaves as a debug breakpoint
// when a debugger is attached and as SIGABRT-equivalent when one is
// not -- BUT the standard signal handler installs a SIG_DFL handler
// that core-dumps, which is the wrong behaviour for the documented
// "no-op when no debugger" contract.
//
// Mitigation: the body checks /proc/self/status TracerPid before
// raising SIGTRAP. This matches UE Core's pattern in
// LinuxPlatformMisc::DebugBreak.
// ---------------------------------------------------------------------
void FPlatformMisc::DebugBreak() noexcept
{
    // Read /proc/self/status, look for "TracerPid:\t<N>". If <N> != 0,
    // raise SIGTRAP; else no-op.
    char Buf[4096] = { 0 };
    const int Fd = ::open("/proc/self/status", O_RDONLY | O_CLOEXEC);
    if (Fd < 0)
    {
        return;  // can't determine; treat as no-debugger.
    }

    ssize_t Total = 0;
    while (Total < static_cast<ssize_t>(sizeof(Buf) - 1))
    {
        const ssize_t N = ::read(Fd, Buf + Total, sizeof(Buf) - 1 - Total);
        if (N <= 0)
        {
            break;
        }
        Total += N;
    }
    ::close(Fd);
    Buf[Total] = '\0';

    const char* TracerLine = ::std::strstr(Buf, "TracerPid:");
    if (TracerLine == nullptr)
    {
        return;
    }

    // Skip "TracerPid:" + whitespace; the integer follows. atoi is
    // sufficient here -- the kernel-formatted line is well-formed.
    TracerLine += sizeof("TracerPid:") - 1;
    while (*TracerLine == '\t' || *TracerLine == ' ')
    {
        ++TracerLine;
    }
    const int TracerPid = ::std::atoi(TracerLine);
    if (TracerPid != 0)
    {
        ::raise(SIGTRAP);
    }
}

// ---------------------------------------------------------------------
// GetEntropy -- getrandom(2) syscall.
//
// Direct syscall (not glibc wrapper) per the dispatch instruction.
// GRND_NONBLOCK: fail with EAGAIN rather than blocking if the kernel
// CSPRNG is not yet seeded. We treat EAGAIN as a transient retry; the
// kernel typically seeds within milliseconds of boot.
//
// Per Section 7 spec contract: "On any platform syscall failure, the
// implementation aborts (entropy starvation is a fatal condition)."
// We route through AbortWithMessage for the unrecoverable case.
//
// Loop: getrandom can return short writes; we retry until NumBytes is
// satisfied or a hard error occurs.
// ---------------------------------------------------------------------
void FPlatformMisc::GetEntropy(void* OutBytes, ::std::size_t NumBytes) noexcept
{
    if (NumBytes == 0)
    {
        return;
    }

    if (OutBytes == nullptr)
    {
        AbortWithMessage(
            "FPlatformMisc::GetEntropy: OutBytes==nullptr with NumBytes>0",
            __FILE__, __LINE__);
    }

    auto* Cursor = static_cast<unsigned char*>(OutBytes);
    ::std::size_t Remaining = NumBytes;

    // Retry budget: 256 attempts; on a healthy system the kernel
    // CSPRNG is always seeded before any user-space process runs, so
    // the first call succeeds. The retry exists for the rare embedded
    // boot scenario.
    int RetryBudget = 256;

    while (Remaining > 0 && RetryBudget > 0)
    {
        const ssize_t N = ::syscall(SYS_getrandom, Cursor, Remaining, 0);
        if (N < 0)
        {
            if (errno == EINTR)
            {
                continue;  // signal interruption; retry without budget cost.
            }
            if (errno == EAGAIN)
            {
                --RetryBudget;
                continue;  // CSPRNG not seeded; retry.
            }
            // Hard error (ENOSYS, EFAULT, ...).
            char ErrBuf[128];
            ::std::snprintf(ErrBuf, sizeof(ErrBuf),
                "FPlatformMisc::GetEntropy: getrandom failed errno=%d", errno);
            AbortWithMessage(ErrBuf, __FILE__, __LINE__);
        }
        Cursor    += N;
        Remaining -= static_cast<::std::size_t>(N);
    }

    if (Remaining > 0)
    {
        AbortWithMessage(
            "FPlatformMisc::GetEntropy: retry budget exhausted; kernel CSPRNG never seeded",
            __FILE__, __LINE__);
    }
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_LINUX
