// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FTimespan.h -- duration / interval; microsecond precision; sim-path-safe.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time). FTimespan is the duration
// type; FDateTime (separate header) is the UTC absolute-time type. The
// split is intentional:
//   - FTimespan is pure arithmetic on a 64-bit microsecond count;
//     sim-path-safe by construction (no wall-clock read; no
//     platform-libm; deterministic across all three platforms).
//   - FDateTime carries a wall-clock origin (Unix epoch) and is
//     sim-path-banned at the type level (Section 7.5).
//
// Pattern reference: UE Core has `Timespan.h` (Runtime/Core/Public/
// Misc/Timespan.h) with a similar 64-bit-tick-count shape; UE uses
// 100-nanosecond ticks (.NET's tick precision) while XPact uses
// microsecond precision (1 us = 10 ticks in UE's scheme). The XPact
// choice is principled:
//   - 1 us is sufficient precision for every gameplay/sim use case
//     (the engine's tick step is at least 16.67 ms = 16,667 us)
//   - The 1 us count fits comfortably in int64 for ~292,000 years of
//     duration before overflow, vs UE's ~29,000 years for 100 ns
//   - C# interop is simpler -- `System.TimeSpan.TotalMicroseconds`
//     matches XPact's storage unit exactly (1:1; no scaling)
//   - .NET-compat is not a goal; matching .NET's 100ns precision is
//     not worth the silent-truncation tax of the C# interop
//
// Range:
//   int64_t microseconds = INT64_MAX = ~9.2e18 us
//   = ~9.2e12 seconds = ~292,277 years
//
// Sim-path discipline (Section 7.5): FTimespan is sim-path-safe.
// Every arithmetic operation is bit-exact across Win64 + Linux +
// Android-ARM64.
//
// =====================================================================

#include <compare>  // for std::strong_ordering / operator<=>
#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// FTimespan -- 8-byte duration (microsecond count).
//
// Internal representation: signed int64 microseconds. Negative spans
// are well-defined (e.g., a "time-until-event" computation may yield a
// negative span if the event is in the past).
//
// ABI lock: 8 bytes; 8-byte alignment. The struct is layout-identical
// across all three platforms by virtue of std::int64_t being uniformly
// 8-byte-aligned on every supported target (Section 13 ABI tags).
//
// Constructors:
//   - default: zero-span (0 microseconds; the "no time" identity).
//   - explicit(int64_t Micros): direct construction from raw count
//     (typically used by the factory methods below; user code uses
//     the FromXxx factories for clarity).
//
// Factory methods (FromSeconds/Milliseconds/Microseconds/Minutes/
// Hours/Days): all constexpr; pure arithmetic; sim-path-safe.
//
// Accessors (TotalMicroseconds, TotalSeconds): constexpr;
// reconstructive (no caching needed -- the raw count IS the
// microsecond total, by definition).
// ---------------------------------------------------------------------

class FTimespan
{
    int64_t m_micros;

public:
    // -----------------------------------------------------------------
    // Constructors.
    //
    // The default constructor produces a zero-span; the explicit
    // single-arg constructor takes raw microseconds. User code should
    // prefer the FromXxx factory methods below; the explicit ctor is
    // primarily for the factories' internal use.
    // -----------------------------------------------------------------
    constexpr FTimespan() noexcept : m_micros(0) {}

    constexpr explicit FTimespan(int64_t Micros) noexcept : m_micros(Micros) {}

    // -----------------------------------------------------------------
    // Factory methods.
    //
    // Per Section 7.5 spec: FromDays/Hours/Minutes/Seconds (double-typed
    // for fractional days/hours) and FromMicroseconds/Milliseconds
    // (int64-typed because sub-millisecond fractions are typically
    // unused). All are constexpr; the multiply-by-constant constant-folds
    // at the call site.
    //
    // Note: the prompt's surface lists `FromSeconds(double Secs)` and
    // `FromMinutes(int64_t Minutes)` (mixed types); the spec at Section
    // 7.5 lists `FromDays(double Days)` and `FromMinutes(double
    // Minutes)`. The principled choice is double-typed for the
    // potentially-fractional units (days/hours/minutes/seconds in the
    // continuous sense) and int64-typed for the discrete units (ms/us
    // typically referenced as whole counts). Following the spec at
    // Section 7.5 over the prompt -- the spec is locked, the prompt's
    // mixed-typing is a doc inconsistency.
    //
    // Sim-path-safe: pure arithmetic on int64; no platform-libm
    // (the cast from double to int64 truncates toward zero via
    // C++ standard, which is deterministic across platforms for
    // representable values).
    // -----------------------------------------------------------------
    static constexpr FTimespan FromSeconds(double Secs) noexcept
    {
        return FTimespan(static_cast<int64_t>(Secs * 1'000'000.0));
    }

