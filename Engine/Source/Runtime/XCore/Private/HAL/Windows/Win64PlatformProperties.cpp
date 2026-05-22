// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64PlatformProperties.cpp -- Win64 acceptance marker.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL).
//
// FPlatformProperties is a pure-constexpr struct; per-OS resolution is
// handled by `#if XPACT_PLATFORM_*` inside the header (option (a) from
// the Phase 1a TODO). This .cpp file's role is to static_assert the
// Win64 resolution at link time: a build that mis-resolves the
// XPACT_PLATFORM_* macros (e.g., a host-Win64 cross-compile that
// accidentally leaves XPACT_PLATFORM_LINUX=1 too) fails here cleanly
// rather than producing wrong values silently.
//
// =====================================================================

#include "HAL/FPlatformProperties.h"

#include "Macros/XPactMacros.h"

#if XPACT_PLATFORM_WIN64

#include <string_view>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// Compile-time acceptance: the active resolution matches Win64
// expectations.
// ---------------------------------------------------------------------

static_assert(FPlatformProperties::HasTouchInput == false,
              "Win64 acceptance: HasTouchInput must be false");

static_assert(FPlatformProperties::IsServer == false,
              "Win64 acceptance: IsServer must be false (Linux is the server target)");

static_assert(FPlatformProperties::IsCaseSensitiveFilesystem == false,
              "Win64 acceptance: IsCaseSensitiveFilesystem must be false (NTFS default)");

static_assert(FPlatformProperties::RequiresCookedData == false,
              "Win64 acceptance: RequiresCookedData must be false (editor + dev paths available)");

static_assert(::std::string_view{FPlatformProperties::PlatformName()} == ::std::string_view{"Win64"},
              "Win64 acceptance: PlatformName() must return \"Win64\"");

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_WIN64
