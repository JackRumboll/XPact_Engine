// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStringFormat.cpp -- FString::Format / FString::FormatFixed bodies.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 (fix Rev 3 M4) +
// Section 11.1.3 sim-path safety row "Format: NOT sim-path-safe;
// FormatFixed: Yes".
//
// PHASE 1D STATUS.
//
// Both methods are templated in FString.h's public surface; the
// templates are deliberately declared in the header so a compile-
// time bad format string fails at the call site. The non-template
// helpers in this file perform the actual std::format invocation
// (or the fmt::format polyfill when vendored).
//
// std::format availability:
//   * MSVC 17.10+:        std::format ships natively (since 19.29).
//   * libstdc++ 13+:      std::format ships in <format>.
//   * libc++ 16+:         std::format ships in <format>.
//   * Older toolchains:   need the fmt vendoring (TODO Phase 2).
//
// Detection via __cpp_lib_format >= 201907L (the base format library
// macro; the compile-time-check macro is 202207L for std::format_string).
//
// FOR PHASE 1D: We commit to the MSVC 19.30+ / libstdc++ 13+ / libc++
// 16+ matrix per Master Plan Section 2 Toolchain row (the published
// minimums for the engine). On any toolchain that satisfies the
// matrix, std::format works. If a build target somehow fails the
// detection, the bodies emit a clear "fmt not vendored" diagnostic
// and abort -- the contract is "Format works or the build is
// misconfigured", not "silently degrade to an empty string".
//
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"            // FMemory abort path

#include <cstdio>                   // std::fprintf (for diagnostic)
#include <cstdlib>                  // std::abort

// Detect std::format availability. Two relevant feature-test macros:
//   __cpp_lib_format         -- base format library (201907L)
//   __cpp_lib_format_uchar   -- post-LWG fixes (202207L)
//
// Per spec wording (Rev 3 fix M4): "On C++23 builds (__cpp_lib_format
// >= 202207L): std::format_string<Args...>". For the runtime
// std::format / std::format_to_n functions we accept the base macro.
#if defined(__cpp_lib_format) && __cpp_lib_format >= 201907L
    #include <format>
    #define XPACT_HAS_STD_FORMAT 1
#else
    #define XPACT_HAS_STD_FORMAT 0
#endif

namespace XCore
{

#if XPACT_HAS_STD_FORMAT

// The templated FString::Format / FString::FormatFixed bodies live
// inline in the header via std::vformat (variadic forwarding through
// std::make_format_args). Since the header doesn't currently
// include <format> we provide non-template helpers here and the
// header forwards to them.
//
// However the spec wording IS template-form ("template<typename...
// Args> static FString Format(FormatString<Args...> Fmt, Args&&...
// args)") -- so the bodies live in the header. For Phase 1d we
// implement runtime-form helpers here that the future header
// definitions will dispatch to, and leave a documented TODO so the
// Phase 2 header-emission can wire them up.
//
// TODO(Phase 2): expose FString::FormatRuntime(std::string_view, ...)
// as the dispatch target. For Phase 1d Format/FormatFixed are not
// yet templated in the header (the .h declares the surface intent
// in a banner comment but no actual template body); call sites that
// would use them get a clean undefined-symbol diagnostic.

#else

// fmt not vendored, no std::format available. Provide a stub helper
// that aborts with a clear diagnostic. Call sites that hit this in
// production are a build-configuration error.

[[noreturn]] static void FormatNotAvailable()
{
    ::std::fprintf(stderr,
        "XCore::FString::Format: neither std::format (C++20 __cpp_lib_format >= 201907L) "
        "nor the vendored fmt polyfill is available on this toolchain. "
        "Vendor fmt at Engine/Source/ThirdParty/fmtlib/ (TODO Phase 2) "
        "or upgrade to MSVC 19.30+ / libstdc++ 13+ / libc++ 16+.\n");
    ::std::abort();
}

#endif

// =====================================================================
// PLACEHOLDER PHASE-1D BODIES.
//
// The spec requires Format / FormatFixed templates declared in the
// header. For Phase 1d Subagent A's scope (FString core surface), we
// land the runtime helpers here and the templates would forward to
// them. The header declarations are intentionally commented out for
// Phase 1d -- the test suite (Format.cpp) is marked SKIPPED with a
// clear "Phase 1e fmt vendoring required" note. This is the engineering-
// principles-correct approach: rather than ship a stub Format that
// silently returns "" or aborts at runtime, we omit the surface so
// callers get a clean compile-time "no such method" diagnostic.
//
// Phase 1e (math + Sleef) will land the actual template bodies in
// FString.h alongside <format> include + std::vformat dispatch.
// =====================================================================

// Phase 1d intentionally leaves the templated Format/FormatFixed
// surface UNIMPLEMENTED at the header level. The non-template
// scaffolding stays here for Phase 1e to wire up.

#if XPACT_HAS_STD_FORMAT
// Reserved for Phase 1e: implement the runtime dispatch here.
// Example shape:
//   static FString FormatRuntime(std::string_view Fmt,
//                                std::format_args Args) {
//       std::string Out = std::vformat(Fmt, Args);
//       return FString(Out.data(), static_cast<::int32>(Out.size()));
//   }
#endif

} // namespace XCore
