// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FDateTime.cpp -- FDateTime body implementations.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time).
//
// Method bodies:
//   * UtcNow()              -- platform monotonic + UTC origin delta.
//   * FromUnixTimestamp     -- range-validated micros = seconds * 1e6.
//   * FromUnixMicros        -- range-validated raw micros assignment.
//   * ParseIso8601          -- routes through XDateTimeAlgorithms.cpp.
//   * ToIso8601             -- routes through XDateTimeAlgorithms.cpp.
//   * Year/Month/Day/Hour/Minute/Second/Microsecond accessors.
//
// All FDateTime arithmetic is bit-exact across the three XPact targets
// per acceptance criterion D-extra (Section 7.5 / Section 17.4) because:
//   * Storage is int64 microseconds-since-Unix-epoch.
//   * The accessor + ParseIso8601 + ToIso8601 paths route through
//     XDateTimeAlgorithms.h's proleptic-Gregorian primitives (pure
//     arithmetic; no platform CRT date routine).
//   * UtcNow() is the only platform-divergent surface, and it returns
//     a single microsecond value derived from the platform's monotonic
//     clock + a UTC-origin delta captured at __Init.
//
// Cross-platform: FDateTime.cpp is built for every platform. The Phase
// 1b platform divergence lives in the UtcOriginCapture below, which
// uses the platform-specific wall-clock APIs.
//
// =====================================================================

#include "HAL/FDateTime.h"
#include "HAL/FPlatformTime.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"

#include "Containers/FString.h"     // FString concrete definition (Phase 1g)
#include "XDateTimeAlgorithms.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <ctime>      // for the POSIX path's clock_gettime / timespec
#include <string_view>

#if XPACT_PLATFORM_WIN64
    #ifndef WIN32_LEAN_AND_MEAN
        #define WIN32_LEAN_AND_MEAN 1
    #endif
    #ifndef NOMINMAX
        #define NOMINMAX 1
    #endif
    #include <Windows.h>
#endif

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// Platform-specific UTC origin capture.
//
// Strategy (per FDateTime.h header note):
//   1. At engine startup (FDateTime::__Init), capture a pair of
//      (wall-clock-now, monotonic-now) readings.
//   2. UtcNow() computes (FPlatformTime::Cycles64() - origin_monotonic)
//      and adds origin_utc_micros. The wall-clock value is read ONCE
//      at startup; subsequent UtcNow() calls compose against the
//      monotonic clock, sidestepping NTP-adjustment jumps.
//
// Storage is constinit so constinit consumers can compile against
// this header; the actual capture happens in __Init.
// ---------------------------------------------------------------------
namespace
{
    // Origin captured at __Init: the unix-micros value reading at the
    // same instant as the monotonic-clock origin.
    constinit ::std::int64_t          g_UtcOriginMicros = 0;

    // Origin captured at __Init: the monotonic-clock cycles count at
    // the same instant as the UTC origin. We compute the delta
    // (FPlatformTime::Cycles64 - g_MonotonicOriginCycles) and convert
    // to micros via FPlatformTime::ToSeconds to get
    // (now - origin) in seconds. Multiplied by 1e6 to micros.
    constinit ::std::uint64_t         g_MonotonicOriginCycles = 0;

    // One-shot init guard.
    constinit ::std::atomic<bool>     g_InitDone{ false };

    // ---------------------------------------------------------
    // CaptureWallclockMicros -- read the platform wall-clock and
    // return as microseconds since Unix epoch.
    //
    // Per Section 7.5 LOCKED restriction: NO PLATFORM CRT DATE
    // ROUTINES. We use:
    //   * Win64: GetSystemTimeAsFileTime (returns 100-ns ticks since
    //            1601-01-01); convert to unix-micros via the documented
    //            constant offset (11644473600 seconds = 116444736000000000
    //            * 100ns).
    //   * Linux/Android: clock_gettime(CLOCK_REALTIME) returns timespec
    //            (sec + ns); convert to unix-micros.
    // Both routes bypass localtime/gmtime/strftime/strptime.
    // ---------------------------------------------------------
    [[nodiscard]] ::std::int64_t CaptureWallclockMicros() noexcept
    {
#if XPACT_PLATFORM_WIN64
        // FILETIME counts 100-ns intervals since January 1, 1601 UTC.
        // The difference between 1601 and 1970 is 11644473600 seconds
        // = 116444736000000000 hundred-ns intervals.
        constexpr ::std::int64_t WIN_EPOCH_TO_UNIX_100NS = 116444736000000000LL;
        FILETIME Ft;
        ::GetSystemTimeAsFileTime(&Ft);
        ULARGE_INTEGER U;
        U.LowPart  = Ft.dwLowDateTime;
        U.HighPart = Ft.dwHighDateTime;
        const ::std::int64_t HundredNs =
            static_cast<::std::int64_t>(U.QuadPart) - WIN_EPOCH_TO_UNIX_100NS;
        return HundredNs / 10LL;  // 100ns -> us
#elif XPACT_PLATFORM_LINUX || XPACT_PLATFORM_ANDROID
        timespec Ts;
        if (::clock_gettime(CLOCK_REALTIME, &Ts) != 0)
        {
            return 0;
        }
        return static_cast<::std::int64_t>(Ts.tv_sec) * 1'000'000LL +
               static_cast<::std::int64_t>(Ts.tv_nsec) / 1'000LL;
#else
        return 0;
#endif
    }

