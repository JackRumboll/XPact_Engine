// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidPlatformTime.cpp -- Android bodies for FPlatformTime.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 + Section 7.3.
//
// Bionic supports clock_gettime(CLOCK_MONOTONIC_RAW) natively (since
// Android API 21+; XPact's minimum API target is well above that for
// Quest 3). The implementation mirrors LinuxPlatformTime.cpp -- the
// kernel APIs are identical.
//
// =====================================================================

#include "HAL/FPlatformTime.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_ANDROID selector

#if XPACT_PLATFORM_ANDROID

#include <atomic>
#include <cstdint>
#include <ctime>     // clock_gettime, CLOCK_MONOTONIC_RAW

namespace XCore::HAL
{

namespace
{
    constinit ::std::int64_t          g_OriginNs            = 0;
    constinit ::std::atomic<bool>     g_TimingInitialised   { false };
    constinit double                  g_SecondsPerCycle     = 1e-9;
}

void FPlatformTime::__Init() noexcept
{
    timespec Ts;
    if (::clock_gettime(CLOCK_MONOTONIC_RAW, &Ts) != 0)
    {
        return;
    }
    g_OriginNs =
        static_cast<::std::int64_t>(Ts.tv_sec)  * 1'000'000'000LL +
        static_cast<::std::int64_t>(Ts.tv_nsec);
    g_TimingInitialised.store(true, ::std::memory_order_release);
}

::std::uint64_t FPlatformTime::Cycles64() noexcept
{
    [[maybe_unused]] const bool bReady =
        g_TimingInitialised.load(::std::memory_order_acquire);

    timespec Ts;
    if (::clock_gettime(CLOCK_MONOTONIC_RAW, &Ts) != 0)
    {
        return 0;
    }
    const ::std::int64_t NowNs =
        static_cast<::std::int64_t>(Ts.tv_sec)  * 1'000'000'000LL +
        static_cast<::std::int64_t>(Ts.tv_nsec);
    return static_cast<::std::uint64_t>(NowNs - g_OriginNs);
}

double FPlatformTime::ToSeconds(::std::uint64_t Cycles) noexcept
{
    return static_cast<double>(Cycles) * g_SecondsPerCycle;
}

double FPlatformTime::Seconds() noexcept
{
    return ToSeconds(Cycles64());
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_ANDROID
