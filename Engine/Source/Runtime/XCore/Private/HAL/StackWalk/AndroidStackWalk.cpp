// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidStackWalk.cpp -- Android stack walk via _Unwind_Backtrace.
// =====================================================================
//
// XCore-4a Rev 3, Section 12.5 fix A-MIN2.
//
// Step 5 baseline ships the frame-pointer-walking implementation via
// __builtin_frame_address + _Unwind_Backtrace (NDK-standard, available
// in libunwind which is part of the NDK shipped libc++).
//
// NDK libunwindstack richer information (DWARF unwind, symbol-mapped
// JIT frames, ART runtime frame translation) is the Step 5 FOLLOW-UP
// commit per Rev 3 fix m5 + Section 12.5. This file ships the baseline
// only; the follow-up file (AndroidStackWalk_libunwindstack.cpp) will
// be added in the Step-5-follow-up commit and gated by a compile flag.
//
// Acceptance criterion I-extra (Section 17.9 / fix A-MIN2): Android
// leak-tracker symbol resolution within 95% of Win64 dbghelp accuracy
// on a 1000-stack-trace sample. The Phase 1b baseline does NOT meet
// this; the libunwindstack follow-up does.
//
// Builds only when XPACT_PLATFORM_ANDROID is 1.
//
// =====================================================================

#include "Private/HAL/StackWalk/IStackWalk.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#if XPACT_LEAK_TRACKING_ENABLED && XPACT_PLATFORM_ANDROID

#include <unwind.h>
#include <dlfcn.h>      // dladdr for module name + symbol offset
#include <cstring>      // ::strncpy / ::memset

namespace XCore::HAL
{
    namespace
    {
        // -----------------------------------------------------------------
        // Unwind callback state.
        //
        // _Unwind_Backtrace calls back once per frame; the callback
        // stores the frame's IP into our outputs.
        // -----------------------------------------------------------------
        struct FUnwindState
        {
            void**   OutFrames;
            ::SIZE_T Count;
            ::SIZE_T Max;
            ::SIZE_T Skip;
        };

        _Unwind_Reason_Code UnwindCallback(struct _Unwind_Context* Ctx, void* UserState) noexcept
        {
            FUnwindState* State = static_cast<FUnwindState*>(UserState);
            const ::uintptr_t Ip = _Unwind_GetIP(Ctx);
            if (Ip == 0)
            {
                return _URC_END_OF_STACK;
            }

            if (State->Skip > 0)
            {
                --State->Skip;
                return _URC_NO_REASON;
            }

            if (State->Count >= State->Max)
            {
                return _URC_END_OF_STACK;
            }

            State->OutFrames[State->Count++] = reinterpret_cast<void*>(Ip);
            return _URC_NO_REASON;
        }
    } // anonymous

    void InitStackWalk() noexcept
    {
        // _Unwind_Backtrace requires no initialisation.
    }

    void ShutdownStackWalk() noexcept
    {
        // No teardown needed.
    }

    ::SIZE_T CaptureStackWalk(void* OutFrames[kMaxStackFrames], ::SIZE_T FramesToSkip) noexcept
    {
        FUnwindState State{};
        State.OutFrames = OutFrames;
        State.Count     = 0;
        State.Max       = kMaxStackFrames;
        State.Skip      = FramesToSkip;

        ::_Unwind_Backtrace(&UnwindCallback, &State);
        return State.Count;
    }

    bool SymbolizeFrame(void* Address, FSymbolizedFrame& Out) noexcept
    {
        ::std::memset(&Out, 0, sizeof(Out));
        Out.Address = Address;

        // dladdr -- best-effort module + symbol resolution. Returns
        // dli_fname (module path), dli_fbase (module base), dli_sname
        // (nearest symbol name), dli_saddr (nearest symbol address).
        Dl_info Info{};
        if (::dladdr(Address, &Info) == 0)
        {
            return false;
        }

        if (Info.dli_fname)
        {
            // Strip path; keep basename.
            const char* Slash = ::strrchr(Info.dli_fname, '/');
            const char* Base  = Slash ? (Slash + 1) : Info.dli_fname;
            ::std::strncpy(Out.Module, Base, sizeof(Out.Module) - 1);
        }

        if (Info.dli_sname)
        {
            ::std::strncpy(Out.Symbol, Info.dli_sname, sizeof(Out.Symbol) - 1);
        }
        else
        {
            ::std::snprintf(Out.Symbol, sizeof(Out.Symbol),
                            "?+0x%llx",
                            static_cast<unsigned long long>(
                                reinterpret_cast<::uintptr_t>(Address) -
                                reinterpret_cast<::uintptr_t>(Info.dli_fbase)));
        }

        return true;
    }

} // namespace XCore::HAL

#endif  // XPACT_LEAK_TRACKING_ENABLED && XPACT_PLATFORM_ANDROID

// =====================================================================
// TODO(Step 5 follow-up commit per Rev 3 fix m5):
//   * Replace this file's _Unwind_Backtrace + dladdr baseline with
//     NDK libunwindstack (LinkerLib + RegsArm64 + Unwinder). The
//     follow-up shipped under AndroidStackWalk_libunwindstack.cpp
//     gates this file's body behind a compile flag.
//   * Wire JIT/ART frame translation for managed (Java) frames that
//     interleave with native frames in a debug build.
//   * Acceptance: Section 17.9 I-extra -- "Android leak-tracker
//     symbol resolution within 95% of Win64 dbghelp accuracy on a
//     1000-stack-trace sample".
// =====================================================================
