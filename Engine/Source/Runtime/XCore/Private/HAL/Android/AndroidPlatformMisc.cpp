// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidPlatformMisc.cpp -- Android bodies for FPlatformMisc.
// =====================================================================
//
// XCore-4a Rev 3, Section 7. Per-OS implementation for the
// FPlatformMisc surface; the Android target is Quest 3 (Snapdragon
// XR2 Gen 2, ARM64, Android-based Horizon OS).
//
// Platform gating: `#if XPACT_PLATFORM_ANDROID`.
//
// Bionic-vs-glibc notes:
//   * Bionic provides clock_gettime, getrandom, getpid, gettid, mmap,
//     sysconf, sysinfo natively. Many functions share Linux syscall
//     numbers.
//   * NDK 28+ ships getrandom(3) in <sys/random.h>; older NDKs require
//     the syscall form. We check __ANDROID_API__ and fall back to
//     /dev/urandom for the older case (per dispatch instruction).
//   * Android's stable per-machine identifier is Settings.Secure.
//     ANDROID_ID exposed via the Java framework -- not accessible from
//     pure NDK code. For Phase 1b we ship a process-lifetime random
//     UUID generated at first call and cached; the JNI-bridge
//     integration is a Phase 2 follow-up.
//
// =====================================================================

#include "HAL/FPlatformMisc.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_ANDROID selector

#if XPACT_PLATFORM_ANDROID

#include <android/log.h>     // __android_log_print
#include <atomic>
#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <csignal>           // raise, SIGTRAP
#include <cstdlib>           // _Exit, atoi
#include <cstring>
#include <fcntl.h>
#include <sys/syscall.h>     // SYS_getrandom
#include <sys/sysinfo.h>     // sysinfo
#include <unistd.h>

#include "Macros/XAssertionMacros.h"  // AbortWithMessage

#if __ANDROID_API__ >= 28
    #include <sys/random.h>   // getrandom in NDK 28+
    #define XPACT_HAS_GETRANDOM_FN 1
#else
    #define XPACT_HAS_GETRANDOM_FN 0
#endif

// SYS_getrandom: ARM64 syscall slot is 278. The fallback definition
// covers the rare ad-hoc compile on a pre-NDK-28 toolchain that doesn't
// define the syscall constant.
#ifndef SYS_getrandom
    #define SYS_getrandom 278
#endif

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetPlatform -- compile-time-constant; returns Android.
// ---------------------------------------------------------------------
EPlatform FPlatformMisc::GetPlatform() noexcept
{
    return EPlatform::Android;
}

// ---------------------------------------------------------------------
// GetMachineId -- process-lifetime random UUID (Phase 1b placeholder).
//
// Quest 3's stable ANDROID_ID is in Settings.Secure (Java framework);
// reaching it from NDK requires a JNI call through the
// MainActivity::getContentResolver() path. That integration is a
// Phase 2 follow-up (XAndroidJNI module).
//
// For Phase 1b we generate a process-lifetime random UUID at first call
// and cache it. PHASE 1b GATING: the FString surface is deferred to
// Phase 1g; the UUID bytes are exposed via the test shim below.
// ---------------------------------------------------------------------

namespace
{
    constinit ::std::atomic<bool>           g_ProcessUuidReady{ false };
    constinit ::std::atomic<bool>           g_ProcessUuidInFlight{ false };
    unsigned char                           g_ProcessUuid[16] = { 0 };

    void EnsureProcessUuidReady() noexcept
    {
        if (g_ProcessUuidReady.load(::std::memory_order_acquire))
        {
            return;
        }
        bool Expected = false;
        if (g_ProcessUuidInFlight.compare_exchange_strong(
                Expected, true,
                ::std::memory_order_acq_rel,
                ::std::memory_order_acquire))
        {
            FPlatformMisc::GetEntropy(g_ProcessUuid, sizeof(g_ProcessUuid));
            g_ProcessUuidReady.store(true, ::std::memory_order_release);
        }
        else
        {
            while (!g_ProcessUuidReady.load(::std::memory_order_acquire))
            {
                // Bounded spin.
            }
        }
    }
}

#if defined(XPACT_PHASE_1G_FSTRING_AVAILABLE)
FString FPlatformMisc::GetMachineId()
{
    EnsureProcessUuidReady();
    // TODO(Phase 1g + Phase 2 JNI): format g_ProcessUuid as a UTF-8
    // hex string and construct FString from the bytes.
    return FString{};
}
#endif // XPACT_PHASE_1G_FSTRING_AVAILABLE

extern "C" int XPACT_TEST_GetProcessUuid(unsigned char* OutBuf, ::std::size_t OutBufBytes) noexcept
{
    if (OutBuf == nullptr || OutBufBytes < sizeof(g_ProcessUuid))
    {
        return 0;
    }
    EnsureProcessUuidReady();
    for (::std::size_t i = 0; i < sizeof(g_ProcessUuid); ++i)
    {
        OutBuf[i] = g_ProcessUuid[i];
    }
    return 1;
}

