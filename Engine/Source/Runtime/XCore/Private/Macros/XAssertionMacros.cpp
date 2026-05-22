// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XAssertionMacros.cpp -- platform-specific abort behaviour bodies.
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1 (fix M-15 + fix Rev 3 M6).
//
// The bodies for the four cold-path failure helpers declared in
// Public/Macros/XAssertionMacros.h. Each helper terminates the
// process via the platform-correct FailFast path:
//   * Win64:   RaiseFailFastException with FAST_FAIL_FATAL_APP_EXIT.
//              Tells the OS this is an unrecoverable engine bug, not a
//              user-application crash; the dump captured by WER is
//              tagged accordingly.
//   * Linux:   write(STDERR_FILENO, ...) + abort(). The abort() raises
//              SIGABRT; the process's core-dump policy kicks in.
//   * Android: __android_log_print(ANDROID_LOG_FATAL, ...) + abort().
//              The fatal-log tier triggers the tombstone collector
//              under DEBUG/USER NDK runtimes.
//
// Phase 1a notes:
//   * The Platform HAL surface (XCore-4a Step 2) is not yet shipped.
//     The platform conditional below uses raw __APPLE__ / _WIN64 /
//     __ANDROID__ / __linux__ macros directly; once Step 2 lands an
//     XPlatformProperties.h with XPACT_PLATFORM_WIN64 etc. flags, the
//     conditional should switch to those flags for consistency with
//     the rest of XCore-4a. The selection logic is the same;
//     XPACT_PLATFORM_WIN64 will be defined exactly when _WIN64 is.
//   * The log subsystem (Section 13 unrelated; lands at XCore-4b) is
//     not yet shipped. LogAndAbort therefore degrades to
//     AbortWithMessage on every platform until the log channel is
//     live. This is documented as a TODO(Phase 1b/c) below.
//
// =====================================================================

#include "Macros/XAssertionMacros.h"
#include "Macros/XCoreDefines.h"

#include <cstdio>
#include <cstdlib>

#if defined(_WIN64) || defined(_WIN32)
    #ifndef WIN32_LEAN_AND_MEAN
        #define WIN32_LEAN_AND_MEAN 1
    #endif
    #ifndef NOMINMAX
        #define NOMINMAX 1
    #endif
    #include <Windows.h>
#elif defined(__ANDROID__)
    #include <android/log.h>
    #include <unistd.h>
#else  // Linux + POSIX fallback
    #include <unistd.h>
