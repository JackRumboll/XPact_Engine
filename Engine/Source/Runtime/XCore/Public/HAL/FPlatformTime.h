// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformTime.h -- abstract platform-time surface.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL) + Section 7.3 (determinism
// contract) + Section 6.3 (sim-path-disciplined math constrains time).
// Per-OS concrete implementations land in Phase 1b.
//
// Pattern reference: UE Core `GenericPlatformTime.h:42-80` defines
// `FGenericPlatformTime` with `Seconds()`, `Cycles()`, `Cycles64()`,
// and an `InitTiming` calibration. XPact's surface is tighter:
//   - No 32-bit `Cycles()` (only `Cycles64()`); UE's 32-bit is a
//     ticking-time-bomb on long-running services.
//   - No `SystemTime`/`UtcTime`/`StrDate`/`StrTime` -- those belong on
//     FDateTime (Section 7.5).
//   - Added `DeterministicTickSeconds(TickIndex)` -- the sim-path-safe
//     replacement for `Seconds()`. UE has no sim-path equivalent
//     (UE-divergence Section 7.6 row 2).
//
// Determinism contract (Section 7.3): `Seconds()` reads a monotonic
// platform clock (QueryPerformanceCounter on Win64; CLOCK_MONOTONIC_RAW
// on Linux/Android); the result is platform-specific (drift differs by
// clock source; precision varies). Sim-path TUs MUST NOT call
// `Seconds()` -- the sim-path overlay header (Phase 1e) decorates it
// with `[[deprecated("not sim-path-safe; use
// DeterministicTickSeconds")]]`. Sim-path code uses
// `DeterministicTickSeconds(TickIndex)` -- pure arithmetic over a
// fixed TickRate, bit-exact across all three platforms.
//
// Abstract surface only -- NO .cpp bodies in Phase 1a (except __Init,
// which is a constexpr no-op stub here; the real calibration lands in
// Phase 1b). Test compiles must NOT link these symbols.
//
// =====================================================================

#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// kTickRate -- sim-path simulation tick rate (Hz).
//
// Per Section 7.3: default 60 Hz client / 30 Hz server. For Phase 1a
// this is a single hardcoded compile-time constant; Phase 1b integrates
// with TargetRules so the constant resolves per-build to the right
// value via the target's ConfigurationVariant.
//
// TODO(Phase 1b): wire `kTickRate` to TargetRules.TickRate; client
// builds resolve to 60.0, server builds resolve to 30.0. Until then,
// the 60.0 default applies to every build.
//
// The constant is `constexpr double` so the divide in
// `DeterministicTickSeconds` constant-folds at the call site.
// ---------------------------------------------------------------------

inline constexpr double kTickRate = 60.0;  // TODO(Phase 1b): TargetRules-driven

// ---------------------------------------------------------------------
// FPlatformTime -- abstract platform-time surface.
//
// Class shape mirrors FPlatformMisc: static-only, no virtuals, no
// instantiation. The per-OS .cpp bodies in Phase 1b provide bodies
// for `Seconds`, `Cycles64`, `ToSeconds`, and `__Init`; the
// `DeterministicTickSeconds` body is inline-defined here because it
// is platform-independent pure arithmetic.
// ---------------------------------------------------------------------

class FPlatformTime
{
public:
    // -----------------------------------------------------------------
    // Wall-clock seconds since engine startup (NOT sim-path-safe).
    //
    // Phase 1b implementation:
    //   - Win64: QueryPerformanceCounter() / QueryPerformanceFrequency()
    //   - Linux: clock_gettime(CLOCK_MONOTONIC_RAW); ts.tv_sec +
    //            ts.tv_nsec/1e9
    //   - Android: clock_gettime(CLOCK_MONOTONIC_RAW); same as Linux
    //
    // Monotonic guarantee: the result is non-decreasing across calls on
    // the same thread (per Section 7.2 threading contract).
    //
    // SIM-PATH DEPRECATION (Phase 1e overlay): the sim-path overlay
    // header decorates this method with
    //     [[deprecated("not sim-path-safe; use
    //                   DeterministicTickSeconds")]]
    // -----------------------------------------------------------------
    static double Seconds() noexcept;

