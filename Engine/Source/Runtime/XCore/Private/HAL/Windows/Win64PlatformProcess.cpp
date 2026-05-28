// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64PlatformProcess.cpp -- Win64 bodies for FPlatformProcess.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL).
//
// Pattern reference:
//   * GetCurrentProcessId -- the Win32 API of the same name.
//   * GetCurrentThreadId  -- the Win32 API of the same name.
//   * Sleep               -- SleepEx with bAlertable=FALSE; per UE Core
//                            WindowsPlatformProcess.cpp Sleep pattern.
//                            SleepEx vs raw Sleep: the alertable flag
//                            lets APCs interrupt; we pass FALSE so the
//                            wait is uninterruptible.
//   * YieldThread         -- SwitchToThread; documented "lets the
//                            scheduler pick another ready thread on
//                            the same processor". UE Core's
//                            WindowsPlatformProcess uses the same.
//   * GetExecutablePath   -- GetModuleFileNameW(NULL, ...) + WC2MB
//                            transcode to UTF-8.
//
// =====================================================================

#include "HAL/FPlatformProcess.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_WIN64 selector

#if XPACT_PLATFORM_WIN64

#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN 1
#endif
#ifndef NOMINMAX
    #define NOMINMAX 1
#endif

#include <Windows.h>

#include <cstdint>

#include "Containers/FString.h"  // FString concrete definition (Phase 1g)

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetCurrentProcessId -- Win32 API.
//
// Returns DWORD; widening to uint32_t is value-preserving (DWORD ==
// unsigned long == 32 bits on Win64).
// ---------------------------------------------------------------------
::std::uint32_t FPlatformProcess::GetCurrentProcessId() noexcept
{
    return static_cast<::std::uint32_t>(::GetCurrentProcessId());
}

// ---------------------------------------------------------------------
// GetCurrentThreadId -- Win32 API.
//
// Returns DWORD; same widening as the process ID. The Win32 DWORD is
// the kernel-level thread ID (a small integer), NOT the opaque
// pthread_t equivalent.
// ---------------------------------------------------------------------
::std::uint32_t FPlatformProcess::GetCurrentThreadId() noexcept
{
    return static_cast<::std::uint32_t>(::GetCurrentThreadId());
}

// ---------------------------------------------------------------------
// Sleep -- SleepEx(ms, FALSE).
//
// Convert the float-seconds argument to DWORD milliseconds with
// round-to-nearest. Negative or NaN inputs clamp to 0 (matching POSIX
// nanosleep's documented behaviour for negative timespecs).
//
// SleepEx with bAlertable=FALSE matches the documented "uninterruptible
// sleep" semantic UE Core uses. The 0-ms case is special: SleepEx(0,
// FALSE) yields the current quantum but does not block (effectively
// the same as SwitchToThread on most quanta).
//
// Maximum sleep: INFINITE (0xFFFFFFFF) is the documented "wait
// forever" sentinel; we clamp to INFINITE-1 if the input would
// translate to that bit pattern to avoid the unintentional-infinite-
// wait pitfall.
// ---------------------------------------------------------------------
void FPlatformProcess::Sleep(float Seconds) noexcept
{
    if (!(Seconds > 0.0f))
    {
        // Branch covers Seconds <= 0 and Seconds == NaN. The 0-ms
        // SleepEx is a quantum-yield equivalent.
        ::SleepEx(0, FALSE);
        return;
    }

    const double Milliseconds = static_cast<double>(Seconds) * 1000.0;
    DWORD MsClamped;
    if (Milliseconds >= 4294967294.0)
    {
        // Clamp below INFINITE (0xFFFFFFFF) to avoid the wait-forever
        // sentinel collision. 4,294,967,294 ms ~= 49.7 days; longer
        // sleeps are not realistic in engine code anyway.
        MsClamped = 0xFFFFFFFEu;
    }
    else
    {
        MsClamped = static_cast<DWORD>(Milliseconds + 0.5);  // round-to-nearest
    }

    ::SleepEx(MsClamped, FALSE);
}

// ---------------------------------------------------------------------
// YieldThread -- SwitchToThread.
//
// Documented to "lets the scheduler pick another ready thread on the
// same processor". The return value indicates whether the scheduler
// actually switched; we discard it (the spec defines this as a hint,
// not a guarantee).
// ---------------------------------------------------------------------
void FPlatformProcess::YieldThread() noexcept
{
    (void)::SwitchToThread();
}

// ---------------------------------------------------------------------
// GetExecutablePath -- GetModuleFileNameW(NULL, ...) -> UTF-16 -> UTF-8.
//
// Per Section 7.1 spec body: NULL passed to GetModuleFileNameW returns
// the executable file's path (NOT argv[0]; argv[0] is the command-line
// invocation string and can be arbitrary -- e.g., the user passed a
// relative path or symlink to the .exe). GetModuleFileNameW returns
// the canonical fully-resolved path with the .exe suffix.
//
// The wide path is transcoded to UTF-8 via WideCharToMultiByte(CP_UTF8)
// inside WriteExecutablePathUtf8 below; GetExecutablePath wraps the
// byte buffer in an FString. The same UTF-8 helper is also reachable
// via the XPACT_TEST_GetExecutablePathUtf8 extern "C" shim for
// platform-syscall tests that want to probe the raw bytes without
// constructing an FString.
// ---------------------------------------------------------------------

namespace
{
    // Internal: fill the caller's UTF-8 buffer with the executable
    // path; return bytes written (excluding nul). The buffer is
    // expected to be MAX_PATH-equivalent (4096 bytes); truncation
    // returns the truncated path.
    ::std::size_t WriteExecutablePathUtf8(char* OutBuf, ::std::size_t OutBufBytes) noexcept
    {
        if (OutBuf == nullptr || OutBufBytes == 0)
        {
            return 0;
        }
        wchar_t StackBuf[1024];
        const DWORD Written = ::GetModuleFileNameW(
            nullptr, StackBuf,
            static_cast<DWORD>(sizeof(StackBuf) / sizeof(wchar_t)));
        if (Written == 0)
        {
            return 0;
        }

        // Two-pass UTF-8 transcode.
        const int Utf8Bytes = ::WideCharToMultiByte(
            CP_UTF8, 0,
            StackBuf, static_cast<int>(Written),
            OutBuf, static_cast<int>(OutBufBytes - 1),
            nullptr, nullptr);
        if (Utf8Bytes <= 0)
        {
            return 0;
        }
        OutBuf[Utf8Bytes] = '\0';
        return static_cast<::std::size_t>(Utf8Bytes);
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

#endif // XPACT_PLATFORM_WIN64
