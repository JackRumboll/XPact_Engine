// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// IStackWalk.h -- internal platform stack-walker interface.
// =====================================================================
//
// XCore-4a Rev 3, Section 12.5.
//
// Per-platform implementations live in sibling .cpp files:
//   * Win64StackWalk.cpp     -- RtlCaptureStackBackTrace + dbghelp
//                               SymFromAddr.
//   * UnixStackWalk.cpp      -- backtrace + backtrace_symbols.
//   * AndroidStackWalk.cpp   -- __builtin_frame_address +
//                               _Unwind_Backtrace; Phase 2 NDK
//                               libunwindstack richer info.
//
// Selection: per-platform compile gates in each .cpp file via
// XPACT_PLATFORM_WIN64 / XPACT_PLATFORM_LINUX / XPACT_PLATFORM_ANDROID
// (defined in XPactMacros.h section 7). Exactly one
// implementation .cpp builds per target.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#if XPACT_LEAK_TRACKING_ENABLED

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // kMaxStackFrames -- the platform-stack-walk depth.
    //
    // Matches FLeakTracker::FStackBucket::Frames[24] (Section 12.1 +
    // Section 12.2: "Per-platform stack-walk depth is fixed at 24
    // frames across Win64 / Linux / Android").
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kMaxStackFrames = 24;

    // -----------------------------------------------------------------
    // FSymbolizedFrame -- the result of symbolizing a single frame.
    //
    // The buffers are inline-sized so symbolic resolution does not
    // allocate. The implementation either writes into Module / Symbol
    // / SourceFile / Line, or clears them to indicate no information
    // is available for the frame.
    // -----------------------------------------------------------------
    struct FSymbolizedFrame
    {
        char     Module[64];      // DLL / SO short name
        char     Symbol[256];     // demangled function name (or mangled fallback)
        char     SourceFile[256]; // file path (relative if PDB available)
        ::uint32 Line;            // source line (0 if not available)
        void*    Address;         // raw return address
    };

    // -----------------------------------------------------------------
    // CaptureStackWalk -- fill OutFrames with the calling thread's
    // stack frames, skipping `FramesToSkip` from the top.
    //
    // Returns the number of frames captured (0..kMaxStackFrames).
    // The returned frames are raw return addresses; symbolic
    // resolution is a separate pass via SymbolizeFrame.
    //
    // The function is noexcept and must not allocate. Implementations
    // use stack-only buffers + platform syscalls.
    //
    // Phase 1b note: implementations are platform-specific .cpp
    // files; per-platform symbol detail varies (Win64 has full PDB-
    // backed symbol resolution; Linux backtrace_symbols may need
    // -rdynamic; Android baseline gives module + offset but no
    // source line until libunwindstack lands).
    // -----------------------------------------------------------------
    ::SIZE_T CaptureStackWalk(void* OutFrames[kMaxStackFrames], ::SIZE_T FramesToSkip) noexcept;

    // -----------------------------------------------------------------
    // SymbolizeFrame -- resolve a single return address to symbol /
    // file / line.
    //
    // Best-effort: returns true if any field was populated; false if
    // no information is available (the address is in an un-mapped
    // module; the platform doesn't ship a symbolizer in this binary).
    //
    // Thread-safe (Win64's dbghelp is single-threaded; the
    // implementation guards with a per-process mutex internally).
    // -----------------------------------------------------------------
    bool SymbolizeFrame(void* Address, FSymbolizedFrame& Out) noexcept;

    // -----------------------------------------------------------------
    // InitStackWalk -- one-time per-process initialisation.
    //
    // Win64: SymInitialize via dbghelp.
    // Linux: backtrace requires no init.
    // Android: backtrace requires no init.
    //
    // Called from FLeakTracker::__Init at PreStaticInit.
    // -----------------------------------------------------------------
    void InitStackWalk() noexcept;

    // -----------------------------------------------------------------
    // ShutdownStackWalk -- one-time per-process teardown.
    //
    // Win64: SymCleanup.
    // Linux: no-op.
    // Android: no-op.
    //
    // Called from FLeakTracker::__Shutdown.
    // -----------------------------------------------------------------
    void ShutdownStackWalk() noexcept;

} // namespace XCore::HAL

#endif  // XPACT_LEAK_TRACKING_ENABLED