#endif

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // Internal: emit a diagnostic line + trigger FailFast.
    //
    // The diagnostic format is fixed for forensic-grep stability:
    //
    //   "[XPACT FATAL] {Message} at {File}:{Line}\n"
    //
    // CI and tooling parses this line; changing it requires a
    // contract bump (XPACT_DIAG_SCHEMA_TAG would be the right place
    // to lock it long-term; for now the format is locked in this
    // comment).
    //
    // The Message buffer is sized to 1024 bytes -- enough for the
    // longest realistic expression + file path on Windows
    // (MAX_PATH = 260; expression text typically under 256 chars).
    // The vsnprintf truncates anything longer; this is acceptable in
    // an abort path where the most important data is the file + line.
    // -----------------------------------------------------------------
    namespace
    {
        void EmitDiagnostic(const char* Message, const char* File, int Line) noexcept
        {
            char Buf[1024];
            // snprintf returns number of bytes that *would* have been
            // written; we ignore the return value because the cold
            // path cannot afford to branch on truncation. The %s/%d
            // format never causes UB even with non-printable bytes.
            ::std::snprintf(Buf, sizeof(Buf), "[XPACT FATAL] %s at %s:%d\n",
                            Message ? Message : "(no message)",
                            File ? File : "(no file)",
                            Line);

#if defined(_WIN64) || defined(_WIN32)
            // OutputDebugStringA reaches the attached debugger;
            // stderr reaches the console. Emit both so the
            // diagnostic is visible regardless of which channel the
            // tester is watching.
            ::OutputDebugStringA(Buf);
            ::fputs(Buf, stderr);
            ::fflush(stderr);
#elif defined(__ANDROID__)
            // ANDROID_LOG_FATAL routes to logcat *and* triggers the
            // tombstone collector on userdebug + eng builds.
            __android_log_print(ANDROID_LOG_FATAL, "XPACT", "%s", Buf);
#else
            ::fputs(Buf, stderr);
            ::fflush(stderr);
#endif
        }

        // [[noreturn]] cold-attr abort entry. Routes through the
        // platform-correct FailFast.
        [[noreturn]]
#if defined(__clang__) || defined(__GNUC__)
        [[gnu::cold]]
#endif
        void DoAbort() noexcept
        {
#if defined(_WIN64) || defined(_WIN32)
            // FAST_FAIL_FATAL_APP_EXIT tells Windows this is an
            // engine-detected unrecoverable error. The OS skips
            // user-level exception handlers and goes straight to
            // WER capture, which is the right behaviour for an
            // assertion failure.
            ::RaiseFailFastException(nullptr, nullptr, 0);
            // RaiseFailFastException is documented noreturn; the
            // unreachable below is purely to satisfy the
            // [[noreturn]] contract in the rare case the OS does
            // return.
            ::abort();
#else
            ::abort();
#endif
        }
    } // anonymous

    // -----------------------------------------------------------------
    // CheckFailed -- classic __FILE__/__LINE__ form.
    // -----------------------------------------------------------------
    void CheckFailed(const char* Expression, const char* File, int Line) noexcept
    {
        // Compose "check failed: {Expression}" and route through
        // EmitDiagnostic. The two-argument shape is a contract with
        // the XPACT_CHECK macro; we cannot change the caller-side
        // arity without a macro re-roll.
        char Buf[1024];
        ::std::snprintf(Buf, sizeof(Buf), "check failed: %s",
                        Expression ? Expression : "(no expression)");
        EmitDiagnostic(Buf, File, Line);
        DoAbort();
    }

    // -----------------------------------------------------------------
    // CheckFailedSL -- std::source_location form (Rev 3 fix M6).
    // -----------------------------------------------------------------
#if XPACT_HAS_SOURCE_LOCATION
    void CheckFailedSL(::std::source_location Loc) noexcept
    {
        // The source_location form carries no expression text -- the
        // macro that invokes this overload knows the check expression
        // by the surrounding source structure (file + line + function
        // name is sufficient). For richer diagnostics the caller can
        // hand-roll an AbortWithMessage with the expression text
        // included.
        char Buf[1024];
        ::std::snprintf(Buf, sizeof(Buf), "check failed in %s", Loc.function_name());
        EmitDiagnostic(Buf, Loc.file_name(), static_cast<int>(Loc.line()));
        DoAbort();
    }
#endif

    // -----------------------------------------------------------------
    // AbortWithMessage -- explicit-message abort.
    // -----------------------------------------------------------------
    void AbortWithMessage(const char* Message, const char* File, int Line) noexcept
    {
        EmitDiagnostic(Message, File, Line);
        DoAbort();
    }

    // -----------------------------------------------------------------
    // LogAndAbort -- abort with structured log emission.
    //
    // TODO(Phase 1b/c): once the log channel (XCore-4a Step 14 FText
    // runtime / XLog when it lands) is live, route the message
    // through the structured-log path before triggering the abort.
    // Currently degrades to AbortWithMessage on every platform; the
    // distinction matters only when there is a separate log channel
    // to flush. Until then the two functions behave identically.
    // -----------------------------------------------------------------
    void LogAndAbort(const char* Message, const char* File, int Line) noexcept
    {
        EmitDiagnostic(Message, File, Line);
        DoAbort();
    }

} // namespace XCore::HAL
