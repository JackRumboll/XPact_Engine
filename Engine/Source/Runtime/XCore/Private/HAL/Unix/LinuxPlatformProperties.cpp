// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// LinuxPlatformProperties.cpp -- Linux acceptance marker.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1. Per-OS resolution lives in
// Public/HAL/FPlatformProperties.h via `#if XPACT_PLATFORM_*`; this
// file is the link-time check that the Linux build resolved to the
// expected values.
//
// =====================================================================

#include "HAL/FPlatformProperties.h"

#include "Macros/XPactMacros.h"

#if XPACT_PLATFORM_LINUX

#include <string_view>

namespace XCore::HAL
{

static_assert(FPlatformProperties::HasTouchInput == false,
              "Linux acceptance: HasTouchInput must be false");

static_assert(FPlatformProperties::IsServer == true,
              "Linux acceptance: IsServer must be true (server target per Section 2)");

static_assert(FPlatformProperties::IsCaseSensitiveFilesystem == true,
              "Linux acceptance: IsCaseSensitiveFilesystem must be true (POSIX default)");

static_assert(FPlatformProperties::RequiresCookedData == false,
              "Linux acceptance: RequiresCookedData must be false (server runtime can load raw assets)");

static_assert(::std::string_view{FPlatformProperties::PlatformName()} == ::std::string_view{"Linux"},
              "Linux acceptance: PlatformName() must return \"Linux\"");

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_LINUX