    // -----------------------------------------------------------------
    // Raw CPU cycle counter (NOT sim-path-safe).
    //
    // Phase 1b implementation:
    //   - Win64: QueryPerformanceCounter().QuadPart (NOT __rdtsc -- QPC
    //            is the documented "high-resolution timestamp" path)
    //   - Linux: clock_gettime(CLOCK_MONOTONIC_RAW) converted to
    //            cycles via the calibrated SecondsPerCycle
    //   - Android: same as Linux
    //
    // 64-bit width: UE's 32-bit `Cycles()` overflows in ~36 minutes at
    // 2GHz; we ship only `Cycles64()` (Section 7.6 divergence row 2).
    // -----------------------------------------------------------------
    static uint64_t Cycles64() noexcept;

    // -----------------------------------------------------------------
    // Convert a cycle count to seconds via the calibrated rate.
    //
    // Phase 1b implementation:
    //   `return Cycles * SecondsPerCycle;` where SecondsPerCycle is
    //   computed at __Init() time from QueryPerformanceFrequency() on
    //   Win64 or the calibration loop on Linux/Android.
    // -----------------------------------------------------------------
    static double ToSeconds(uint64_t Cycles) noexcept;

    // -----------------------------------------------------------------
    // Sim-path-safe deterministic tick-time.
    //
    // Returns `TickIndex / kTickRate` -- pure arithmetic; no platform
    // syscall; no clock read; bit-exact across all three platforms for
    // identical TickIndex.
    //
    // Body is inline-defined here because it's platform-independent
    // pure arithmetic. The divide constant-folds at the call site when
    // TickIndex is a compile-time constant (rare); the per-tick read
    // pattern is a single double-precision multiply by reciprocal.
    //
    // SIM-PATH SAFE: this is the only time-reading method sim-path TUs
    // may call. Verlet integration over identical initial conditions
    // produces bit-identical positions across Win64 + Linux + Android
    // because the tick stride is bit-exact (Section 7.7 + Section 6.6
    // acceptance criterion D-extra).
    // -----------------------------------------------------------------
    static constexpr double DeterministicTickSeconds(uint64_t TickIndex) noexcept
    {
        return static_cast<double>(TickIndex) / kTickRate;
    }

    // -----------------------------------------------------------------
    // One-time calibration of the seconds-per-cycle constant.
    //
    // Called once at engine startup before any `Seconds()` or
    // `ToSeconds()` call (Section 1.5 EInitPhase::PreStaticInit; the
    // bootstrap in XEngineInit sequences this before any code can
    // reach the timing surface).
    //
    // Phase 1b implementation:
    //   - Win64: QueryPerformanceFrequency() -> SecondsPerCycle =
    //            1.0 / Freq.QuadPart; capture the QPC origin tick into
    //            a static so subsequent Seconds() return delta-since.
    //   - Linux: clock_gettime(CLOCK_MONOTONIC_RAW) twice with a small
    //            calibration loop; convert to SecondsPerCycle stored at
    //            file scope.
    //   - Android: same as Linux.
    //
    // Calling Seconds()/Cycles64()/ToSeconds() before __Init() is a
    // contract violation; Debug/Dev builds abort via XPACT_CHECK; the
    // bootstrap ordering guarantees this is impossible in practice.
    // -----------------------------------------------------------------
    static void __Init() noexcept;
};

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Implement FPlatformTime::Seconds per OS (QPC / CLOCK_MONOTONIC_RAW).
//   - Implement FPlatformTime::Cycles64 per OS.
//   - Implement FPlatformTime::ToSeconds via the calibrated rate.
//   - Implement FPlatformTime::__Init via QueryPerformanceFrequency
//     (Win64) and a calibration loop (Linux/Android).
//   - Integrate kTickRate with TargetRules: client = 60.0, server =
//     30.0; selected via the build's ConfigurationVariant.
// =====================================================================
