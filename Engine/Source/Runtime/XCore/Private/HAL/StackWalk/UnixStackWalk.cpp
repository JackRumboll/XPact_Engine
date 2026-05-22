// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// UnixStackWalk.cpp -- Linux stack walk via backtrace(3).
// =====================================================================
//
// XCore-4a Rev 3, Section 12.5.
//
//   * Capture:   backtrace(3) from <execinfo.h>.
//   * Symbolize: backtrace_symbols (returns mangled name + offset);
//                Phase 2 libunwind richer-info noted as TODO.
//
// Requires -rdynamic linker flag to resolve symbols beyond dynamic
// export table (statically-linked symbols are otherwise invisible
// to backtrace_symbols). The XCore.Build.toml wires `-rdynamic` on
// Linux builds.
//
// Builds only when XPACT_PLATFORM_LINUX is 1.
//
// =====================================================================

#include "Private/HAL/StackWalk/IStackWalk.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#if XPACT_LEAK_TRACKING_ENABLED && XPACT_PLATFORM_LINUX

#include <execinfo.h>
#include <cstdlib>   // ::free
#include <cstring>   // ::strncpy / ::memset
#include <cstdio>    // ::snprintf

namespace XCore::HAL
{
    void InitStackWalk() noexcept
    {
        // backtrace(3) requires no initialisation.
    }

    void ShutdownStackWalk() noexcept
    {
        // No teardown needed.
    }

    ::SIZE_T CaptureStackWalk(void* OutFrames[kMaxStackFrames], ::SIZE_T FramesToSkip) noexcept
    {
        // backtrace(3) doesn't take a skip parameter; we capture
        // kMaxStackFrames + FramesToSkip and shift down. Buffer is
        // sized for the maximum reasonable skip (4) plus kMaxStackFrames.
        void* RawFrames[kMaxStackFrames + 8];
        const int Captured = ::backtrace(RawFrames, kMaxStackFrames + 8);

        if (Captured <= static_cast<int>(FramesToSkip))
        {
            return 0;
        }

        const int Effective = Captured - static_cast<int>(FramesToSkip);
        const int ToCopy    = (Effective > static_cast<int>(kMaxStackFrames))
                                  ? static_cast<int>(kMaxStackFrames)
                                  : Effective;

        for (int I = 0; I < ToCopy; ++I)
        {
            OutFrames[I] = RawFrames[FramesToSkip + I];
        }

        return static_cast<::SIZE_T>(ToCopy);
    }

    bool SymbolizeFrame(void* Address, FSymbolizedFrame& Out) noexcept
    {
        ::std::memset(&Out, 0, sizeof(Out));
        Out.Address = Address;

        // backtrace_symbols handles one address; it allocates a single
        // string via malloc (we ::free it before returning). The string
        // format is "module(symbol+0xoffset) [0xaddr]".
        void* Addrs[1] = { Address };
        char** Symbols = ::backtrace_symbols(Addrs, 1);
        if (Symbols == nullptr || Symbols[0] == nullptr)
        {
            if (Symbols)
            {
                ::free(Symbols);
            }
            return false;
        }

        // We do not parse out module / symbol separately; copy the
        // full backtrace_symbols string into Out.Symbol for Phase 1b.
        // Phase 1c will add libunwind-based richer parsing.
        ::std::strncpy(Out.Symbol, Symbols[0], sizeof(Out.Symbol) - 1);
        Out.Symbol[sizeof(Out.Symbol) - 1] = '\0';

        ::free(Symbols);
        return true;
    }

} // namespace XCore::HAL

#endif  // XPACT_LEAK_TRACKING_ENABLED && XPACT_PLATFORM_LINUX

// =====================================================================
// TODO(Phase 1c):
//   * libunwind-based richer parsing (separate module / symbol /
//     offset / file / line). Section 12.5 spec body mentions libunwind
//     as the higher-fidelity option for Linux; backtrace_symbols is
//     the baseline for Phase 1b.
// =====================================================================
