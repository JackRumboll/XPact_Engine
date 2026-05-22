// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidPlatformTLS.cpp -- Android bodies for FPlatformTLS.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 + Section 13.1 fix B-C1.
//
// Android Bionic exposes the standard POSIX pthread_key_* surface; this
// file is the Linux file with a different platform guard. Bionic
// supports >= 128 pthread keys; XCore-4a uses one slot per
// TModuleSafeThreadLocal instance, well within the cap.
//
// =====================================================================

#include "HAL/FPlatformTLS.h"
#include "Macros/XPactMacros.h"

#if XPACT_PLATFORM_ANDROID

#include <pthread.h>

namespace XCore::HAL
{

::std::uint32_t FPlatformTLS::AllocSlot(FTLSDestructor Destructor) noexcept
{
    pthread_key_t Key{};
    // pthread_key_create's destructor signature matches FTLSDestructor.
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

#endif // XPACT_PLATFORM_ANDROID