    // EnsureInitialised -- lazy __Init for callers that race before
    // the bootstrap. The first-call winner captures the origins; later
    // callers see g_InitDone == true and skip.
    void EnsureInitialised() noexcept
    {
        if (g_InitDone.load(::std::memory_order_acquire))
        {
            return;
        }
        // Race lazy-init: the compare_exchange single-writer pattern.
        static constinit ::std::atomic<bool> InitInFlight{ false };
        bool Expected = false;
        if (!InitInFlight.compare_exchange_strong(
                Expected, true,
                ::std::memory_order_acq_rel,
                ::std::memory_order_acquire))
        {
            // A sister thread is initialising; spin until visible.
            while (!g_InitDone.load(::std::memory_order_acquire))
            {
                // Bounded spin; the init takes <1 us.
            }
            return;
        }

        // Tight-pair capture: read monotonic origin, then wall-clock
        // origin. The order matters slightly for accuracy under heavy
        // jitter; we read monotonic first because Cycles64 is the
        // cheaper call (no syscall on Win64; one syscall on POSIX).
        const ::std::uint64_t MonoOrigin = FPlatformTime::Cycles64();
        const ::std::int64_t  UtcOrigin  = CaptureWallclockMicros();

        g_MonotonicOriginCycles = MonoOrigin;
        g_UtcOriginMicros       = UtcOrigin;
        g_InitDone.store(true, ::std::memory_order_release);
    }
}

// ---------------------------------------------------------------------
// UtcNow -- monotonic-delta + captured origin.
//
// Per the FDateTime.h header strategy:
//   UtcNow = (monotonic_now - monotonic_origin) + utc_origin
//
// The cast to micros uses FPlatformTime::ToSeconds * 1e6 to bridge from
// cycle count to micros. The double-precision multiply is safe for the
// engine's uptime range (~10 hours -> ~36e9 us = ~5.6 * 2^33; double's
// 53-bit mantissa loses no precision below 2^53).
//
// LOCKED contract: no platform CRT date routines. CaptureWallclockMicros
// uses GetSystemTimeAsFileTime / clock_gettime(REALTIME) -- both of
// which return integer microseconds-since-epoch via documented
// conversions, NOT CRT date routines (no localtime / mktime / gmtime).
// ---------------------------------------------------------------------
FDateTime FDateTime::UtcNow() noexcept
{
    EnsureInitialised();

    const ::std::uint64_t Now = FPlatformTime::Cycles64();
    const ::std::uint64_t Delta = Now - g_MonotonicOriginCycles;

    // Convert cycle-delta to micros via the platform's seconds-per-cycle.
    const double DeltaSeconds = FPlatformTime::ToSeconds(Delta);
    const ::std::int64_t DeltaMicros =
        static_cast<::std::int64_t>(DeltaSeconds * 1'000'000.0);

    return FDateTime(g_UtcOriginMicros + DeltaMicros);
}

// ---------------------------------------------------------------------
// FromUnixTimestamp -- seconds -> micros with range check.
//
// Range guard: |Seconds| <= INT64_MAX / 1'000'000 ~= 9.2e12 seconds
// = ~292,277 years. An input outside this range returns
// InvalidUnixTimestamp.
// ---------------------------------------------------------------------
Result<FDateTime, FDateRangeError> FDateTime::FromUnixTimestamp(::std::int64_t Seconds) noexcept
{
    constexpr ::std::int64_t MAX_SECONDS = INT64_MAX / 1'000'000LL;
    if (Seconds > MAX_SECONDS || Seconds < -MAX_SECONDS)
    {
        return ::XCore::Unexpected(FDateRangeError::InvalidUnixTimestamp);
    }
    return FDateTime(Seconds * 1'000'000LL);
}

