// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64PlatformTLS.cpp -- Win64 bodies for FPlatformTLS.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 + Section 13.1 fix B-C1 (module-safe
// TLS) + fix B's MAJOR #2 (TLS thread-exit drain).
//
// Two underlying Win32 APIs:
//   * TlsAlloc / TlsGetValue / TlsSetValue / TlsFree -- the simple
//     family. No per-thread destructor callback.
//   * FlsAlloc / FlsGetValue / FlsSetValue / FlsFree -- the
//     "Fiber Local Storage" family. PFLS_CALLBACK_FUNCTION runs at
//     thread exit (NOT only at fiber exit; the name is historical).
//     Required to drive the FTLSBinCache::CrossThreadFlushOnExit
//     drain on thread shutdown.
//
// HANDLE-ENCODING SCHEME.
//
// AllocSlot returns a uint32_t handle. We need to track at FreeSlot /
// GetValue / SetValue time whether the underlying slot is Tls* (no
// destructor passed) or Fls* (destructor passed). Both TlsAlloc and
// FlsAlloc return small slot indices (1088-cap on Win64); the top bit
// of a 32-bit slot index is unused. We OR in 0x80000000u to flag
// "this is an Fls slot" so the FreeSlot / GetValue / SetValue paths
// can dispatch correctly.
//
// kInvalidTLSSlot is 0xFFFFFFFFu (TLS_OUT_OF_INDEXES / FLS_OUT_OF_INDEXES);
// it is distinguishable from any valid encoded slot because the encoded
// slot's low 31 bits hold a value <= 1087.
//
// =====================================================================

#include "HAL/FPlatformTLS.h"
#include "Macros/XPactMacros.h"

#if XPACT_PLATFORM_WIN64

#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN 1
#endif
#ifndef NOMINMAX
    #define NOMINMAX 1
#endif

#include <Windows.h>

namespace XCore::HAL
{

namespace
{
    // Top bit flags "Fls slot" vs "Tls slot".
    inline constexpr ::std::uint32_t kFlsSlotFlag  = 0x80000000u;
    inline constexpr ::std::uint32_t kSlotIndexMask = 0x7FFFFFFFu;

    // True if the handle is the "Fls" variant.
    inline bool IsFlsSlot(::std::uint32_t SlotIndex) noexcept
    {
        return (SlotIndex & kFlsSlotFlag) != 0u
            && SlotIndex != kInvalidTLSSlot;
    }

    inline DWORD DecodeSlot(::std::uint32_t SlotIndex) noexcept
    {
        return static_cast<DWORD>(SlotIndex & kSlotIndexMask);
    }
}

::std::uint32_t FPlatformTLS::AllocSlot(FTLSDestructor Destructor) noexcept
{
    if (Destructor == nullptr)
    {
        // Fast path: TlsAlloc with no callback.
        const DWORD Slot = ::TlsAlloc();
        if (Slot == TLS_OUT_OF_INDEXES)
        {
            return kInvalidTLSSlot;
        }
        return static_cast<::std::uint32_t>(Slot);
    }

    // Destructor path: FlsAlloc. The PFLS_CALLBACK_FUNCTION signature
    // is `VOID NTAPI Callback(PVOID lpFlsData)`; our FTLSDestructor is
    // `void (*)(void*)` with default C calling convention. Reinterpret-
    // cast is correct on Win64 (NTAPI = __stdcall, but Win64 x64 has
    // a single calling convention so __stdcall == cdecl). The static
    // assertion below pins this.
    static_assert(sizeof(FTLSDestructor) == sizeof(PFLS_CALLBACK_FUNCTION),
                  "Win64 FTLSDestructor must match PFLS_CALLBACK_FUNCTION size");
    PFLS_CALLBACK_FUNCTION Cb = reinterpret_cast<PFLS_CALLBACK_FUNCTION>(Destructor);
    const DWORD Slot = ::FlsAlloc(Cb);
    if (Slot == FLS_OUT_OF_INDEXES)
    {
        return kInvalidTLSSlot;
    }
    return static_cast<::std::uint32_t>(Slot) | kFlsSlotFlag;
}

void FPlatformTLS::FreeSlot(::std::uint32_t SlotIndex) noexcept
{
    if (SlotIndex == kInvalidTLSSlot)
    {
        return;
    }
    if (IsFlsSlot(SlotIndex))
    {
        (void)::FlsFree(DecodeSlot(SlotIndex));
    }
    else
    {
        (void)::TlsFree(DecodeSlot(SlotIndex));
    }
}

void* FPlatformTLS::GetValueInSlot(::std::uint32_t SlotIndex) noexcept
{
    if (SlotIndex == kInvalidTLSSlot)
    {
        return nullptr;
    }
    if (IsFlsSlot(SlotIndex))
    {
        return ::FlsGetValue(DecodeSlot(SlotIndex));
    }
    return ::TlsGetValue(DecodeSlot(SlotIndex));
}

void FPlatformTLS::SetValueInSlot(::std::uint32_t SlotIndex, void* Value) noexcept
{
    if (SlotIndex == kInvalidTLSSlot)
    {
        return;
    }
    if (IsFlsSlot(SlotIndex))
    {
        (void)::FlsSetValue(DecodeSlot(SlotIndex), Value);
    }
    else
    {
        (void)::TlsSetValue(DecodeSlot(SlotIndex), Value);
    }
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_WIN64