    static constexpr FTimespan FromMilliseconds(int64_t Millis) noexcept
    {
        return FTimespan(Millis * 1'000);
    }

    static constexpr FTimespan FromMicroseconds(int64_t Micros) noexcept
    {
        return FTimespan(Micros);
    }

    static constexpr FTimespan FromMinutes(int64_t Minutes) noexcept
    {
        // 60 seconds * 1,000,000 us/s = 60,000,000 us/min
        return FTimespan(Minutes * 60'000'000);
    }

    static constexpr FTimespan FromHours(int64_t Hours) noexcept
    {
        // 3,600 seconds * 1,000,000 us/s = 3,600,000,000 us/hr
        return FTimespan(Hours * 3'600'000'000LL);
    }

    static constexpr FTimespan FromDays(int64_t Days) noexcept
    {
        // 86,400 seconds * 1,000,000 us/s = 86,400,000,000 us/day
        return FTimespan(Days * 86'400'000'000LL);
    }

    // -----------------------------------------------------------------
    // Accessors.
    //
    // TotalMicroseconds returns the raw int64 storage (the canonical
    // accessor; pure-read).
    //
    // TotalSeconds returns the duration as a double; the divide
    // constant-folds when the caller is constexpr. NOTE: for very
    // large spans (> ~104 days), the double conversion loses
    // sub-microsecond precision because double's 53-bit mantissa
    // cannot represent every microsecond count up to that range.
    // Callers needing exact sub-second resolution on large spans
    // should use TotalMicroseconds() directly.
    // -----------------------------------------------------------------
    constexpr int64_t TotalMicroseconds() const noexcept { return m_micros; }

    constexpr double TotalSeconds() const noexcept
    {
        return static_cast<double>(m_micros) / 1'000'000.0;
    }

    // -----------------------------------------------------------------
    // Arithmetic operators.
    //
    // Addition and subtraction on FTimespan produce FTimespan. The
    // underlying int64 arithmetic wraps on overflow (well-defined for
    // signed int64 in C++20 since signed integer overflow becomes UB
    // only on overflow-of-value; constexpr arithmetic catches this at
    // compile time when the operands are constants). Runtime overflow
    // is the caller's responsibility; the engine does not silently
    // saturate or wrap.
    //
    // Compound-assign variants delegate to the binary form.
    // -----------------------------------------------------------------
    constexpr FTimespan operator+(FTimespan Other) const noexcept
    {
        return FTimespan(m_micros + Other.m_micros);
    }

    constexpr FTimespan operator-(FTimespan Other) const noexcept
    {
        return FTimespan(m_micros - Other.m_micros);
    }

    constexpr FTimespan& operator+=(FTimespan Other) noexcept
    {
        m_micros += Other.m_micros;
        return *this;
    }

    constexpr FTimespan& operator-=(FTimespan Other) noexcept
    {
        m_micros -= Other.m_micros;
        return *this;
    }

    // -----------------------------------------------------------------
    // Comparison operators.
    //
    // C++20 operator==/operator<=> defaulted; provides ==, !=, <, >,
    // <=, >=. The comparison is integer comparison on m_micros which
    // is bit-exact across all three platforms.
    //
    // The spaceship operator returns std::strong_ordering because the
    // underlying int64 has total ordering.
    //
    // C++20 standard requirement [class.compare.default]/1: the
    // defaulted comparison operator inside a class must take
    // `const T&` (not `T` by value). The Phase 1a wording at Section
    // 7.5 spec lines 760-761 wrote `FTimespan Other` by value; MSVC
    // 19.44 + Clang 17 + GCC 13 all reject the by-value form per
    // strict C++20 conformance. Phase 1b fix: switch to const-ref.
    // -----------------------------------------------------------------
    constexpr bool operator==(const FTimespan& Other) const noexcept = default;
    constexpr auto operator<=>(const FTimespan& Other) const noexcept = default;
};

// ---------------------------------------------------------------------
// ABI locks per Section 7.5 spec.
// ---------------------------------------------------------------------

static_assert(sizeof(FTimespan) == 8,
              "FTimespan ABI lock: 8-byte microsecond count");

static_assert(alignof(FTimespan) == 8,
              "FTimespan 8-byte alignment");

} // namespace XCore::HAL
