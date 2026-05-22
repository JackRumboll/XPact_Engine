// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStringFormat.cpp -- FString::Format / FString::FormatFixed bodies.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 fix Rev 3 M4 + Section 11.1.3 sim-path
// safety row.
//
// Phase 1g landing:
//   * Format       -- inline template in FString.h; uses std::vformat.
//                     Locale-defaulted (NOT sim-path-safe).
//   * FormatFixed  -- inline template in FString.h; routes through
//                     FormatFixedRuntime defined here. C-locale fixed
//                     (sim-path-safe).
//
// Both rely on the modern C++20 std::format surface; the toolchain
// matrix is pinned in HAL/FormatString.h. If the toolchain is older
// than the supported minimum, FormatString.h emits a #error at
// include time.
//
// FormatFixedRuntime is non-template (the std::format_args type is
// opaque), defined here, and marked noexcept. The std::vformat call
// inside CAN throw std::format_error on internal-state issues that the
// compile-time-checked format string should preclude; we wrap with a
// try/catch and route any escape to a clean abort. The Prime Directive
// "no silent corruption" means a thrown std::format_error in a sim-path
// TU IS a build/test failure surface, not "format to empty string".
//
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"            // FMemory abort path

#include <cstdio>                   // std::fprintf (for diagnostic)
#include <cstdlib>                  // std::abort
#include <exception>                // std::exception
#include <format>                   // std::vformat / std::format_args
#include <string>                   // std::string

namespace XCore
{

// =====================================================================
// FString::FormatFixedRuntime -- non-template helper for FormatFixed.
//
// The std::format_args type is opaque (it holds a reference into the
// caller's argument pack), so the helper body lives outside the
// template instantiation and is non-template-able.
//
// Noexcept contract: std::vformat can throw std::format_error if the
// format string is invalid AT RUNTIME. The compile-time-checked
// std::format_string template parameter on the caller side prevents
// this in practice; defense-in-depth here funnels any escape to
// stderr + abort. The Prime Directive forbids silent format failures.
// =====================================================================

FString FString::FormatFixedRuntime(
    ::std::string_view Fmt,
    ::std::format_args Args) noexcept
{
    // The vformat call uses the default C locale by construction
    // (std::vformat does NOT consult the locale unless a {:L} specifier
    // appears; sim-path TUs forbid {:L} via the lint layer in §11.1.3).
    // The {:f}, {:e}, {:g} specifiers are locale-INDEPENDENT per the
    // standard (P0067R5 + N4885 §28.5.2.4), so the same format string
    // produces byte-identical output on every supported platform.
    try
    {
        ::std::string Out = ::std::vformat(Fmt, Args);
        return FString(Out.data(), static_cast<::int32>(Out.size()));
    }
    catch (const ::std::exception& E)
    {
        // std::format_error or any other exception. The Prime Directive
        // ("no silent corruption") routes us to a hard abort rather
        // than returning an empty string that would silently propagate
        // through downstream computation.
        ::std::fprintf(stderr,
            "XCore::FString::FormatFixed: std::vformat threw '%s' on format string "
            "(this should not be reachable with a compile-time-checked "
            "FormatString<Args...> parameter; please file a bug).\n",
            E.what());
        ::std::abort();
    }
    catch (...)
    {
        ::std::fprintf(stderr,
            "XCore::FString::FormatFixed: std::vformat threw a non-std::exception "
            "value (this should not be reachable).\n");
        ::std::abort();
    }
}

} // namespace XCore
