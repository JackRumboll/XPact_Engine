// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

#pragma once

#if (defined(__AUTORTFM) && __AUTORTFM)

namespace AutoRTFM
{

// Enumerator used to control validity assertions for containers
// (bounds checking, etc).
enum class EContainerValidation
{
	Enabled,
	Disabled
};

}

#endif  // (defined(__AUTORTFM) && __AUTORTFM)
