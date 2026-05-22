// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// LinuxPlatformTime.cpp -- Linux bodies for FPlatformTime.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 + Section 7.3.
// clock_gettime(CLOCK_MONOTONIC_RAW) for the monotonic clock.
//
// Pattern reference:
//   * CLOCK_MONOTONIC_RAW vs CLOCK_MONOTONIC: the _RAW variant is NOT
//     subject to NTP-skew adjustments. Section 7 spec mandates _RAW
//     specifically so the cross-platform-bit-exact-tick contract is
//     preserved (an NTP slewed CLOCK_MONOTONIC would silently change
//     pace under wall-clock corrections).
//   * The clock is documented thread-safe (man clock_gettime(2)).
//   * No calibration loop needed -- clock_gettime returns nanoseconds
//     directly. __Init is a no-op placeholder (the dispatch instruction
//     explicitly notes this).
//
// =====================================================================

#include "HAL/FPlatformTime.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_LINUX selector

#if XPACT_PLATFORM_LINUX

#include <atomic>
#include <cstdint>
#include <ctime>     // clock_gettime, CLOCK_MONOTONIC_RAW, timespec

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// File-scope state.
//
// g_OriginNs: nanoseconds captured at __Init() so Cycles64 returns
// nanoseconds-since-engine-startup. The unit of "cycle" on Linux is
// "nanosecond" -- there is no QPF equivalent; clock_gettime always
// reports ns. ToSeconds divides by 1e9.
//
// g_TimingInitialised mirrors the Win64 pattern for sentinel checking.
// ---------------------------------------------------------------------
namespace
{
    constinit ::std::int64_t          g_OriginNs            = 0;
    constinit ::std::atomic<bool>     g_TimingInitialised   { false };

    // 1.0 / 1e9 precomputed for ToSeconds. constinit double is fine.
    constinit double                  g_SecondsPerCycle     = 1e-9;
}

// ---------------------------------------------------------------------
// __Init -- capture the origin tick.
//
// No calibration loop required (Linux nanosecond units are exact). The
// only state to set is g_OriginNs.
// ---------------------------------------------------------------------
void FPlatformTime::__Init() noexcept
{
    timespec Ts;
    if (::clock_gettime(CLOCK_MONOTONIC_RAW, &Ts) != 0)
    {
        // clock_gettime on CLOCK_MONOTONIC_RAW is documented to never
        // fail except for EFAULT (invalid pointer). Leave g_OriginNs=0
        // on the unreachable error path.
        return;
    }
    g_OriginNs =
        static_cast<::std::int64_t>(Ts.tv_sec)  * 1'000'000'000LL +
        static_cast<::std::int64_t>(Ts.tv_nsec);
    g_TimingInitialised.store(true, ::std::memory_order_release);
}

// ---------------------------------------------------------------------
// Cycles64 -- clock_gettime(MONOTONIC_RAW) nanoseconds since __Init.
//
// Returns ns-since-startup. The conversion to nanoseconds happens
// in-line; no allocation; no syscall beyond clock_gettime itself.
// ---------------------------------------------------------------------
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

// ---------------------------------------------------------------------
// ToSeconds -- Cycles (ns) * 1e-9.
//
// Multiply by the precomputed reciprocal is faster than divide by 1e9
// on every supported microarchitecture; both produce identical results
// for representable values.
// ---------------------------------------------------------------------
double FPlatformTime::ToSeconds(::std::uint64_t Cycles) noexcept
{
    return static_cast<double>(Cycles) * g_SecondsPerCycle;
}

// ---------------------------------------------------------------------
// Seconds -- composed from Cycles64 + ToSeconds.
// ---------------------------------------------------------------------
double FPlatformTime::Seconds() noexcept
{
    return ToSeconds(Cycles64());
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_LINUX