// ---------------------------------------------------------------------
// GetCpuCount -- sysconf(_SC_NPROCESSORS_ONLN).
//
// Bionic provides sysconf; same semantics as Linux.
// Quest 3's Snapdragon XR2 Gen 2 reports 8 cores. The result respects
// cgroup constraints (taskset-equivalent).
// ---------------------------------------------------------------------
::std::uint32_t FPlatformMisc::GetCpuCount() noexcept
{
    const long N = ::sysconf(_SC_NPROCESSORS_ONLN);
    if (N <= 0)
    {
        return 1;
    }
    return static_cast<::std::uint32_t>(N);
}

// ---------------------------------------------------------------------
// GetTotalPhysicalRamBytes -- sysinfo().totalram * sysinfo().mem_unit.
//
// Bionic provides sysinfo(2); Quest 3 reports ~8 GiB.
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
// GetEngineVersionString -- placeholder; gated on
// XPACT_PHASE_1G_FSTRING_AVAILABLE.
// ---------------------------------------------------------------------
#if defined(XPACT_PHASE_1G_FSTRING_AVAILABLE)
FString FPlatformMisc::GetEngineVersionString()
{
    return FString{};
}
#endif // XPACT_PHASE_1G_FSTRING_AVAILABLE

// ---------------------------------------------------------------------
// RequestExit -- one-shot flag (same pattern as Linux/Win64).
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
        // Emit a fatal-log line so logcat captures the exit request.
        __android_log_print(ANDROID_LOG_INFO, "XPACT",
            "RequestExit code=%d", static_cast<int>(Code));
    }
    // TODO(Phase 1c / XEngineInit): the bootstrap polls the flag at
    // frame boundaries; on Android the exit goes through ANativeActivity_
    // finish so the Android lifecycle observes the orderly shutdown.
}

// ---------------------------------------------------------------------
// DebugBreak -- TracerPid check + raise(SIGTRAP).
//
// Same /proc/self/status pattern as Linux. Bionic supports /proc.
// ---------------------------------------------------------------------
void FPlatformMisc::DebugBreak() noexcept
{
    char Buf[4096] = { 0 };
    const int Fd = ::open("/proc/self/status", O_RDONLY | O_CLOEXEC);
    if (Fd < 0)
    {
        return;
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
    TracerLine += sizeof("TracerPid:") - 1;
    while (*TracerLine == '\t' || *TracerLine == ' ')
    {
        ++TracerLine;
    }
    if (::std::atoi(TracerLine) != 0)
    {
        ::raise(SIGTRAP);
    }
}

// ---------------------------------------------------------------------
// GetEntropy -- getrandom syscall, fallback to /dev/urandom on old NDKs.
//
// The dispatch instruction specifies:
//   "uses getrandom(2) syscall (NDK 28+) with fallback to /dev/urandom
//    for older NDKs (check __ANDROID_API__)."
//
// We implement both paths. NDK 28+: getrandom direct call (or syscall).
// NDK <28: read from /dev/urandom which is also a kernel CSPRNG source.
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

#if XPACT_HAS_GETRANDOM_FN
    // NDK 28+ path: use getrandom() from <sys/random.h>.
    int RetryBudget = 256;
    while (Remaining > 0 && RetryBudget > 0)
    {
        const ssize_t N = ::getrandom(Cursor, Remaining, 0);
        if (N < 0)
        {
            if (errno == EINTR)
            {
                continue;
            }
            if (errno == EAGAIN)
            {
                --RetryBudget;
                continue;
            }
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
            "FPlatformMisc::GetEntropy: retry budget exhausted",
            __FILE__, __LINE__);
    }
#else
    // Pre-NDK-28 fallback: read /dev/urandom.
    const int Fd = ::open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (Fd < 0)
    {
        AbortWithMessage(
            "FPlatformMisc::GetEntropy: /dev/urandom open failed",
            __FILE__, __LINE__);
    }
    while (Remaining > 0)
    {
        const ssize_t N = ::read(Fd, Cursor, Remaining);
        if (N < 0)
        {
            if (errno == EINTR)
            {
                continue;
            }
            ::close(Fd);
            char ErrBuf[128];
            ::std::snprintf(ErrBuf, sizeof(ErrBuf),
                "FPlatformMisc::GetEntropy: /dev/urandom read failed errno=%d", errno);
            AbortWithMessage(ErrBuf, __FILE__, __LINE__);
        }
        Cursor    += N;
        Remaining -= static_cast<::std::size_t>(N);
    }
    ::close(Fd);
#endif
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_ANDROID
