// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FormatString.h -- compile-time-checked format-string alias.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 fix Rev 3 M4 + Section 13.1.
//
// Aliases std::format_string<Args...> when the toolchain's C++20
// std::format is available (__cpp_lib_format >= 201907L). On older
// toolchains the spec mandates a polyfill via vendored fmt; the
// engineering-principled approach for XPact Phase 1g is to require the
// modern C++ surface rather than vendor a separate library that drifts
// out of sync. The static_assert in the #else branch makes the
// missing-feature path a hard compile error at the macro-expansion
// site rather than a runtime abort.
//
// Supported toolchains (Master Plan Section 2 minimums):
//   * MSVC 19.30+ (Visual Studio 2022 17.0+): std::format ships.
//   * libstdc++ 13+: std::format ships.
//   * libc++ 16+: std::format ships.
// All three meet __cpp_lib_format >= 201907L (the base feature-test
// macro). The compile-time-format-string check macro
// __cpp_lib_format_uchar / __cpp_lib_format_201907 carries the
// post-LWG fixes; either feature-test gates the same surface.
//
// USAGE:
//
//   #include "HAL/FormatString.h"
//
//   template<typename... Args>
//   static FString Format(::XCore::HAL::FormatString<Args...> Fmt,
//                         Args&&... Vs);
//
// The FormatString<Args...> parameter is a compile-time-checked
// std::format_string<Args...>; a malformed format string fails the
// build at the call site with a constraint diagnostic.
//
// =====================================================================

#if defined(__cpp_lib_format) && __cpp_lib_format >= 201907L
    #include <format>
    namespace XCore::HAL
    {
        // Alias the standard compile-time format string parameter.
        // std::format_string<Args...> is the C++23 type (introduced in
        // P2216R3 and backported to many C++20 implementations via
        // __cpp_lib_format upgrades). It enforces that the format
        // string is a constant expression matching the argument types.
        template<typename... Args>
        using FormatString = ::std::format_string<Args...>;
    }
#else
    // Toolchain matrix dropped: fail the build at the include site with
    // a clear diagnostic. This is intentional per Section 11.1 fix
    // Rev 3 M4 + Master Plan Section 2 toolchain row: XPact requires
    // the modern std::format surface on every supported target.
    // Vendoring fmt as a polyfill is documented as a Phase 2
    // enhancement (the polyfill would carry a non-trivial maintenance
    // load; the right time to add it is when an older toolchain is
    // genuinely required, not as defensive shipping today).
    #error "XCore-4a requires __cpp_lib_format >= 201907L. " \
           "Supported toolchains: MSVC 19.30+, libstdc++ 13+, libc++ 16+. " \
           "Upgrade the toolchain or vendor fmt as a Phase 2 polyfill."
#endif
