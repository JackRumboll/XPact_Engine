// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// LinuxPlatformTLS.cpp -- Linux bodies for FPlatformTLS.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 + Section 13.1 fix B-C1.
//
// Wraps POSIX pthread_key_*:
//   pthread_key_create  -- allocate a key.
//   pthread_key_delete  -- release a key.
//   pthread_getspecific -- read the calling thread's pointer.
//   pthread_setspecific -- write the calling thread's pointer.
//
// pthread_key_t is typically an unsigned int on Linux; we cast to/from
// uint32_t for the cross-platform handle uniformity. Linux servers do
// not hot-reload DLLs in the MVP target so XPACT_TLS_MODULE_SAFE falls
// through to native thread_local on this platform; FPlatformTLS still
// ships for parity / test coverage.
//
// =====================================================================

#include "HAL/FPlatformTLS.h"
#include "Macros/XPactMacros.h"

#if XPACT_PLATFORM_LINUX

#include <pthread.h>

namespace XCore::HAL
{

::std::uint32_t FPlatformTLS::AllocSlot(FTLSDestructor Destructor) noexcept
{
    pthread_key_t Key{};
    // pthread_key_create's destructor signature matches FTLSDestructor
    // exactly (`void (*)(void*)`). When the destructor is null the
    // pthread runtime simply skips the call; the higher-level template
    // owns the per-thread storage lifecycle.
    const int Rc = ::pthread_key_create(&Key, Destructor);
    if (Rc != 0)
    {
        return kInvalidTLSSlot;
    }
    return static_cast<::std::uint32_t>(Key);
}

void FPlatformTLS::FreeSlot(::std::uint32_t SlotIndex) noexcept
{
    if (SlotIndex == kInvalidTLSSlot)
    {
        return;
    }
    (void)::pthread_key_delete(static_cast<pthread_key_t>(SlotIndex));
}

void* FPlatformTLS::GetValueInSlot(::std::uint32_t SlotIndex) noexcept
{
    return ::pthread_getspecific(static_cast<pthread_key_t>(SlotIndex));
}

void FPlatformTLS::SetValueInSlot(::std::uint32_t SlotIndex, void* Value) noexcept
{
    (void)::pthread_setspecific(static_cast<pthread_key_t>(SlotIndex), Value);
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_LINUX
