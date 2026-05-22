// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidPlatformProperties.cpp -- Android acceptance marker.
// =====================================================================

#include "HAL/FPlatformProperties.h"

#include "Macros/XPactMacros.h"

#if XPACT_PLATFORM_ANDROID

#include <string_view>

namespace XCore::HAL
{

static_assert(FPlatformProperties::HasTouchInput == true,
              "Android acceptance: HasTouchInput must be true (Quest 3 touch / controller)");

static_assert(FPlatformProperties::IsServer == false,
              "Android acceptance: IsServer must be false (Quest 3 is client)");

static_assert(FPlatformProperties::IsCaseSensitiveFilesystem == true,
              "Android acceptance: IsCaseSensitiveFilesystem must be true (POSIX default)");

static_assert(FPlatformProperties::RequiresCookedData == true,
              "Android acceptance: RequiresCookedData must be true (no editor on device)");

static_assert(::std::string_view{FPlatformProperties::PlatformName()} == ::std::string_view{"Android"},
              "Android acceptance: PlatformName() must return \"Android\"");

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_ANDROID
