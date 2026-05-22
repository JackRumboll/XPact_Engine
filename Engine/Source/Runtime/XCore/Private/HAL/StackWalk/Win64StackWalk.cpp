// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64StackWalk.cpp -- Win64 stack walk via dbghelp.
// =====================================================================
//
// XCore-4a Rev 3, Section 12.5.
//
//   * Capture:  RtlCaptureStackBackTrace (kernel-mode efficient;
//                 no manual unwind code path; ~50 ns per call for
//                 24 frames).
//   * Symbolize: dbghelp.dll SymFromAddr + SymGetLineFromAddr64.
//                 PDB-backed; loads on demand via SymInitialize.
//
// dbghelp is single-threaded by design (a Win32 documentation lock);
// all calls are serialised through a per-process mutex.
//
// Builds only when XPACT_PLATFORM_WIN64 is 1. The .cpp's body is
// wrapped in an #if; on other platforms the TU compiles to an
// empty body so the build system can include it unconditionally.
//
// =====================================================================

#include "Private/HAL/StackWalk/IStackWalk.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"
#include "HAL/FMutex.h"        // Phase 1g fix M-8: FMutex replaces std::mutex

#if XPACT_LEAK_TRACKING_ENABLED && XPACT_PLATFORM_WIN64

#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN 1
#endif
#ifndef NOMINMAX
    #define NOMINMAX 1
#endif
#include <Windows.h>
#include <DbgHelp.h>
#pragma comment(lib, "dbghelp.lib")

#include <atomic>
#include <cstring>

namespace XCore::HAL
{
    namespace
    {
        // dbghelp is single-threaded; serialise all access.
        //
        // Phase 1g fix M-8: FMutex (XCore HAL primitive) replaces
        // std::mutex. Function-local Meyers singleton; not constinit-
        // required, so FMutex's non-constexpr ctor is fine here.
        ::XCore::HAL::FMutex& GetDbgHelpMutex() noexcept
        {
            static ::XCore::HAL::FMutex Mutex;
            return Mutex;
        }

        ::std::atomic<bool> g_symInitialized{ false };
    } // anonymous

    // -----------------------------------------------------------------
    // InitStackWalk -- one-time SymInitialize.
    //
    // Lazy: called from FLeakTracker::__Init, but also defensively
    // re-checked at first symbol-resolution call so a Phase 1b build
    // that does not yet wire __Init at PreStaticInit still produces
    // resolved symbols.
    // -----------------------------------------------------------------
    void InitStackWalk() noexcept
    {
        if (g_symInitialized.exchange(true, ::std::memory_order_acq_rel))
        {
            return;  // already initialised
        }

        ::XCore::HAL::FScopedMutexLock Lock(GetDbgHelpMutex());

        // SYMOPT_LOAD_LINES: load source-line info from PDB.
        // SYMOPT_DEFERRED_LOADS: only load PDB metadata on demand.
        ::SymSetOptions(::SymGetOptions() | SYMOPT_LOAD_LINES | SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME);

        // fInvadeProcess = TRUE: load symbols for every currently-loaded
        // module. Slow at init but cheap at every subsequent
        // SymFromAddr call.
        ::SymInitialize(::GetCurrentProcess(), nullptr, TRUE);
    }

    // -----------------------------------------------------------------
    // ShutdownStackWalk -- SymCleanup.
    // -----------------------------------------------------------------
    void ShutdownStackWalk() noexcept
    {
        if (!g_symInitialized.exchange(false, ::std::memory_order_acq_rel))
        {
            return;
        }

        ::XCore::HAL::FScopedMutexLock Lock(GetDbgHelpMutex());
        ::SymCleanup(::GetCurrentProcess());
    }

    // -----------------------------------------------------------------
    // CaptureStackWalk -- RtlCaptureStackBackTrace.
    //
    // The function is no-alloc; the frame buffer is the caller's
    // OutFrames array. FramesToSkip skips kFrames-to-skip top frames
    // (typically 2: this function + the calling Hook in
    // FLeakTracker.cpp).
    //
    // Capture limit on Win10+: 200 frames per call. Our kMaxStackFrames
    // = 24 is well within.
    // -----------------------------------------------------------------
    ::SIZE_T CaptureStackWalk(void* OutFrames[kMaxStackFrames], ::SIZE_T FramesToSkip) noexcept
    {
        const ULONG Skip    = static_cast<ULONG>(FramesToSkip);
        const ULONG Capture = static_cast<ULONG>(kMaxStackFrames);
        const USHORT Result = ::RtlCaptureStackBackTrace(Skip, Capture, OutFrames, nullptr);
        return static_cast<::SIZE_T>(Result);
    }

    // -----------------------------------------------------------------
    // SymbolizeFrame -- SymFromAddr + SymGetLineFromAddr64.
    //
    // The SYMBOL_INFO buffer is stack-allocated (the trailing Name
    // array sized to 256 bytes for the demangled symbol).
    // -----------------------------------------------------------------
    bool SymbolizeFrame(void* Address, FSymbolizedFrame& Out) noexcept
    {
        ::std::memset(&Out, 0, sizeof(Out));
        Out.Address = Address;

        if (!g_symInitialized.load(::std::memory_order_acquire))
        {
            InitStackWalk();  // lazy
        }

        ::XCore::HAL::FScopedMutexLock Lock(GetDbgHelpMutex());

        // SYMBOL_INFO with trailing Name buffer.
        struct alignas(8) FSymBuf
        {
            SYMBOL_INFO Info;
            char Name[256];
        } SymBuf{};

        SymBuf.Info.SizeOfStruct = sizeof(SYMBOL_INFO);
        SymBuf.Info.MaxNameLen   = sizeof(SymBuf.Name);

        const HANDLE Proc        = ::GetCurrentProcess();
        const DWORD64 AddrDword  = reinterpret_cast<DWORD64>(Address);
        DWORD64 Displacement     = 0;

        bool SymOK = false;
        if (::SymFromAddr(Proc, AddrDword, &Displacement, &SymBuf.Info))
        {
            ::strncpy_s(Out.Symbol, sizeof(Out.Symbol), SymBuf.Info.Name, _TRUNCATE);
            SymOK = true;
        }

        IMAGEHLP_LINE64 LineInfo{};
        LineInfo.SizeOfStruct = sizeof(IMAGEHLP_LINE64);
        DWORD LineDisp        = 0;
        if (::SymGetLineFromAddr64(Proc, AddrDword, &LineDisp, &LineInfo))
        {
            ::strncpy_s(Out.SourceFile, sizeof(Out.SourceFile), LineInfo.FileName, _TRUNCATE);
            Out.Line = LineInfo.LineNumber;
            SymOK = true;
        }

        IMAGEHLP_MODULE64 ModInfo{};
        ModInfo.SizeOfStruct = sizeof(IMAGEHLP_MODULE64);
        if (::SymGetModuleInfo64(Proc, AddrDword, &ModInfo))
        {
            ::strncpy_s(Out.Module, sizeof(Out.Module), ModInfo.ModuleName, _TRUNCATE);
            SymOK = true;
        }

        return SymOK;
    }

} // namespace XCore::HAL

#endif  // XPACT_LEAK_TRACKING_ENABLED && XPACT_PLATFORM_WIN64
