// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

#pragma once

#if (defined(__AUTORTFM) && __AUTORTFM)

#include "Utils.h"

namespace AutoRTFM
{

template <typename T>
class AUTORTFM_INTERNAL TScopedGuard
{
public:
	TScopedGuard(T& Ref, const T& Value) : OldValue(Ref), Ref(Ref)
	{
		Ref = Value;
	}

	~TScopedGuard()
	{
		Ref = OldValue;
	}

private:
	T OldValue;
	T& Ref;
};

}  // namespace AutoRTFM

#endif  // (defined(__AUTORTFM) && __AUTORTFM)
