// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XAssertionMacros.h -- cold-path failure helper declarations.
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1 (fix M-15 + fix Rev 3 M6).
//
// The XPACT_CHECK macro in XPactMacros.h is laid out so the hot path
// is one UNLIKELY-branch + one cold-call to one of these helpers. The
// helpers themselves are `[[noreturn]] [[gnu::cold]]` so the optimiser
// reorders them out of the i-cache hot region and emits the calling
// convention with the smallest possible inline cost on the calling
// side.
//
// The bodies live in Private/Macros/XAssertionMacros.cpp where the
// platform-specific abort behaviour is dispatched:
//   * Win64:   RaiseFailFastException (FAST_FAIL_FATAL_APP_EXIT) with
//              a structured log payload identifying the failing
//              expression + source location.
//   * Linux:   abort() after writing a SIGABRT-cooked message to
//              stderr.
//   * Android: __android_log_print + abort(); the NDK log channel
//              carries the diagnostic to logcat.
//
// All four helpers are noreturn -- the compiler treats their call site
// as an unreachable tail jump and stops generating fall-through code
// after them. This is essential for the XPACT_CHECK macro: the
// optimiser must understand that the CheckFailed branch never
// re-enters the program so the macro itself can be used safely from
// within `noexcept` functions.
//
// `[[gnu::cold]]` is a Clang/GCC attribute; MSVC does not have a
// direct equivalent in C++20 (the closest is __declspec(noinline) but
// that hints at codegen, not at hotness). The XPACT_FORCEINLINE
// macro in XPactMacros.h treats MSVC as the special case (no [[gnu::*]]
// attributes); here we use the same pattern -- the attribute is a hint,
// not a contract, and the lack of it on MSVC costs at most a few
// bytes of inlined call sequence at each check site.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

// std::source_location requires <source_location> header (C++20).
// Some toolchains gate the header behind __cpp_lib_source_location;
// MSVC 19.29+, libstdc++ 11+, libc++ 16+ all ship the header by
// default in C++20 mode. The XCore-4a build is C++20 (Section 2 of
// the spec) so the header is available; sentry the include defensively
// for the rare ad-hoc compile against an older standard library.
#if __has_include(<source_location>)
    #include <source_location>
    #define XPACT_HAS_SOURCE_LOCATION 1
#else
    #define XPACT_HAS_SOURCE_LOCATION 0
#endif

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // CheckFailed -- classic __FILE__/__LINE__ overload.
    //
    // Called from the XPACT_CHECK macro in Debug + Development. The
    // arguments are pointer-to-string literals that the compiler
    // emits to .rdata; on Shipping the macro compiles out and these
    // strings are dead code that the linker GCs.
    //
    // The function never returns; downstream behaviour is the
    // platform-specific FailFast / abort path. The cold attribute
    // signals to the optimiser that this is a one-shot terminal
    // branch.
    // -----------------------------------------------------------------
    [[noreturn]]
#if defined(__clang__) || defined(__GNUC__)
    [[gnu::cold]]
#endif
    void CheckFailed(const char* Expression, const char* File, int Line) noexcept;

    // -----------------------------------------------------------------
    // CheckFailedSL -- std::source_location overload (Rev 3 fix M6).
    //
    // The C++20 std::source_location carries file_name(), line(),
    // function_name() inside a single ~32-byte struct passed by
    // value. The advantage over the classic __FILE__/__LINE__ form is
    // that the linker can dedupe the per-site source-string emission
    // (one copy per TU rather than one copy per check site), and
    // function_name() is captured for free.
    //
    // Critical for hot-reload: the patched DLL revision otherwise
    // re-emits every __FILE__ literal for every check site in every
    // TU, which the linker cannot dedupe across the patch boundary.
    // The source_location form lets the linker dedupe the strings
    // once, sharing them between the original DLL and the patched
    // revision.
    //
    // The default argument captures the call site at *the macro's
    // expansion point*; that is the point the diagnostic should
    // identify. Calling the function with no arguments from a
    // non-macro site (rare; reserved for hand-rolled check sites)
    // captures the calling function's source location naturally.
    // -----------------------------------------------------------------
#if XPACT_HAS_SOURCE_LOCATION
    [[noreturn]]
#if defined(__clang__) || defined(__GNUC__)
    [[gnu::cold]]
#endif
    void CheckFailedSL(::std::source_location Loc = ::std::source_location::current()) noexcept;
#endif

    // -----------------------------------------------------------------
    // AbortWithMessage -- explicit-message abort.
    //
    // Used for hand-rolled abort paths that need to carry a message
    // distinct from the failing-expression text (e.g., "OOM in
    // FMemory::MallocOrAbort at tag X, requested Y bytes").
    //
    // Callers that already have an expression-text macro (XPACT_CHECK)
    // should prefer that path. This is the lower-level primitive that
    // CheckFailed itself routes through on platforms where the
    // expression-text and the abort-message are separable.
    // -----------------------------------------------------------------
    [[noreturn]]
#if defined(__clang__) || defined(__GNUC__)
    [[gnu::cold]]
#endif
    void AbortWithMessage(const char* Message, const char* File, int Line) noexcept;

    // -----------------------------------------------------------------
    // LogAndAbort -- abort with structured log emission.
    //
    // Difference from AbortWithMessage: this variant guarantees the
    // message reaches the engine log channel (and on Android, logcat)
    // before the abort fires. AbortWithMessage may be invoked in
    // contexts where the log subsystem is not yet up (e.g.,
    // PreStaticInit failure); LogAndAbort assumes the log is live.
    //
    // On platforms where the log is not separable from stderr (Linux,
    // Android in early-boot), LogAndAbort degrades to AbortWithMessage.
    // -----------------------------------------------------------------
    [[noreturn]]
#if defined(__clang__) || defined(__GNUC__)
    [[gnu::cold]]
#endif
    void LogAndAbort(const char* Message, const char* File, int Line) noexcept;

} // namespace XCore::HAL
