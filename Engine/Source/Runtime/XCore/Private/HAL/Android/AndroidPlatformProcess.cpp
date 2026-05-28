// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidPlatformProcess.cpp -- Android bodies for FPlatformProcess.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1.
//
// Bionic provides getpid, gettid (since Android API 16; XPact's
// minimum API target is API 29+ for Quest 3), nanosleep, sched_yield
// natively. /proc/self/exe symlink works on Android too.
//
// =====================================================================

#include "HAL/FPlatformProcess.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_ANDROID selector

#if XPACT_PLATFORM_ANDROID

#include <cerrno>
#include <cstdint>
#include <ctime>           // nanosleep, timespec
#include <sched.h>         // sched_yield
#include <sys/types.h>     // pid_t
#include <unistd.h>        // getpid, gettid, readlink

#include "Containers/FString.h"  // FString concrete definition (Phase 1g)

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetCurrentProcessId -- getpid().
// ---------------------------------------------------------------------
::std::uint32_t FPlatformProcess::GetCurrentProcessId() noexcept
{
    return static_cast<::std::uint32_t>(::getpid());
}

// ---------------------------------------------------------------------
// GetCurrentThreadId -- gettid() (Bionic native).
//
// Bionic ships gettid() as a libc function (no syscall fallback
// needed). Returns the kernel TID.
// ---------------------------------------------------------------------
::std::uint32_t FPlatformProcess::GetCurrentThreadId() noexcept
{
    return static_cast<::std::uint32_t>(::gettid());
}

// ---------------------------------------------------------------------
// Sleep -- nanosleep with EINTR retry.
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
        Req = Rem;
    }
}

// ---------------------------------------------------------------------
// YieldThread -- sched_yield().
// ---------------------------------------------------------------------
void FPlatformProcess::YieldThread() noexcept
{
    (void)::sched_yield();
}

// ---------------------------------------------------------------------
// GetExecutablePath -- readlink("/proc/self/exe").
//
// Android's Bionic supports /proc/self/exe; the returned path is the
// canonical APK-extracted native-library path or the dex-launched
// activity binary depending on the launch mode. The path is already
// UTF-8 (Bionic filesystem paths are byte strings). GetExecutablePath
// wraps the byte buffer in an FString. The same UTF-8 helper is
// reachable via the XPACT_TEST_GetExecutablePathUtf8 extern "C" shim
// for tests that want the raw bytes.
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

FString FPlatformProcess::GetExecutablePath()
{
    char Buf[4096];
    const ::std::size_t Bytes = WriteExecutablePathUtf8(Buf, sizeof(Buf));
    return FString(Buf, static_cast<::int32>(Bytes));
}

extern "C" ::std::size_t XPACT_TEST_GetExecutablePathUtf8(
    char* OutBuf, ::std::size_t OutBufBytes) noexcept
{
    return WriteExecutablePathUtf8(OutBuf, OutBufBytes);
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_ANDROID