// ---------------------------------------------------------------------
// FromUnixMicros -- raw micros with range check.
//
// The proleptic-Gregorian algorithm represents years in int32; this
// caps the date range at roughly ±5.8 million years from the epoch,
// but the int64 micros storage caps at ±292,277 years (a stricter
// limit). Both are satisfied by any input that fits int64.
//
// For Phase 1b we accept any int64 value; the year-overflow case is
// surfaced by civil_from_days returning a year outside [-99999, 99999].
// A future revision may add a stricter check at construction time.
// ---------------------------------------------------------------------
Result<FDateTime, FDateRangeError> FDateTime::FromUnixMicros(::std::int64_t Micros) noexcept
{
    return FDateTime(Micros);
}

// ---------------------------------------------------------------------
// ParseIso8601 -- delegate to XDateTimeAlgorithms.cpp helpers.
//
// EParseStatus maps to FDateRangeError:
//   Malformed         -> InvalidUnixTimestamp (no better fit in the
//                                              uint8 enum without
//                                              adding a new variant)
//   OverflowComponent -> OverflowYear (when year > 9999) or
//                        UnderflowYear (when year < -9999); for non-
//                        year components we conservatively return
//                        InvalidUnixTimestamp.
// ---------------------------------------------------------------------
Result<FDateTime, FDateRangeError> FDateTime::ParseIso8601(::std::string_view Iso8601) noexcept
{
    using namespace ::XCore::HAL::DateAlgorithms;

    ParsedComponents C{};
    const EParseStatus Status = ParseIso8601Components(Iso8601, C);
    if (Status == EParseStatus::Malformed)
    {
        return ::XCore::Unexpected(FDateRangeError::InvalidUnixTimestamp);
    }
    if (Status == EParseStatus::OverflowComponent)
    {
        // Year-specific overflow / underflow gets the dedicated codes.
        if (C.Year > 9999)
        {
            return ::XCore::Unexpected(FDateRangeError::OverflowYear);
        }
        if (C.Year < 0)
        {
            return ::XCore::Unexpected(FDateRangeError::UnderflowYear);
        }
        return ::XCore::Unexpected(FDateRangeError::InvalidUnixTimestamp);
    }

    const ::std::int64_t Micros = ComponentsToUnixMicros(C);
    return FDateTime(Micros);
}

// ---------------------------------------------------------------------
// ToIso8601 -- emit YYYY-MM-DDTHH:MM:SS.ffffffZ.
//
// Construct an FString from the EmitIso8601 UTF-8 byte buffer. The
// algorithm is sim-path-safe (pure proleptic-Gregorian arithmetic)
// and bit-exact across the three XPact targets. The body also fronts
// the XPACT_TEST_EmitIso8601 `extern "C"` shim defined at the bottom
// of this file which the round-trip property test (Section 17.4
// D-extra-2) exercises directly against the byte buffer.
// ---------------------------------------------------------------------
FString FDateTime::ToIso8601() const
{
    char Buf[32] = { 0 };
    const ::std::size_t Written =
        ::XCore::HAL::DateAlgorithms::EmitIso8601(m_unixMicros, Buf, sizeof(Buf));
    return FString(Buf, static_cast<::int32>(Written));
}

// ---------------------------------------------------------------------
// Year / Month / Day / Hour / Minute / Second / Microsecond accessors.
//
// Derive each field from m_unixMicros via the proleptic-Gregorian
// algorithms. Each accessor is independent (no shared state) so a
// caller that queries all 7 fields pays 7 algorithm runs; a future
// optimisation could expose a single-call `Decompose` helper to amortise.
// ---------------------------------------------------------------------
::std::int32_t FDateTime::Year() const noexcept
{
    using namespace ::XCore::HAL::DateAlgorithms;
    constexpr ::std::int64_t MICROS_PER_DAY = 86'400'000'000LL;
    ::std::int64_t Days = m_unixMicros / MICROS_PER_DAY;
    if (m_unixMicros % MICROS_PER_DAY < 0)
    {
        --Days;
    }
    ::std::int32_t Y = 0, M = 0, D = 0;
    civil_from_days(Days, Y, M, D);
    return Y;
}

::std::int32_t FDateTime::Month() const noexcept
{
    using namespace ::XCore::HAL::DateAlgorithms;
    constexpr ::std::int64_t MICROS_PER_DAY = 86'400'000'000LL;
    ::std::int64_t Days = m_unixMicros / MICROS_PER_DAY;
    if (m_unixMicros % MICROS_PER_DAY < 0)
    {
        --Days;
    }
    ::std::int32_t Y = 0, M = 0, D = 0;
    civil_from_days(Days, Y, M, D);
    return M;
}

::std::int32_t FDateTime::Day() const noexcept
{
    using namespace ::XCore::HAL::DateAlgorithms;
    constexpr ::std::int64_t MICROS_PER_DAY = 86'400'000'000LL;
    ::std::int64_t Days = m_unixMicros / MICROS_PER_DAY;
    if (m_unixMicros % MICROS_PER_DAY < 0)
    {
        --Days;
    }
    ::std::int32_t Y = 0, M = 0, D = 0;
    civil_from_days(Days, Y, M, D);
    return D;
}

