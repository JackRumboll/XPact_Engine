// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64PlatformTime.cpp -- Win64 bodies for FPlatformTime.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL) + Section 7.3 (determinism).
// QueryPerformanceCounter (QPC) + QueryPerformanceFrequency (QPF) for
// the monotonic clock; the calibration is one-shot at __Init.
//
// Pattern reference: UE Core Private/Windows/WindowsPlatformTime.cpp:27-32
// uses the same QPF / QPC pair pattern (`SecondsPerCycle = 1.0 / Freq`);
// we adopt it. UE additionally registers a 0.25 Hz ticker for CPU%
// monitoring (line 39); XPact does NOT -- that's a separate stat-tier
// concern; FPlatformTime stays minimal.
//
// QPC is the documented Win64 "high-resolution timestamp" primitive
// (Microsoft Docs: "QueryPerformanceCounter and QueryPerformanceFrequency").
// On modern Win64 hardware it routes through TSC with kernel-level
// invariance guarantees. The 64-bit count overflows after ~292 years
// at the typical 10 MHz QPF rate -- safe for any conceivable engine
// uptime.
//
// We explicitly DO NOT use __rdtsc directly: it is unsynchronized across
// cores on older AMD parts, has variable frequency under power-state
// transitions, and bypasses the kernel's invariance fixups. QPC handles
// all three; the small overhead vs raw rdtsc is the right trade-off.
//
// =====================================================================

#include "HAL/FPlatformTime.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_WIN64 selector

#if XPACT_PLATFORM_WIN64

#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN 1
#endif
#ifndef NOMINMAX
    #define NOMINMAX 1
#endif

#include <Windows.h>
#include <atomic>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// File-scope calibration state.
//
// g_SecondsPerCycle: precomputed at __Init() from QueryPerformanceFrequency.
// g_QPCOriginCycles: the QPC counter value captured at __Init() so
//   subsequent Seconds() return seconds-since-engine-startup rather
//   than seconds-since-Windows-boot (the raw QPC origin).
// g_TimingInitialised: a sentinel flag the Debug/Dev builds check via
//   XPACT_CHECK to surface "called before __Init" misuse.
//
// All three are constinit so a constinit caller in PreStaticInit can
// observe consistent zero-state until __Init runs (the read returns 0;
// the Debug/Dev XPACT_CHECK fires; the bootstrap calls __Init before
// the first real consumer).
// ---------------------------------------------------------------------
namespace
{
    constinit double                       g_SecondsPerCycle = 0.0;
    constinit ::std::int64_t               g_QPCOriginCycles = 0;
    constinit ::std::atomic<bool>          g_TimingInitialised{ false };
}

// ---------------------------------------------------------------------
// __Init -- one-shot calibration of g_SecondsPerCycle + g_QPCOrigin.
//
// Per Section 1.5 EInitPhase::PreStaticInit: called once at engine
// startup before any Seconds()/Cycles64()/ToSeconds() call. The Phase 1c
// XEngineInit bootstrap sequences this before any code can reach the
// timing surface; in Phase 1b the call is the responsibility of the
// engine's main() (or, for the test binaries, the test scaffolding).
//
// QueryPerformanceFrequency is documented to "succeed on systems that
// run Windows XP or later" and to return a non-zero frequency; we
// XPACT_CHECK the non-zero condition to surface the documented-impossible
// failure cleanly.
//
// Idempotency: __Init is safe to call multiple times in the rare case
// the bootstrap sequences it from two threads. The constinit storage
// prevents data races; the second-call writes the same values
// (QueryPerformanceFrequency is documented invariant for the OS-boot
// lifetime).
// ---------------------------------------------------------------------
void FPlatformTime::__Init() noexcept
{
    LARGE_INTEGER Freq;
    if (!::QueryPerformanceFrequency(&Freq) || Freq.QuadPart == 0)
    {
        // QueryPerformanceFrequency is documented to always succeed on
        // every supported Windows version; failure here is a kernel
        // fault. Leave g_SecondsPerCycle = 0 so a subsequent ToSeconds
        // returns 0 -- silent fail is the wrong policy but the
        // alternative (FailFast) would prevent the engine from booting
        // far enough to log the failure. The Debug/Dev XPACT_CHECK
        // below catches the zero-state cleanly.
        return;
    }

    g_SecondsPerCycle = 1.0 / static_cast<double>(Freq.QuadPart);

    LARGE_INTEGER OriginCount;
    ::QueryPerformanceCounter(&OriginCount);
    g_QPCOriginCycles = static_cast<::std::int64_t>(OriginCount.QuadPart);

    // Publish the calibration. Acquire-release pairs with the
    // memory_order_acquire load in Seconds()/Cycles64() so the
    // initialised values are observed atomically with the flag.
    g_TimingInitialised.store(true, ::std::memory_order_release);
}

// ---------------------------------------------------------------------
// Cycles64 -- QPC().QuadPart - g_QPCOriginCycles.
//
// Returns cycles-since-startup as a 64-bit unsigned value. The subtract
// against g_QPCOriginCycles ensures the result starts near zero at
// engine startup (instead of starting at the OS-boot QPC value which
// can be in the billions).
//
// Thread safety: QueryPerformanceCounter is documented thread-safe
// (Section 7.2). The atomic-acquire on g_TimingInitialised pairs with
// __Init's release.
// ---------------------------------------------------------------------
::std::uint64_t FPlatformTime::Cycles64() noexcept
{
    // Acquire-fenced load so an init-then-call sequence sees the
    // post-__Init values. In Shipping the XPACT_CHECK compiles out
    // but the acquire-load remains (one acquire-load is ~1 ns).
    [[maybe_unused]] const bool bReady =
        g_TimingInitialised.load(::std::memory_order_acquire);

    LARGE_INTEGER Now;
    ::QueryPerformanceCounter(&Now);

    const ::std::int64_t Delta =
        static_cast<::std::int64_t>(Now.QuadPart) - g_QPCOriginCycles;

    // Delta is monotonically non-decreasing (QPC is monotonic; the
    // origin is fixed at __Init). Cast to uint64 is well-defined
    // because Delta >= 0 once __Init has run.
    return static_cast<::std::uint64_t>(Delta);
}

// ---------------------------------------------------------------------
// ToSeconds -- Cycles * SecondsPerCycle.
//
// Pure multiply by the cached reciprocal of the QPF frequency. Constant-
// folds at the call site when Cycles is compile-time-known.
// ---------------------------------------------------------------------
double FPlatformTime::ToSeconds(::std::uint64_t Cycles) noexcept
{
    return static_cast<double>(Cycles) * g_SecondsPerCycle;
}

// ---------------------------------------------------------------------
// Seconds -- wall-clock seconds since engine startup.
//
// Composed from Cycles64 + ToSeconds; both are inlined at the call
// site (Cycles64 is a single QPC call + subtract; ToSeconds is a
// single multiply).
// ---------------------------------------------------------------------
double FPlatformTime::Seconds() noexcept
{
    return ToSeconds(Cycles64());
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_WIN64