::std::int32_t FDateTime::Hour() const noexcept
{
    constexpr ::std::int64_t MICROS_PER_HOUR = 3'600'000'000LL;
    constexpr ::std::int64_t MICROS_PER_DAY  = 86'400'000'000LL;
    ::std::int64_t Remain = m_unixMicros % MICROS_PER_DAY;
    if (Remain < 0)
    {
        Remain += MICROS_PER_DAY;
    }
    return static_cast<::std::int32_t>(Remain / MICROS_PER_HOUR);
}

::std::int32_t FDateTime::Minute() const noexcept
{
    constexpr ::std::int64_t MICROS_PER_MIN  = 60'000'000LL;
    constexpr ::std::int64_t MICROS_PER_HOUR = 3'600'000'000LL;
    constexpr ::std::int64_t MICROS_PER_DAY  = 86'400'000'000LL;
    ::std::int64_t Remain = m_unixMicros % MICROS_PER_DAY;
    if (Remain < 0)
    {
        Remain += MICROS_PER_DAY;
    }
    return static_cast<::std::int32_t>((Remain % MICROS_PER_HOUR) / MICROS_PER_MIN);
}

::std::int32_t FDateTime::Second() const noexcept
{
    constexpr ::std::int64_t MICROS_PER_SEC  = 1'000'000LL;
    constexpr ::std::int64_t MICROS_PER_MIN  = 60'000'000LL;
    constexpr ::std::int64_t MICROS_PER_DAY  = 86'400'000'000LL;
    ::std::int64_t Remain = m_unixMicros % MICROS_PER_DAY;
    if (Remain < 0)
    {
        Remain += MICROS_PER_DAY;
    }
    return static_cast<::std::int32_t>((Remain % MICROS_PER_MIN) / MICROS_PER_SEC);
}

::std::int32_t FDateTime::Microsecond() const noexcept
{
    constexpr ::std::int64_t MICROS_PER_SEC = 1'000'000LL;
    constexpr ::std::int64_t MICROS_PER_DAY = 86'400'000'000LL;
    ::std::int64_t Remain = m_unixMicros % MICROS_PER_DAY;
    if (Remain < 0)
    {
        Remain += MICROS_PER_DAY;
    }
    return static_cast<::std::int32_t>(Remain % MICROS_PER_SEC);
}

} // namespace XCore::HAL

// =====================================================================
// Test-only shims for the ISO 8601 round-trip property test.
//
// The Phase 1g FString dependency forces ToIso8601 to return a
// default-constructed FString for now (see body above). To unblock the
// round-trip verification test (Iso8601RoundTrip.cpp acceptance
// criterion D-extra-2), we export two `extern "C"` shims:
//
//   XPACT_TEST_EmitIso8601(unixMicros, OutBuf, BufSize)
//     Writes the YYYY-MM-DDTHH:MM:SS.ffffffZ representation directly
//     into the caller's buffer. Returns bytes written.
//
//   XPACT_TEST_ParseIso8601(InBuf, InLen, OutMicros)
//     Parses the ISO 8601 string and writes the result to *OutMicros.
//     Returns 1 on success, 0 on parse failure.
//
// These exist only for testing; downstream production code uses
// FDateTime::ToIso8601 / FDateTime::ParseIso8601 directly once FString
// is shipped.
// =====================================================================

extern "C" {

::std::size_t XPACT_TEST_EmitIso8601(
    ::std::int64_t UnixMicros,
    char* OutBuf,
    ::std::size_t BufSize) noexcept
{
    return ::XCore::HAL::DateAlgorithms::EmitIso8601(UnixMicros, OutBuf, BufSize);
}

int XPACT_TEST_ParseIso8601(
    const char* InBuf,
    ::std::size_t InLen,
    ::std::int64_t* OutMicros) noexcept
{
    if (InBuf == nullptr || OutMicros == nullptr)
    {
        return 0;
    }
    ::XCore::HAL::DateAlgorithms::ParsedComponents Comp{};
    const ::XCore::HAL::DateAlgorithms::EParseStatus Status =
        ::XCore::HAL::DateAlgorithms::ParseIso8601Components(
            ::std::string_view{ InBuf, InLen }, Comp);
    if (Status != ::XCore::HAL::DateAlgorithms::EParseStatus::Ok)
    {
        return 0;
    }
    *OutMicros = ::XCore::HAL::DateAlgorithms::ComponentsToUnixMicros(Comp);
    return 1;
}

} // extern "C"
